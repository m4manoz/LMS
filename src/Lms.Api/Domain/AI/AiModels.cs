namespace Lms.Api.Domain.AI;

public enum AiFeatureType
{
    LessonSummary = 1,
    QuestionDraft = 2,
    FlashcardDraft = 3,
    TranslationDraft = 4,
    TutorExplanation = 5
}

public enum AiJobStatus
{
    Pending = 1,
    Processing = 2,
    Completed = 3,
    Failed = 4,
    DeadLetter = 5
}

public enum AiOutputStatus
{
    Draft = 1,
    Approved = 2,
    Rejected = 3
}

public enum AiReviewDecision
{
    Approved = 1,
    Rejected = 2
}

public sealed class AiJob
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid RequestedByUserId { get; set; }
    public Guid? CourseId { get; set; }
    public AiFeatureType Feature { get; set; }
    public string Instruction { get; set; } = string.Empty;
    public string OutputLanguage { get; set; } = "en";
    public AiJobStatus Status { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string InputHash { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public DateTimeOffset? NextAttemptAtUtc { get; set; }
}

public sealed class AiOutput
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid JobId { get; set; }
    public Guid? CourseId { get; set; }
    public AiFeatureType Feature { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? StructuredJson { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string InputHash { get; set; } = string.Empty;
    public AiOutputStatus Status { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ApprovedAtUtc { get; set; }
}

public sealed class AiCitation
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid AiOutputId { get; set; }
    public string SourceType { get; set; } = "lesson";
    public Guid SourceId { get; set; }
    public string SourceTitle { get; set; } = string.Empty;
    public string? Locator { get; set; }
    public string? Excerpt { get; set; }
}

public sealed class AiReview
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid AiOutputId { get; set; }
    public Guid ReviewerUserId { get; set; }
    public AiReviewDecision Decision { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
