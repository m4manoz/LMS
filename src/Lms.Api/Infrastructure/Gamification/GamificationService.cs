using Lms.Api.Domain.Gamification;
using Lms.Api.Domain.Learning;
using Lms.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Infrastructure.Gamification;

public sealed class GamificationService
{
    private static readonly BadgeSeed[] DefaultBadges =
    [
        new("FIRST_COURSE", "First steps", "Complete your first course.", 100, null),
        new("POINTS_500", "Learning momentum", "Earn 500 learning points.", 500, null),
        new("STREAK_7", "Seven-day streak", "Learn on seven consecutive days.", null, 7)
    ];

    public async Task AwardCourseCompletionAsync(
        LmsDbContext db,
        Enrollment enrollment,
        string courseTitle,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var idempotencyKey = $"COURSE_COMPLETED:{enrollment.Id:D}";
        if (await db.GamificationEvents.AnyAsync(item => item.UserId == enrollment.LearnerUserId && item.IdempotencyKey == idempotencyKey, cancellationToken))
            return;

        var settings = await db.GamificationSettings.SingleOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            settings = new GamificationSettings { Id = Guid.NewGuid(), TenantId = enrollment.TenantId, UpdatedAtUtc = now };
            db.GamificationSettings.Add(settings);
        }
        if (!settings.IsEnabled) return;
        var hourlyWindowStart = now.AddHours(-1);
        var awardsInLastHour = await db.GamificationEvents.CountAsync(item => item.UserId == enrollment.LearnerUserId && item.Points > 0 && item.OccurredAtUtc >= hourlyWindowStart, cancellationToken);
        if (awardsInLastHour >= Math.Max(1, settings.MaxAwardsPerHour))
        {
            var hasOpenReview = await db.GamificationAbuseReviews.AnyAsync(item => item.UserId == enrollment.LearnerUserId && item.Status == "Open" && item.Signal == "course-completion-rate", cancellationToken);
            if (!hasOpenReview)
            {
                db.GamificationAbuseReviews.Add(new GamificationAbuseReview
                {
                    Id = Guid.NewGuid(), TenantId = enrollment.TenantId, UserId = enrollment.LearnerUserId,
                    Signal = "course-completion-rate", EventCount = awardsInLastHour, WindowStartedAtUtc = hourlyWindowStart,
                    Status = "Open", CreatedAtUtc = now
                });
            }
            db.GamificationEvents.Add(new GamificationEvent
            {
                Id = Guid.NewGuid(), TenantId = enrollment.TenantId, UserId = enrollment.LearnerUserId,
                Type = GamificationEventType.CourseCompleted, CourseId = enrollment.CourseId, Points = 0,
                Description = $"Completed {courseTitle}; points held for abuse review.", IdempotencyKey = idempotencyKey, OccurredAtUtc = now
            });
            return;
        }
        var todayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var pointsAwardedToday = await db.GamificationEvents.Where(item => item.UserId == enrollment.LearnerUserId && item.OccurredAtUtc >= todayStart).SumAsync(item => item.Points, cancellationToken);
        var points = Math.Min(settings.CourseCompletionPoints, Math.Max(0, settings.DailyPointCap - pointsAwardedToday));

        var profile = await db.GamificationProfiles.SingleOrDefaultAsync(item => item.UserId == enrollment.LearnerUserId, cancellationToken);
        if (profile is null)
        {
            profile = new GamificationProfile { Id = Guid.NewGuid(), TenantId = enrollment.TenantId, UserId = enrollment.LearnerUserId };
            db.GamificationProfiles.Add(profile);
        }

        var today = DateOnly.FromDateTime(now.UtcDateTime.Date);
        profile.CurrentStreakDays = profile.LastActivityDateAd == today
            ? profile.CurrentStreakDays
            : profile.LastActivityDateAd == today.AddDays(-1) ? profile.CurrentStreakDays + 1 : 1;
        profile.LongestStreakDays = Math.Max(profile.LongestStreakDays, profile.CurrentStreakDays);
        profile.LastActivityDateAd = today;
        profile.TotalPoints += points;
        profile.UpdatedAtUtc = now;

        db.GamificationEvents.Add(new GamificationEvent
        {
            Id = Guid.NewGuid(), TenantId = enrollment.TenantId, UserId = enrollment.LearnerUserId,
            Type = GamificationEventType.CourseCompleted, CourseId = enrollment.CourseId, Points = points,
            Description = points == 0 ? $"Completed {courseTitle}; daily points cap reached." : $"Completed {courseTitle}.", IdempotencyKey = idempotencyKey, OccurredAtUtc = now
        });

        var badges = await db.BadgeDefinitions.Where(item => item.IsActive &&
                ((item.PointsThreshold != null && profile.TotalPoints >= item.PointsThreshold) ||
                 (item.StreakDays != null && profile.CurrentStreakDays >= item.StreakDays)))
            .ToListAsync(cancellationToken);
        var badgeIds = badges.Select(item => item.Id).ToArray();
        var existingBadgeIds = await db.UserBadges.Where(item => item.UserId == enrollment.LearnerUserId && badgeIds.Contains(item.BadgeDefinitionId)).Select(item => item.BadgeDefinitionId).ToListAsync(cancellationToken);
        db.UserBadges.AddRange(badges.Where(item => !existingBadgeIds.Contains(item.Id)).Select(item => new UserBadge
        {
            Id = Guid.NewGuid(), TenantId = enrollment.TenantId, UserId = enrollment.LearnerUserId, BadgeDefinitionId = item.Id, AwardedAtUtc = now
        }));
    }

    public static async Task EnsureDefaultBadgesForAllTenantsAsync(LmsDbContext db, CancellationToken cancellationToken)
    {
        var tenantIds = await db.Tenants.Select(item => item.Id).ToListAsync(cancellationToken);
        foreach (var tenantId in tenantIds) await EnsureDefaultBadgesAsync(db, tenantId, cancellationToken);
    }

    public static async Task EnsureDefaultSettingsForAllTenantsAsync(LmsDbContext db, CancellationToken cancellationToken)
    {
        var tenantIds = await db.Tenants.Select(item => item.Id).ToListAsync(cancellationToken);
        foreach (var tenantId in tenantIds) await EnsureDefaultSettingsAsync(db, tenantId, cancellationToken);
    }

    public static async Task EnsureDefaultSettingsAsync(LmsDbContext db, Guid tenantId, CancellationToken cancellationToken)
    {
        if (await db.GamificationSettings.IgnoreQueryFilters().AnyAsync(item => item.TenantId == tenantId, cancellationToken)) return;
        db.GamificationSettings.Add(new GamificationSettings { Id = Guid.NewGuid(), TenantId = tenantId, UpdatedAtUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(cancellationToken);
    }

    public static async Task EnsureDefaultBadgesAsync(LmsDbContext db, Guid tenantId, CancellationToken cancellationToken)
    {
        foreach (var seed in DefaultBadges)
        {
            if (await db.BadgeDefinitions.IgnoreQueryFilters().AnyAsync(item => item.TenantId == tenantId && item.Code == seed.Code, cancellationToken)) continue;
            db.BadgeDefinitions.Add(new BadgeDefinition
            {
                Id = Guid.NewGuid(), TenantId = tenantId, Code = seed.Code, Name = seed.Name,
                Description = seed.Description, PointsThreshold = seed.PointsThreshold, StreakDays = seed.StreakDays,
                IsActive = true, CreatedAtUtc = DateTimeOffset.UtcNow
            });
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private sealed record BadgeSeed(string Code, string Name, string Description, int? PointsThreshold, int? StreakDays);
}
