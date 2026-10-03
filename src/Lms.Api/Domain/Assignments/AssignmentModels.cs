namespace Lms.Api.Domain.Assignments;

public enum AssignmentStatus
{
    Draft = 1,
    Published = 2,
    Closed = 3
}

public enum SubmissionStatus
{
    Submitted = 1,
    Graded = 2
}

public sealed class Assignment
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CourseId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Instructions { get; set; }
    public int MaxPoints { get; set; } = 100;
    public DateTimeOffset? DueAtUtc { get; set; }
    public bool AllowLate { get; set; }
    /// <summary>Percentage (0-100) deducted from the awarded points when a submission is late.</summary>
    public int LatePenaltyPercent { get; set; }
    public AssignmentStatus Status { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
}

/// <summary>One submission per learner per assignment; a learner may resubmit until it is graded.</summary>
public sealed class AssignmentSubmission
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid AssignmentId { get; set; }
    public Guid LearnerUserId { get; set; }
    public string? TextResponse { get; set; }
    public string? FileStorageKey { get; set; }
    public string? FileName { get; set; }
    public string? FileContentType { get; set; }
    public long? FileSizeBytes { get; set; }
    public int SubmissionCount { get; set; } = 1;
    public bool IsLate { get; set; }
    public SubmissionStatus Status { get; set; }
    public decimal? ScorePoints { get; set; }
    /// <summary>Score after any late penalty.</summary>
    public decimal? FinalPoints { get; set; }
    public string? Feedback { get; set; }
    public Guid? GradedByUserId { get; set; }
    public DateTimeOffset SubmittedAtUtc { get; set; }
    public DateTimeOffset? GradedAtUtc { get; set; }
}
