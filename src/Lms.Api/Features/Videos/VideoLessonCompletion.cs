using Lms.Api.Domain.Courses;
using Lms.Api.Domain.Learning;
using Lms.Api.Domain.Videos;
using Lms.Api.Features.Learning;
using Lms.Api.Infrastructure.Gamification;
using Lms.Api.Infrastructure.Notifications;
using Lms.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Videos;

/// <summary>
/// A lesson whose author asked for it completes by itself once the learner has watched all of its videos (the ones from the video library).
/// It is called when someone finishes a video; the lesson is then completed in the same way as when the learner marks it done, so course progress,
/// completing the course, points and notices follow.
/// </summary>
public static class VideoLessonCompletion
{
    public static async Task ApplyAsync(LmsDbContext db, NotificationService notifications, GamificationService gamification, Video video, Guid userId, CancellationToken cancellationToken)
    {
        if (video.ContentAssetId is not Guid assetId) return;
        var course = await db.Courses.AsNoTracking().SingleOrDefaultAsync(item => item.Id == video.CourseId && item.Status == CourseStatus.Published, cancellationToken);
        if (course?.CurrentVersionId is not Guid versionId) return;

        var lessonIds = await (from block in db.LessonBlocks.AsNoTracking()
                               join lesson in db.CourseLessons.AsNoTracking() on block.CourseLessonId equals lesson.Id
                               join module in db.CourseModules.AsNoTracking() on lesson.CourseModuleId equals module.Id
                               where block.Type == BlockType.Video && block.ContentAssetId == assetId && lesson.CompleteWhenVideosWatched && module.CourseVersionId == versionId
                               select lesson.Id).Distinct().ToListAsync(cancellationToken);
        foreach (var lessonId in lessonIds)
        {
            var assetIds = await db.LessonBlocks.AsNoTracking().Where(item => item.CourseLessonId == lessonId && item.Type == BlockType.Video && item.ContentAssetId != null)
                .Select(item => item.ContentAssetId!.Value).Distinct().ToListAsync(cancellationToken);
            // Only videos that are in the library can be watched here; a plain file in the lesson has no way to say it was watched.
            var videoIds = await db.Videos.AsNoTracking().Where(item => item.ContentAssetId != null && assetIds.Contains(item.ContentAssetId.Value)).Select(item => item.Id).Distinct().ToListAsync(cancellationToken);
            if (videoIds.Count == 0) continue;
            var watched = await db.VideoWatches.AsNoTracking().CountAsync(item => item.UserId == userId && item.Completed && videoIds.Contains(item.VideoId), cancellationToken);
            if (watched < videoIds.Count) continue;
            if (await db.LessonProgress.AsNoTracking().AnyAsync(item => item.LessonId == lessonId && item.LearnerUserId == userId && item.Status == LessonProgressStatus.Completed, cancellationToken)) continue;
            // A person who is not a learner of the course (staff previewing it) simply gets no progress.
            await LearningEndpoints.RecordProgressAsync(db, notifications, gamification, userId, video.CourseId, lessonId, "Completed", null, null, cancellationToken);
        }
    }
}
