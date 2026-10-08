using Lms.Api.Domain.Courses;
using Lms.Api.Domain.Learning;
using Lms.Api.Infrastructure.Gamification;
using Lms.Api.Infrastructure.Notifications;
using Lms.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Infrastructure.CourseAccess;

public sealed record VersionPublishSummary(int LessonsCarriedOver, int LessonsRemoved, int LessonsAdded, int EnrollmentsUpdated, int LearnersCompleted = 0, int LearnersNotified = 0);

public static class CourseVersionRules
{
    public static async Task<Guid?> EditableVersionIdAsync(LmsDbContext db, Course course, CancellationToken cancellationToken)
    {
        if (course.Status == CourseStatus.Draft) return course.CurrentVersionId;
        if (course.Status == CourseStatus.Published && course.DraftVersionId is Guid draftId
            && await db.CourseVersions.AnyAsync(item => item.Id == draftId && item.Status == CourseVersionStatus.Draft, cancellationToken))
            return draftId;
        return null;
    }
}

/// <summary>
/// Changes to a published course are made in a copy (the draft version). Learners stay on the published version until the
/// copy is published; then their progress, notes and bookmarks are moved onto the matching lessons of the new version.
/// </summary>
public sealed class CourseVersioningService(NotificationService notifications, GamificationService gamification)
{
    /// <summary>
    /// The version whose content may be changed right now: the course's own version while the course is a draft,
    /// or the draft copy of a published course. Null when nothing is editable (in review, published without a draft, archived).
    /// </summary>
    public Task<Guid?> EditableVersionIdAsync(LmsDbContext db, Course course, CancellationToken cancellationToken) => CourseVersionRules.EditableVersionIdAsync(db, course, cancellationToken);

    /// <summary>Copies the published version into a new draft. Files are shared, not duplicated.</summary>
    public async Task<(CourseVersion? Version, string? Error)> StartDraftAsync(LmsDbContext db, Course course, Guid userId, string? summary, CancellationToken cancellationToken)
    {
        if (course.Status != CourseStatus.Published || course.CurrentVersionId is not Guid currentId) return (null, "Only a published course can get a new version.");
        if (course.DraftVersionId is not null) return (null, "This course already has a version in progress.");

        var now = DateTimeOffset.UtcNow;
        var nextNumber = (await db.CourseVersions.Where(item => item.CourseId == course.Id).MaxAsync(item => (int?)item.VersionNumber, cancellationToken) ?? 0) + 1;
        var draft = new CourseVersion
        {
            Id = Guid.NewGuid(), TenantId = course.TenantId, CourseId = course.Id, VersionNumber = nextNumber, Status = CourseVersionStatus.Draft,
            ChangeSummary = string.IsNullOrWhiteSpace(summary) ? $"Version {nextNumber}" : summary.Trim(), CreatedByUserId = userId, CreatedAtUtc = now
        };
        db.CourseVersions.Add(draft);

        var modules = await db.CourseModules.AsNoTracking().Where(item => item.CourseVersionId == currentId).OrderBy(item => item.DisplayOrder).ToListAsync(cancellationToken);
        var moduleIds = modules.Select(item => item.Id).ToList();
        var lessons = await db.CourseLessons.AsNoTracking().Where(item => moduleIds.Contains(item.CourseModuleId)).ToListAsync(cancellationToken);
        var lessonIds = lessons.Select(item => item.Id).ToList();
        var blocks = await db.LessonBlocks.AsNoTracking().Where(item => lessonIds.Contains(item.CourseLessonId)).ToListAsync(cancellationToken);

        var moduleCopies = modules.ToDictionary(item => item.Id, item => new CourseModule
        {
            Id = Guid.NewGuid(), TenantId = item.TenantId, CourseVersionId = draft.Id, SourceModuleId = item.Id,
            Title = item.Title, Description = item.Description, DisplayOrder = item.DisplayOrder
        });
        var lessonCopies = lessons.ToDictionary(item => item.Id, item => new CourseLesson
        {
            Id = Guid.NewGuid(), TenantId = item.TenantId, CourseModuleId = moduleCopies[item.CourseModuleId].Id, SourceLessonId = item.Id,
            Title = item.Title, Summary = item.Summary, ContentHtml = item.ContentHtml, DisplayOrder = item.DisplayOrder, CompleteWhenVideosWatched = item.CompleteWhenVideosWatched
        });
        db.CourseModules.AddRange(moduleCopies.Values);
        db.CourseLessons.AddRange(lessonCopies.Values);
        db.LessonBlocks.AddRange(blocks.Select(item => new LessonBlock
        {
            Id = Guid.NewGuid(), TenantId = item.TenantId, CourseLessonId = lessonCopies[item.CourseLessonId].Id, Type = item.Type, DisplayOrder = item.DisplayOrder,
            Title = item.Title, Text = item.Text, Language = item.Language, Url = item.Url, Caption = item.Caption, ContentAssetId = item.ContentAssetId,
            CreatedAtUtc = now, UpdatedAtUtc = now
        }));

        course.DraftVersionId = draft.Id;
        course.UpdatedAtUtc = now;
        return (draft, null);
    }

    /// <summary>Throws the draft copy away. The published version and every learner's progress are untouched.</summary>
    public async Task DiscardDraftAsync(LmsDbContext db, Course course, CancellationToken cancellationToken)
    {
        if (course.DraftVersionId is not Guid draftId) return;
        var moduleIds = await db.CourseModules.Where(item => item.CourseVersionId == draftId).Select(item => item.Id).ToListAsync(cancellationToken);
        var lessonIds = await db.CourseLessons.Where(item => moduleIds.Contains(item.CourseModuleId)).Select(item => item.Id).ToListAsync(cancellationToken);
        db.LessonBlocks.RemoveRange(await db.LessonBlocks.Where(item => lessonIds.Contains(item.CourseLessonId)).ToListAsync(cancellationToken));
        db.CourseLessons.RemoveRange(await db.CourseLessons.Where(item => moduleIds.Contains(item.CourseModuleId)).ToListAsync(cancellationToken));
        db.CourseModules.RemoveRange(await db.CourseModules.Where(item => item.CourseVersionId == draftId).ToListAsync(cancellationToken));
        // Files uploaded only for the draft; the stored objects are cleaned up with the other storage follow-ups.
        db.ContentAssets.RemoveRange(await db.ContentAssets.Where(item => item.CourseVersionId == draftId).ToListAsync(cancellationToken));
        var draft = await db.CourseVersions.SingleOrDefaultAsync(item => item.Id == draftId, cancellationToken);
        if (draft is not null) db.CourseVersions.Remove(draft);
        course.DraftVersionId = null;
        course.UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Makes the draft the live version and moves learner data onto it. Lessons are matched by where they were copied from,
    /// so renamed and reordered lessons keep their progress; deleted lessons simply stop counting.
    /// Completed enrollments stay completed. The caller saves.
    /// </summary>
    public async Task<VersionPublishSummary> PublishDraftAsync(LmsDbContext db, Course course, CourseVersion draft, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var oldVersionId = course.CurrentVersionId;
        var newModules = await db.CourseModules.Where(item => item.CourseVersionId == draft.Id).ToListAsync(cancellationToken);
        var newModuleIds = newModules.Select(item => item.Id).ToList();
        var newLessons = await db.CourseLessons.Where(item => newModuleIds.Contains(item.CourseModuleId)).ToListAsync(cancellationToken);
        var lessonMap = newLessons.Where(item => item.SourceLessonId != null).ToDictionary(item => item.SourceLessonId!.Value, item => item.Id);
        var moduleMap = newModules.Where(item => item.SourceModuleId != null).ToDictionary(item => item.SourceModuleId!.Value, item => item.Id);

        var oldModuleIds = oldVersionId is Guid oldId ? await db.CourseModules.Where(item => item.CourseVersionId == oldId).Select(item => item.Id).ToListAsync(cancellationToken) : [];
        var oldLessonCount = await db.CourseLessons.CountAsync(item => oldModuleIds.Contains(item.CourseModuleId), cancellationToken);

        // Learner data follows the lesson it was recorded against.
        var progress = await db.LessonProgress.Where(item => item.CourseId == course.Id).ToListAsync(cancellationToken);
        foreach (var item in progress) if (lessonMap.TryGetValue(item.LessonId, out var next)) item.LessonId = next;
        foreach (var item in await db.LearningProgressEvents.Where(item => item.CourseId == course.Id && item.LessonId != null).ToListAsync(cancellationToken))
            if (lessonMap.TryGetValue(item.LessonId!.Value, out var next)) item.LessonId = next;
        foreach (var item in await db.CourseBookmarks.Where(item => item.CourseId == course.Id).ToListAsync(cancellationToken))
            if (lessonMap.TryGetValue(item.LessonId, out var next)) item.LessonId = next;
        foreach (var item in await db.LearnerNotes.Where(item => item.CourseId == course.Id).ToListAsync(cancellationToken))
            if (lessonMap.TryGetValue(item.LessonId, out var next)) item.LessonId = next;
        foreach (var item in await db.OfflineSyncConflicts.Where(item => item.CourseId == course.Id).ToListAsync(cancellationToken))
            if (lessonMap.TryGetValue(item.LessonId, out var next)) item.LessonId = next;

        var newLessonIds = newLessons.Select(item => item.Id).ToHashSet();
        var enrollments = await db.Enrollments.Where(item => item.CourseId == course.Id).ToListAsync(cancellationToken);
        var updated = 0;
        var completedNow = new List<Enrollment>();
        foreach (var enrollment in enrollments)
        {
            if (enrollment.CurrentLessonId is Guid current) enrollment.CurrentLessonId = lessonMap.TryGetValue(current, out var next) ? next : newLessonIds.Contains(current) ? current : null;
            if (enrollment.Status != EnrollmentStatus.Active) continue;
            var done = progress.Count(item => item.EnrollmentId == enrollment.Id && item.Status == LessonProgressStatus.Completed && newLessonIds.Contains(item.LessonId));
            enrollment.ProgressPercent = newLessonIds.Count == 0 ? 0 : Math.Min(100, (int)Math.Round(done * 100d / newLessonIds.Count));
            enrollment.UpdatedAtUtc = now;
            updated++;
            // Removing lessons can leave someone with everything left already done: they finish now rather than waiting for their next save.
            if (newLessonIds.Count > 0 && done >= newLessonIds.Count)
            {
                enrollment.Status = EnrollmentStatus.Completed;
                enrollment.CompletedAtUtc ??= now;
                enrollment.ProgressPercent = 100;
                completedNow.Add(enrollment);
            }
        }

        foreach (var enrollment in completedNow)
        {
            db.LearningProgressEvents.Add(new LearningProgressEvent { Id = Guid.NewGuid(), TenantId = enrollment.TenantId, EnrollmentId = enrollment.Id, CourseId = course.Id, LearnerUserId = enrollment.LearnerUserId, EventType = LearningProgressEventType.CourseCompleted, OccurredAtUtc = now });
            await gamification.AwardCourseCompletionAsync(db, enrollment, course.Title, now, cancellationToken);
            await notifications.QueueAsync(db, enrollment.TenantId, enrollment.LearnerUserId, "COURSE_COMPLETED", new Dictionary<string, string> { ["CourseTitle"] = course.Title }, cancellationToken);
        }

        // Module opening rules are copied across, pointing at the new modules.
        var oldRules = await db.ModuleAccessRules.Where(item => oldModuleIds.Contains(item.ModuleId)).ToListAsync(cancellationToken);
        foreach (var rule in oldRules)
        {
            if (!moduleMap.TryGetValue(rule.ModuleId, out var moduleId)) continue;
            db.ModuleAccessRules.Add(new ModuleAccessRule
            {
                Id = Guid.NewGuid(), TenantId = rule.TenantId, ModuleId = moduleId, ReleaseAfterDays = rule.ReleaseAfterDays, ReleaseOnUtc = rule.ReleaseOnUtc,
                RequiresModuleId = rule.RequiresModuleId is Guid required && moduleMap.TryGetValue(required, out var mapped) ? mapped : null, UpdatedAtUtc = now
            });
        }

        // Things that hang off the version rather than the lessons come along.
        if (oldVersionId is Guid previous)
        {
            foreach (var assessment in await db.Assessments.Where(item => item.CourseId == course.Id && item.CourseVersionId == previous).ToListAsync(cancellationToken)) assessment.CourseVersionId = draft.Id;
            foreach (var asset in await db.ContentAssets.Where(item => item.CourseId == course.Id && item.CourseVersionId == previous).ToListAsync(cancellationToken)) asset.CourseVersionId = draft.Id;
            var old = await db.CourseVersions.SingleOrDefaultAsync(item => item.Id == previous, cancellationToken);
            if (old is not null) old.Status = CourseVersionStatus.Archived;
        }

        draft.Status = CourseVersionStatus.Published;
        draft.PublishedAtUtc = now;
        course.CurrentVersionId = draft.Id;
        course.DraftVersionId = null;
        course.UpdatedAtUtc = now;

        // Everyone still learning or who finished earlier hears that the course changed (those who just finished already got their own message).
        var finishedIds = completedNow.Select(item => item.Id).ToHashSet();
        var audience = enrollments.Where(item => item.Status is EnrollmentStatus.Active or EnrollmentStatus.Completed && !finishedIds.Contains(item.Id)).Select(item => item.LearnerUserId).ToList();
        var summaryText = string.IsNullOrWhiteSpace(draft.ChangeSummary) ? $"Version {draft.VersionNumber}" : draft.ChangeSummary!;
        var notified = await notifications.QueueManyAsync(db, course.TenantId, audience, "COURSE_UPDATED",
            new Dictionary<string, string> { ["CourseTitle"] = course.Title, ["ChangeSummary"] = summaryText }, $"course-version:{draft.Id:N}", cancellationToken);

        var carried = newLessons.Count(item => item.SourceLessonId != null);
        return new VersionPublishSummary(carried, Math.Max(0, oldLessonCount - carried), newLessons.Count - carried, updated, completedNow.Count, notified);
    }
}
