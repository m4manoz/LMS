using Lms.Api.Domain.Courses;
using Lms.Api.Domain.Learning;
using Lms.Api.Infrastructure.Notifications;
using Lms.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Infrastructure.CourseAccess;

public enum EnrollOutcome
{
    Enrolled,
    Waitlisted,
    AlreadyEnrolled,
    MissingPrerequisites,
    /// <summary>Outside the enrollment dates, or the person's enrollment is suspended.</summary>
    NotAvailable
}

public sealed record EnrollResult(EnrollOutcome Outcome, Domain.Learning.Enrollment? Enrollment, IReadOnlyList<string> MissingPrerequisites, string? Message);

/// <summary>The single place that decides how someone gets into a course: prerequisites, capacity, waitlist and promotion.</summary>
public sealed class EnrollmentService(NotificationService notifications)
{
    /// <summary>Titles of prerequisite courses the learner has not completed.</summary>
    public async Task<IReadOnlyList<string>> MissingPrerequisitesAsync(LmsDbContext db, Guid courseId, Guid learnerUserId, CancellationToken cancellationToken)
    {
        var required = await db.CoursePrerequisites.AsNoTracking().Where(item => item.CourseId == courseId).Select(item => item.RequiredCourseId).ToListAsync(cancellationToken);
        if (required.Count == 0) return [];
        var completed = await db.Enrollments.AsNoTracking()
            .Where(item => item.LearnerUserId == learnerUserId && required.Contains(item.CourseId) && item.Status == EnrollmentStatus.Completed)
            .Select(item => item.CourseId).ToListAsync(cancellationToken);
        var missing = required.Except(completed).ToList();
        return await db.Courses.AsNoTracking().Where(item => missing.Contains(item.Id)).OrderBy(item => item.Title).Select(item => item.Title).ToListAsync(cancellationToken);
    }

    public async Task<bool> HasCapacityAsync(LmsDbContext db, Course course, CancellationToken cancellationToken)
    {
        if (course.Capacity is not int capacity) return true;
        return await db.Enrollments.CountAsync(item => item.CourseId == course.Id && item.Status == EnrollmentStatus.Active, cancellationToken) < capacity;
    }

    /// <summary>
    /// Puts a learner in a course, or on its waitlist when it is full. Saves its own changes so that several calls in a row
    /// (a cohort, for example) each see the seats the previous ones used.
    /// </summary>
    public async Task<EnrollResult> EnrollAsync(LmsDbContext db, Guid tenantId, Course course, Guid learnerUserId, EnrollmentSource source, bool checkPrerequisites, bool enforceDates, CancellationToken cancellationToken)
    {
        if (enforceDates)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            if (course.StartDateAd is DateOnly start && start > today) return new(EnrollOutcome.NotAvailable, null, [], $"Enrollment opens on {start:yyyy-MM-dd}.");
            if (course.EndDateAd is DateOnly end && end < today) return new(EnrollOutcome.NotAvailable, null, [], $"Enrollment closed on {end:yyyy-MM-dd}.");
        }

        var existing = await db.Enrollments.SingleOrDefaultAsync(item => item.CourseId == course.Id && item.LearnerUserId == learnerUserId, cancellationToken);
        if (existing is not null && existing.Status is EnrollmentStatus.Active or EnrollmentStatus.Completed or EnrollmentStatus.Waitlisted)
            return new(EnrollOutcome.AlreadyEnrolled, existing, [], existing.Status == EnrollmentStatus.Waitlisted ? "Already on the waitlist." : "Already enrolled.");
        if (existing?.Status == EnrollmentStatus.Suspended) return new(EnrollOutcome.NotAvailable, existing, [], "This enrollment is suspended. Ask an administrator.");

        if (checkPrerequisites)
        {
            var missing = await MissingPrerequisitesAsync(db, course.Id, learnerUserId, cancellationToken);
            if (missing.Count > 0) return new(EnrollOutcome.MissingPrerequisites, existing, missing, $"Complete {string.Join(", ", missing)} first.");
        }

        var now = DateTimeOffset.UtcNow;
        var status = await HasCapacityAsync(db, course, cancellationToken) ? EnrollmentStatus.Active : EnrollmentStatus.Waitlisted;
        var enrollment = existing; // an invited or withdrawn record is reused so the learner keeps a single row per course
        if (enrollment is null)
        {
            enrollment = new Domain.Learning.Enrollment { Id = Guid.NewGuid(), TenantId = tenantId, CourseId = course.Id, LearnerUserId = learnerUserId, EnrolledAtUtc = now };
            db.Enrollments.Add(enrollment);
        }
        enrollment.Status = status; enrollment.Source = source; enrollment.EnrolledAtUtc = now; enrollment.UpdatedAtUtc = now;
        db.LearningProgressEvents.Add(new LearningProgressEvent { Id = Guid.NewGuid(), TenantId = tenantId, EnrollmentId = enrollment.Id, CourseId = course.Id, LearnerUserId = learnerUserId, EventType = LearningProgressEventType.EnrollmentCreated, OccurredAtUtc = now });
        await notifications.QueueAsync(db, tenantId, learnerUserId, status == EnrollmentStatus.Waitlisted ? "ENROLLMENT_WAITLISTED" : "ENROLLMENT_CREATED", new Dictionary<string, string> { ["CourseTitle"] = course.Title }, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return new(status == EnrollmentStatus.Waitlisted ? EnrollOutcome.Waitlisted : EnrollOutcome.Enrolled, enrollment, [], null);
    }

    /// <summary>
    /// Moves the longest-waiting learners into free seats, in the order they joined. Returns the enrollments that were promoted.
    /// Their enrollment date is reset to now, because content that opens a number of days after enrollment should count from the moment they got in.
    /// </summary>
    public async Task<IReadOnlyList<Domain.Learning.Enrollment>> PromoteWaitlistAsync(LmsDbContext db, Course course, int? maxCount, CancellationToken cancellationToken)
    {
        if (course.Status != CourseStatus.Published) return [];
        var free = course.Capacity is int capacity
            ? capacity - await db.Enrollments.CountAsync(item => item.CourseId == course.Id && item.Status == EnrollmentStatus.Active, cancellationToken)
            : int.MaxValue;
        var take = Math.Min(free, maxCount ?? int.MaxValue);
        if (take <= 0) return [];

        var waiting = await db.Enrollments.Where(item => item.CourseId == course.Id && item.Status == EnrollmentStatus.Waitlisted)
            .OrderBy(item => item.EnrolledAtUtc).Take(take).ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        foreach (var enrollment in waiting)
        {
            enrollment.Status = EnrollmentStatus.Active; enrollment.EnrolledAtUtc = now; enrollment.UpdatedAtUtc = now;
            db.LearningProgressEvents.Add(new LearningProgressEvent { Id = Guid.NewGuid(), TenantId = enrollment.TenantId, EnrollmentId = enrollment.Id, CourseId = course.Id, LearnerUserId = enrollment.LearnerUserId, EventType = LearningProgressEventType.EnrollmentCreated, OccurredAtUtc = now });
            await notifications.QueueAsync(db, enrollment.TenantId, enrollment.LearnerUserId, "ENROLLMENT_PROMOTED", new Dictionary<string, string> { ["CourseTitle"] = course.Title }, cancellationToken);
        }
        if (waiting.Count > 0) await db.SaveChangesAsync(cancellationToken);
        return waiting;
    }
}

public sealed record ModuleLock(bool Locked, string? Reason, DateTimeOffset? UnlocksAtUtc);

/// <summary>Works out which modules of a course are still closed to a learner (drip schedule and "finish this first" rules).</summary>
public sealed class ModuleAccessService
{
    public async Task<Dictionary<Guid, ModuleLock>> EvaluateAsync(LmsDbContext db, Domain.Learning.Enrollment enrollment, Guid versionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var modules = await db.CourseModules.AsNoTracking().Where(item => item.CourseVersionId == versionId).ToListAsync(cancellationToken);
        var moduleIds = modules.Select(item => item.Id).ToList();
        var rules = await db.ModuleAccessRules.AsNoTracking().Where(item => moduleIds.Contains(item.ModuleId)).ToListAsync(cancellationToken);
        var result = modules.ToDictionary(item => item.Id, _ => new ModuleLock(false, null, null));
        if (rules.Count == 0) return result;

        var lessons = await db.CourseLessons.AsNoTracking().Where(item => moduleIds.Contains(item.CourseModuleId)).Select(item => new { item.Id, item.CourseModuleId }).ToListAsync(cancellationToken);
        var completed = (await db.LessonProgress.AsNoTracking().Where(item => item.EnrollmentId == enrollment.Id && item.Status == LessonProgressStatus.Completed).Select(item => item.LessonId).ToListAsync(cancellationToken)).ToHashSet();

        foreach (var rule in rules)
        {
            DateTimeOffset? opensAt = null;
            if (rule.ReleaseOnUtc is DateTimeOffset on) opensAt = on;
            if (rule.ReleaseAfterDays is int days)
            {
                var afterEnrollment = enrollment.EnrolledAtUtc.AddDays(days);
                opensAt = opensAt is null || afterEnrollment > opensAt ? afterEnrollment : opensAt;
            }
            if (opensAt is DateTimeOffset when && when > now) { result[rule.ModuleId] = new ModuleLock(true, $"Opens on {when:yyyy-MM-dd}.", when); continue; }

            if (rule.RequiresModuleId is Guid requiredId && modules.Any(item => item.Id == requiredId))
            {
                var required = lessons.Where(item => item.CourseModuleId == requiredId).ToList();
                if (required.Any(item => !completed.Contains(item.Id)))
                    result[rule.ModuleId] = new ModuleLock(true, $"Finish “{modules.First(item => item.Id == requiredId).Title}” first.", null);
            }
        }
        return result;
    }

    /// <summary>The lock on one module, or null when it is open.</summary>
    public async Task<ModuleLock?> LockOfAsync(LmsDbContext db, Domain.Learning.Enrollment enrollment, Guid versionId, Guid moduleId, CancellationToken cancellationToken)
    {
        var locks = await EvaluateAsync(db, enrollment, versionId, DateTimeOffset.UtcNow, cancellationToken);
        return locks.TryGetValue(moduleId, out var found) && found.Locked ? found : null;
    }
}
