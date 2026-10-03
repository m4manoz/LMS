namespace Lms.Api.Domain.Learning;

public enum EnrollmentStatus
{
    Invited = 1,
    Waitlisted = 2,
    Active = 3,
    Completed = 4,
    Suspended = 5,
    Withdrawn = 6
}

public enum EnrollmentSource
{
    SelfService = 1,
    Administrator = 2,
    Invitation = 3,
    Cohort = 4
}

public enum LessonProgressStatus
{
    NotStarted = 1,
    InProgress = 2,
    Completed = 3
}

public enum LearningProgressEventType
{
    EnrollmentCreated = 1,
    LessonStarted = 2,
    LessonResumed = 3,
    LessonCompleted = 4,
    CourseCompleted = 5,
    BookmarkCreated = 6,
    NoteCreated = 7
}

public sealed class Enrollment
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CourseId { get; set; }
    public Guid LearnerUserId { get; set; }
    public EnrollmentStatus Status { get; set; }
    public EnrollmentSource Source { get; set; }
    public DateOnly? StartDateAd { get; set; }
    public DateOnly? EndDateAd { get; set; }
    public int ProgressPercent { get; set; }
    public Guid? CurrentLessonId { get; set; }
    public DateTimeOffset EnrolledAtUtc { get; set; }
    public DateTimeOffset? LastAccessedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class LessonProgress
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid EnrollmentId { get; set; }
    public Guid CourseId { get; set; }
    public Guid LearnerUserId { get; set; }
    public Guid LessonId { get; set; }
    public LessonProgressStatus Status { get; set; }
    public int PositionSeconds { get; set; }
    public DateTimeOffset? LastViewedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class LearningProgressEvent
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid EnrollmentId { get; set; }
    public Guid CourseId { get; set; }
    public Guid LearnerUserId { get; set; }
    public Guid? LessonId { get; set; }
    public LearningProgressEventType EventType { get; set; }
    public int? PositionSeconds { get; set; }
    public string? IdempotencyKey { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}

public sealed class CourseBookmark
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid EnrollmentId { get; set; }
    public Guid CourseId { get; set; }
    public Guid LearnerUserId { get; set; }
    public Guid LessonId { get; set; }
    public string? Title { get; set; }
    public string? Note { get; set; }
    public int PositionSeconds { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class LearnerNote
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid EnrollmentId { get; set; }
    public Guid CourseId { get; set; }
    public Guid LearnerUserId { get; set; }
    public Guid LessonId { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
