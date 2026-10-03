using System.Security.Claims;
using Lms.Api.Domain.Courses;
using Lms.Api.Domain.Identity;
using Lms.Api.Domain.Learning;
using Lms.Api.Infrastructure.CourseAccess;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Notifications;
using Lms.Api.Infrastructure.Gamification;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Learning;

public static class LearningEndpoints
{
    public static void MapLearningEndpoints(this WebApplication app)
    {
        var tenant = app.MapGroup("/api/v1/tenant").RequireAuthorization("tenant.authenticated");
        tenant.AddEndpointFilter(async (context, next) =>
        {
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (!tenantContext.IsResolved)
                return Results.BadRequest(new { message = "A tenant is required. Use the X-Tenant-Slug header or a tenant subdomain." });
            return await next(context);
        });

        tenant.MapGet("/enrollments", ListEnrollmentsAsync).RequireAuthorization("tenant.enrollment.read");
        tenant.MapPost("/enrollments", CreateEnrollmentAsync).RequireAuthorization("tenant.enrollment.manage");
        tenant.MapPost("/courses/{courseId:guid}/enroll", SelfEnrollAsync).RequireAuthorization("tenant.enrollment.read");
        tenant.MapGet("/courses/{courseId:guid}/learning", GetPlayerAsync).RequireAuthorization("tenant.enrollment.read");
        tenant.MapPost("/courses/{courseId:guid}/learning/lessons/{lessonId:guid}/progress", RecordLessonProgressAsync).RequireAuthorization("tenant.progress.manage");
        tenant.MapGet("/courses/{courseId:guid}/learning/bookmarks", ListBookmarksAsync).RequireAuthorization("tenant.progress.read");
        tenant.MapPost("/courses/{courseId:guid}/learning/bookmarks", CreateBookmarkAsync).RequireAuthorization("tenant.progress.manage");
        tenant.MapDelete("/courses/{courseId:guid}/learning/bookmarks/{bookmarkId:guid}", DeleteBookmarkAsync).RequireAuthorization("tenant.progress.manage");
        tenant.MapGet("/courses/{courseId:guid}/learning/notes", ListNotesAsync).RequireAuthorization("tenant.progress.read");
        tenant.MapPost("/courses/{courseId:guid}/learning/notes", CreateNoteAsync).RequireAuthorization("tenant.progress.manage");
        tenant.MapPut("/courses/{courseId:guid}/learning/notes/{noteId:guid}", UpdateNoteAsync).RequireAuthorization("tenant.progress.manage");
    }

    private static async Task<IResult> ListEnrollmentsAsync(HttpContext httpContext, LmsDbContext db, CancellationToken cancellationToken)
    {
        var userId = GetUserId(httpContext);
        if (userId is not Guid currentUserId) return Results.Unauthorized();
        var canManage = HasPermission(httpContext, LmsPermissions.EnrollmentManage);
        var query = db.Enrollments.AsNoTracking();
        if (!canManage) query = query.Where(item => item.LearnerUserId == currentUserId);
        var enrollments = await query.OrderByDescending(item => item.EnrolledAtUtc).ToListAsync(cancellationToken);
        var courseIds = enrollments.Select(item => item.CourseId).Distinct().ToArray();
        var courses = await db.Courses.AsNoTracking().Where(item => courseIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, cancellationToken);
        return Results.Ok(enrollments.Where(item => courses.ContainsKey(item.CourseId)).Select(item => ToEnrollmentResponse(item, courses[item.CourseId])).ToArray());
    }

    private static async Task<IResult> CreateEnrollmentAsync(
        HttpContext httpContext,
        LmsDbContext db,
        NotificationService notifications,
        EnrollmentService enrollments,
        ITenantContext tenantContext,
        CreateEnrollmentRequest request,
        CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || request.LearnerUserId == Guid.Empty) return Results.BadRequest(new { message = "Tenant and learner are required." });
        var course = await db.Courses.SingleOrDefaultAsync(item => item.Id == request.CourseId && item.Status == CourseStatus.Published, cancellationToken);
        if (course is null) return Results.NotFound(new { message = "A published course was not found." });
        if (!await db.TenantMemberships.AnyAsync(item => item.UserId == request.LearnerUserId && item.Status == MembershipStatus.Active, cancellationToken))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["learnerUserId"] = ["The learner must be an active member of the tenant."] });
        var existing = await db.Enrollments.SingleOrDefaultAsync(item => item.CourseId == request.CourseId && item.LearnerUserId == request.LearnerUserId, cancellationToken);
        if (existing is not null) return Results.Conflict(new { message = "This learner is already enrolled in the course.", enrollmentId = existing.Id });
        if (!request.OverridePrerequisites)
        {
            var missing = await enrollments.MissingPrerequisitesAsync(db, course.Id, request.LearnerUserId, cancellationToken);
            if (missing.Count > 0) return Results.Conflict(new { message = $"The learner has not completed: {string.Join(", ", missing)}. Set overridePrerequisites to enroll anyway.", missingPrerequisites = missing });
        }
        var status = ParseEnrollmentStatus(request.Status) ?? EnrollmentStatus.Active;
        var now = DateTimeOffset.UtcNow;
        var enrollment = new Enrollment
        {
            Id = Guid.NewGuid(), TenantId = tenantId, CourseId = course.Id, LearnerUserId = request.LearnerUserId,
            Status = status, Source = EnrollmentSource.Administrator, StartDateAd = request.StartDateAd,
            EndDateAd = request.EndDateAd, EnrolledAtUtc = now, UpdatedAtUtc = now
        };
        db.Enrollments.Add(enrollment);
        db.LearningProgressEvents.Add(new LearningProgressEvent { Id = Guid.NewGuid(), TenantId = tenantId, EnrollmentId = enrollment.Id, CourseId = course.Id, LearnerUserId = request.LearnerUserId, EventType = LearningProgressEventType.EnrollmentCreated, OccurredAtUtc = now });
        await notifications.QueueAsync(db, tenantId, request.LearnerUserId, "ENROLLMENT_CREATED", new Dictionary<string, string> { ["CourseTitle"] = course.Title }, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/enrollments/{enrollment.Id:D}", ToEnrollmentResponse(enrollment, course));
    }

    private static async Task<IResult> SelfEnrollAsync(HttpContext httpContext, LmsDbContext db, ITenantContext tenantContext, EnrollmentService enrollments, Guid courseId, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || GetUserId(httpContext) is not Guid learnerUserId) return Results.Unauthorized();
        var course = await db.Courses.SingleOrDefaultAsync(item => item.Id == courseId && item.Status == CourseStatus.Published, cancellationToken);
        if (course is null) return Results.NotFound(new { message = "A published course was not found." });

        var result = await enrollments.EnrollAsync(db, tenantId, course, learnerUserId, EnrollmentSource.SelfService, checkPrerequisites: true, enforceDates: true, cancellationToken);
        return result.Outcome switch
        {
            EnrollOutcome.Enrolled => Results.Created($"/api/v1/tenant/enrollments/{result.Enrollment!.Id:D}", ToEnrollmentResponse(result.Enrollment, course)),
            EnrollOutcome.Waitlisted => Results.Accepted($"/api/v1/tenant/enrollments/{result.Enrollment!.Id:D}", ToEnrollmentResponse(result.Enrollment, course)),
            EnrollOutcome.AlreadyEnrolled => Results.Ok(ToEnrollmentResponse(result.Enrollment!, course)),
            EnrollOutcome.MissingPrerequisites => Results.Conflict(new { message = $"Complete {string.Join(", ", result.MissingPrerequisites)} before enrolling in this course.", missingPrerequisites = result.MissingPrerequisites }),
            _ => Results.Conflict(new { message = result.Message })
        };
    }

    private static async Task<IResult> GetPlayerAsync(HttpContext httpContext, LmsDbContext db, Guid courseId, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var learnerUserId = userId;
        if (HasPermission(httpContext, LmsPermissions.EnrollmentManage) && Guid.TryParse(httpContext.Request.Query["learnerUserId"], out var requestedLearner)) learnerUserId = requestedLearner;
        var enrollment = await db.Enrollments.SingleOrDefaultAsync(item => item.CourseId == courseId && item.LearnerUserId == learnerUserId, cancellationToken);
        if (enrollment is null || enrollment.Status is not (EnrollmentStatus.Active or EnrollmentStatus.Completed)) return Results.NotFound(new { message = "The learner is not actively enrolled in this course." });
        var course = await db.Courses.AsNoTracking().SingleOrDefaultAsync(item => item.Id == courseId && item.Status == CourseStatus.Published, cancellationToken);
        if (course is null || course.CurrentVersionId is not Guid versionId) return Results.NotFound();
        return Results.Ok(await BuildPlayerAsync(db, course, versionId, enrollment, cancellationToken));
    }

    private static async Task<IResult> RecordLessonProgressAsync(
        HttpContext httpContext,
        LmsDbContext db,
        NotificationService notifications,
        GamificationService gamification,
        Guid courseId,
        Guid lessonId,
        LessonProgressRequest request,
        CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid learnerUserId) return Results.Unauthorized();
        var enrollment = await db.Enrollments.SingleOrDefaultAsync(item => item.CourseId == courseId && item.LearnerUserId == learnerUserId, cancellationToken);
        if (enrollment is null || enrollment.Status is EnrollmentStatus.Withdrawn or EnrollmentStatus.Suspended) return Results.NotFound(new { message = "The learner is not enrolled in this course." });
        var course = await db.Courses.SingleOrDefaultAsync(item => item.Id == courseId && item.Status == CourseStatus.Published, cancellationToken);
        if (course is null || course.CurrentVersionId is not Guid versionId) return Results.NotFound();
        var lesson = await db.CourseLessons.SingleOrDefaultAsync(item => item.Id == lessonId && db.CourseModules.Any(module => module.Id == item.CourseModuleId && module.CourseVersionId == versionId), cancellationToken);
        if (lesson is null) return Results.NotFound(new { message = "The lesson is not part of the published course." });
        if (await new ModuleAccessService().LockOfAsync(db, enrollment, versionId, lesson.CourseModuleId, cancellationToken) is { } closed)
            return Results.Conflict(new { message = closed.Reason, locked = true, unlocksAtUtc = closed.UnlocksAtUtc });
        var requestedStatus = ParseLessonProgressStatus(request.Status);
        if (requestedStatus is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = ["Status must be InProgress or Completed."] });
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey) && await db.LearningProgressEvents.AnyAsync(item => item.EnrollmentId == enrollment.Id && item.IdempotencyKey == request.IdempotencyKey.Trim(), cancellationToken))
            return Results.Ok(await BuildPlayerAsync(db, course, versionId, enrollment, cancellationToken));

        var now = DateTimeOffset.UtcNow;
        var wasCourseCompleted = enrollment.Status == EnrollmentStatus.Completed;
        var progress = await db.LessonProgress.SingleOrDefaultAsync(item => item.EnrollmentId == enrollment.Id && item.LessonId == lessonId, cancellationToken);
        var wasCompleted = progress?.Status == LessonProgressStatus.Completed;
        if (progress is null)
        {
            progress = new LessonProgress { Id = Guid.NewGuid(), TenantId = enrollment.TenantId, EnrollmentId = enrollment.Id, CourseId = courseId, LearnerUserId = learnerUserId, LessonId = lessonId, Status = LessonProgressStatus.NotStarted, UpdatedAtUtc = now };
            db.LessonProgress.Add(progress);
        }
        if (progress.Status != LessonProgressStatus.Completed || requestedStatus == LessonProgressStatus.Completed)
            progress.Status = requestedStatus.Value;
        progress.PositionSeconds = Math.Max(0, request.PositionSeconds ?? progress.PositionSeconds);
        progress.LastViewedAtUtc = now;
        if (progress.Status == LessonProgressStatus.Completed) progress.CompletedAtUtc ??= now;
        progress.UpdatedAtUtc = now;
        enrollment.CurrentLessonId = lessonId;
        enrollment.LastAccessedAtUtc = now;
        enrollment.UpdatedAtUtc = now;
        var eventType = progress.Status == LessonProgressStatus.Completed ? LearningProgressEventType.LessonCompleted : progress.PositionSeconds > 0 ? LearningProgressEventType.LessonResumed : LearningProgressEventType.LessonStarted;
        db.LearningProgressEvents.Add(new LearningProgressEvent { Id = Guid.NewGuid(), TenantId = enrollment.TenantId, EnrollmentId = enrollment.Id, CourseId = courseId, LearnerUserId = learnerUserId, LessonId = lessonId, EventType = eventType, PositionSeconds = progress.PositionSeconds, IdempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey.Trim(), OccurredAtUtc = now });
        var totalLessons = await db.CourseLessons.CountAsync(item => db.CourseModules.Any(module => module.Id == item.CourseModuleId && module.CourseVersionId == versionId), cancellationToken);
        var completedLessons = await db.LessonProgress.CountAsync(item => item.EnrollmentId == enrollment.Id && item.LessonId != lessonId && item.Status == LessonProgressStatus.Completed, cancellationToken);
        if (progress.Status == LessonProgressStatus.Completed) completedLessons++;
        enrollment.ProgressPercent = totalLessons == 0 ? 0 : Math.Min(100, (int)Math.Round(completedLessons * 100d / totalLessons));
        if (totalLessons > 0 && completedLessons >= totalLessons)
        {
            enrollment.Status = EnrollmentStatus.Completed;
            enrollment.CompletedAtUtc ??= now;
            db.LearningProgressEvents.Add(new LearningProgressEvent { Id = Guid.NewGuid(), TenantId = enrollment.TenantId, EnrollmentId = enrollment.Id, CourseId = courseId, LearnerUserId = learnerUserId, EventType = LearningProgressEventType.CourseCompleted, OccurredAtUtc = now });
            await gamification.AwardCourseCompletionAsync(db, enrollment, course.Title, now, cancellationToken);
            if (!wasCourseCompleted) await notifications.QueueAsync(db, enrollment.TenantId, learnerUserId, "COURSE_COMPLETED", new Dictionary<string, string> { ["CourseTitle"] = course.Title }, cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(await BuildPlayerAsync(db, course, versionId, enrollment, cancellationToken));
    }

    private static async Task<IResult> ListBookmarksAsync(HttpContext httpContext, LmsDbContext db, Guid courseId, CancellationToken cancellationToken)
    {
        var enrollment = await GetOwnEnrollmentAsync(httpContext, db, courseId, cancellationToken);
        if (enrollment is null) return Results.NotFound();
        var bookmarks = await db.CourseBookmarks.AsNoTracking().Where(item => item.EnrollmentId == enrollment.Id).OrderByDescending(item => item.CreatedAtUtc).Select(item => new BookmarkResponse(item.Id, item.LessonId, item.Title, item.Note, item.PositionSeconds, item.CreatedAtUtc)).ToListAsync(cancellationToken);
        return Results.Ok(bookmarks);
    }

    private static async Task<IResult> CreateBookmarkAsync(HttpContext httpContext, LmsDbContext db, Guid courseId, CreateBookmarkRequest request, CancellationToken cancellationToken)
    {
        var enrollment = await GetOwnEnrollmentAsync(httpContext, db, courseId, cancellationToken);
        if (enrollment is null) return Results.NotFound();
        if (!await IsPublishedLessonAsync(db, courseId, request.LessonId, cancellationToken)) return Results.NotFound(new { message = "The lesson is not part of the published course." });
        var bookmark = new CourseBookmark { Id = Guid.NewGuid(), TenantId = enrollment.TenantId, EnrollmentId = enrollment.Id, CourseId = courseId, LearnerUserId = enrollment.LearnerUserId, LessonId = request.LessonId, Title = request.Title?.Trim(), Note = request.Note?.Trim(), PositionSeconds = Math.Max(0, request.PositionSeconds), CreatedAtUtc = DateTimeOffset.UtcNow };
        db.CourseBookmarks.Add(bookmark);
        db.LearningProgressEvents.Add(new LearningProgressEvent { Id = Guid.NewGuid(), TenantId = enrollment.TenantId, EnrollmentId = enrollment.Id, CourseId = courseId, LearnerUserId = enrollment.LearnerUserId, LessonId = request.LessonId, EventType = LearningProgressEventType.BookmarkCreated, PositionSeconds = bookmark.PositionSeconds, OccurredAtUtc = bookmark.CreatedAtUtc });
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/courses/{courseId:D}/learning/bookmarks/{bookmark.Id:D}", new BookmarkResponse(bookmark.Id, bookmark.LessonId, bookmark.Title, bookmark.Note, bookmark.PositionSeconds, bookmark.CreatedAtUtc));
    }

    private static async Task<IResult> DeleteBookmarkAsync(HttpContext httpContext, LmsDbContext db, Guid courseId, Guid bookmarkId, CancellationToken cancellationToken)
    {
        var userId = GetUserId(httpContext);
        var bookmark = await db.CourseBookmarks.SingleOrDefaultAsync(item => item.Id == bookmarkId && item.CourseId == courseId && item.LearnerUserId == userId, cancellationToken);
        if (bookmark is null) return Results.NotFound();
        db.CourseBookmarks.Remove(bookmark);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ListNotesAsync(HttpContext httpContext, LmsDbContext db, Guid courseId, CancellationToken cancellationToken)
    {
        var enrollment = await GetOwnEnrollmentAsync(httpContext, db, courseId, cancellationToken);
        if (enrollment is null) return Results.NotFound();
        var notes = await db.LearnerNotes.AsNoTracking().Where(item => item.EnrollmentId == enrollment.Id).OrderByDescending(item => item.UpdatedAtUtc).Select(item => new NoteResponse(item.Id, item.LessonId, item.Content, item.CreatedAtUtc, item.UpdatedAtUtc)).ToListAsync(cancellationToken);
        return Results.Ok(notes);
    }

    private static async Task<IResult> CreateNoteAsync(HttpContext httpContext, LmsDbContext db, Guid courseId, CreateNoteRequest request, CancellationToken cancellationToken)
    {
        var enrollment = await GetOwnEnrollmentAsync(httpContext, db, courseId, cancellationToken);
        if (enrollment is null) return Results.NotFound();
        if (!await IsPublishedLessonAsync(db, courseId, request.LessonId, cancellationToken)) return Results.NotFound(new { message = "The lesson is not part of the published course." });
        if (string.IsNullOrWhiteSpace(request.Content) || request.Content.Trim().Length > 20000) return Results.ValidationProblem(new Dictionary<string, string[]> { ["content"] = ["Note content is required and must be 20,000 characters or fewer."] });
        var now = DateTimeOffset.UtcNow;
        var note = new LearnerNote { Id = Guid.NewGuid(), TenantId = enrollment.TenantId, EnrollmentId = enrollment.Id, CourseId = courseId, LearnerUserId = enrollment.LearnerUserId, LessonId = request.LessonId, Content = request.Content.Trim(), CreatedAtUtc = now, UpdatedAtUtc = now };
        db.LearnerNotes.Add(note);
        db.LearningProgressEvents.Add(new LearningProgressEvent { Id = Guid.NewGuid(), TenantId = enrollment.TenantId, EnrollmentId = enrollment.Id, CourseId = courseId, LearnerUserId = enrollment.LearnerUserId, LessonId = request.LessonId, EventType = LearningProgressEventType.NoteCreated, OccurredAtUtc = now });
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/courses/{courseId:D}/learning/notes/{note.Id:D}", new NoteResponse(note.Id, note.LessonId, note.Content, note.CreatedAtUtc, note.UpdatedAtUtc));
    }

    private static async Task<IResult> UpdateNoteAsync(HttpContext httpContext, LmsDbContext db, Guid courseId, Guid noteId, UpdateNoteRequest request, CancellationToken cancellationToken)
    {
        var userId = GetUserId(httpContext);
        if (string.IsNullOrWhiteSpace(request.Content) || request.Content.Trim().Length > 20000) return Results.ValidationProblem(new Dictionary<string, string[]> { ["content"] = ["Note content is required and must be 20,000 characters or fewer."] });
        var note = await db.LearnerNotes.SingleOrDefaultAsync(item => item.Id == noteId && item.CourseId == courseId && item.LearnerUserId == userId, cancellationToken);
        if (note is null) return Results.NotFound();
        note.Content = request.Content.Trim();
        note.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new NoteResponse(note.Id, note.LessonId, note.Content, note.CreatedAtUtc, note.UpdatedAtUtc));
    }

    private static async Task<PlayerResponse> BuildPlayerAsync(LmsDbContext db, Course course, Guid versionId, Enrollment enrollment, CancellationToken cancellationToken)
    {
        var modules = await db.CourseModules.AsNoTracking().Where(item => item.CourseVersionId == versionId).OrderBy(item => item.DisplayOrder).ToListAsync(cancellationToken);
        var moduleIds = modules.Select(item => item.Id).ToArray();
        var lessons = await db.CourseLessons.AsNoTracking().Where(item => moduleIds.Contains(item.CourseModuleId)).OrderBy(item => item.DisplayOrder).ToListAsync(cancellationToken);
        var progress = await db.LessonProgress.AsNoTracking().Where(item => item.EnrollmentId == enrollment.Id).ToDictionaryAsync(item => item.LessonId, cancellationToken);
        var locks = await new ModuleAccessService().EvaluateAsync(db, enrollment, versionId, DateTimeOffset.UtcNow, cancellationToken);
        return new PlayerResponse(
            new PlayerCourseResponse(course.Id, course.Code, course.Title, course.Description),
            ToEnrollmentResponse(enrollment, course),
            modules.Select(module =>
            {
                var moduleLock = locks.GetValueOrDefault(module.Id) ?? new ModuleLock(false, null, null);
                return new PlayerModuleResponse(module.Id, module.Title, module.Description, module.DisplayOrder, lessons.Where(lesson => lesson.CourseModuleId == module.Id).Select(lesson =>
                {
                    progress.TryGetValue(lesson.Id, out var item);
                    // A closed module still lists its lessons, but none of their content.
                    return new PlayerLessonResponse(lesson.Id, lesson.Title, moduleLock.Locked ? null : lesson.Summary, moduleLock.Locked ? null : lesson.ContentHtml, lesson.DisplayOrder, item?.Status.ToString() ?? LessonProgressStatus.NotStarted.ToString(), item?.PositionSeconds ?? 0, item?.LastViewedAtUtc, item?.CompletedAtUtc);
                }).ToArray(), moduleLock.Locked, moduleLock.Reason, moduleLock.UnlocksAtUtc);
            }).ToArray());
    }

    private static async Task<Enrollment?> GetOwnEnrollmentAsync(HttpContext httpContext, LmsDbContext db, Guid courseId, CancellationToken cancellationToken)
    {
        var userId = GetUserId(httpContext);
        return userId is Guid learnerUserId
            ? await db.Enrollments.SingleOrDefaultAsync(item => item.CourseId == courseId && item.LearnerUserId == learnerUserId && (item.Status == EnrollmentStatus.Active || item.Status == EnrollmentStatus.Completed), cancellationToken)
            : null;
    }

    private static Task<bool> IsPublishedLessonAsync(LmsDbContext db, Guid courseId, Guid lessonId, CancellationToken cancellationToken) =>
        db.Courses.AnyAsync(course => course.Id == courseId && course.Status == CourseStatus.Published && course.CurrentVersionId != null && db.CourseLessons.Any(lesson => lesson.Id == lessonId && db.CourseModules.Any(module => module.Id == lesson.CourseModuleId && module.CourseVersionId == course.CurrentVersionId)), cancellationToken);

    private static EnrollmentResponse ToEnrollmentResponse(Enrollment enrollment, Course course) =>
        new(enrollment.Id, course.Id, course.Code, course.Title, enrollment.LearnerUserId, enrollment.Status.ToString(), enrollment.Source.ToString(), enrollment.ProgressPercent, enrollment.CurrentLessonId, enrollment.StartDateAd, enrollment.EndDateAd, enrollment.EnrolledAtUtc, enrollment.LastAccessedAtUtc, enrollment.CompletedAtUtc);

    private static EnrollmentStatus? ParseEnrollmentStatus(string? value) => Enum.TryParse<EnrollmentStatus>(value, true, out var status) && status is EnrollmentStatus.Invited or EnrollmentStatus.Waitlisted or EnrollmentStatus.Active ? status : null;

    private static async Task<bool> HasCapacityAsync(LmsDbContext db, Course course, CancellationToken cancellationToken)
    {
        if (course.Capacity is not int capacity) return true;
        var activeCount = await db.Enrollments.CountAsync(item => item.CourseId == course.Id && item.Status == EnrollmentStatus.Active, cancellationToken);
        return activeCount < capacity;
    }

    private static LessonProgressStatus? ParseLessonProgressStatus(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "inprogress" or "in_progress" or "started" => LessonProgressStatus.InProgress,
        "completed" or "complete" => LessonProgressStatus.Completed,
        _ => null
    };

    private static Guid? GetUserId(HttpContext httpContext) => Guid.TryParse(httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private static bool HasPermission(HttpContext httpContext, string permission) => httpContext.User.HasClaim("permission", permission);
}

public sealed record CreateEnrollmentRequest(Guid CourseId, Guid LearnerUserId, string? Status = null, DateOnly? StartDateAd = null, DateOnly? EndDateAd = null, bool OverridePrerequisites = false);
public sealed record LessonProgressRequest(string Status, int? PositionSeconds = null, string? IdempotencyKey = null);
public sealed record CreateBookmarkRequest(Guid LessonId, string? Title = null, string? Note = null, int PositionSeconds = 0);
public sealed record CreateNoteRequest(Guid LessonId, string Content);
public sealed record UpdateNoteRequest(string Content);
public sealed record EnrollmentResponse(Guid Id, Guid CourseId, string CourseCode, string CourseTitle, Guid LearnerUserId, string Status, string Source, int ProgressPercent, Guid? CurrentLessonId, DateOnly? StartDateAd, DateOnly? EndDateAd, DateTimeOffset EnrolledAtUtc, DateTimeOffset? LastAccessedAtUtc, DateTimeOffset? CompletedAtUtc);
public sealed record PlayerCourseResponse(Guid Id, string Code, string Title, string? Description);
public sealed record PlayerLessonResponse(Guid Id, string Title, string? Summary, string? ContentHtml, int DisplayOrder, string Status, int PositionSeconds, DateTimeOffset? LastViewedAtUtc, DateTimeOffset? CompletedAtUtc);
public sealed record PlayerModuleResponse(Guid Id, string Title, string? Description, int DisplayOrder, PlayerLessonResponse[] Lessons, bool Locked = false, string? LockReason = null, DateTimeOffset? UnlocksAtUtc = null);
public sealed record PlayerResponse(PlayerCourseResponse Course, EnrollmentResponse Enrollment, PlayerModuleResponse[] Modules);
public sealed record BookmarkResponse(Guid Id, Guid LessonId, string? Title, string? Note, int PositionSeconds, DateTimeOffset CreatedAtUtc);
public sealed record NoteResponse(Guid Id, Guid LessonId, string Content, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
