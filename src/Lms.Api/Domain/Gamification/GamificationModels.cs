namespace Lms.Api.Domain.Gamification;

public enum GamificationEventType
{
    LessonCompleted = 1,
    CourseCompleted = 2,
    AssessmentPassed = 3,
    StreakMilestone = 4
}

public sealed class GamificationProfile
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public int TotalPoints { get; set; }
    public int CurrentStreakDays { get; set; }
    public int LongestStreakDays { get; set; }
    public DateOnly? LastActivityDateAd { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class GamificationSettings
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int CourseCompletionPoints { get; set; } = 100;
    public int DailyPointCap { get; set; } = 500;
    public int MaxAwardsPerHour { get; set; } = 10;
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class GamificationEvent
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public GamificationEventType Type { get; set; }
    public Guid? CourseId { get; set; }
    public int Points { get; set; }
    public string Description { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; }
}

public sealed class BadgeDefinition
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int? PointsThreshold { get; set; }
    public int? StreakDays { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class UserBadge
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid BadgeDefinitionId { get; set; }
    public DateTimeOffset AwardedAtUtc { get; set; }
    public BadgeDefinition BadgeDefinition { get; set; } = null!;
}

public sealed class GamificationAbuseReview
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string Signal { get; set; } = string.Empty;
    public int EventCount { get; set; }
    public DateTimeOffset WindowStartedAtUtc { get; set; }
    public string Status { get; set; } = "Open";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ResolvedAtUtc { get; set; }
}
