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
    /// <summary>A course rubric the grader scores this assignment with; the assignment's points are then the rubric's total.</summary>
    public Guid? RubricId { get; set; }
    /// <summary>Group work: staff place learners in groups, a member's submission counts for the whole group, and the group shares one grade.</summary>
    public bool IsGroup { get; set; }
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
    /// <summary>For group work: the group this copy of the submission belongs to. Every member has their own row (so the gradebook and task list work as for individual work); they all carry the same work and grade.</summary>
    public Guid? GroupId { get; set; }
    /// <summary>What each rubric criterion was scored (JSON), with its name and maximum copied in so later rubric changes never alter a grade.</summary>
    public string? RubricScoresJson { get; set; }
    public Guid? GradedByUserId { get; set; }
    public DateTimeOffset SubmittedAtUtc { get; set; }
    public DateTimeOffset? GradedAtUtc { get; set; }
}

public sealed class AssignmentGroup
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid AssignmentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

/// <summary>A learner belongs to at most one group per assignment.</summary>
public sealed class AssignmentGroupMember
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid AssignmentId { get; set; }
    public Guid GroupId { get; set; }
    public Guid LearnerUserId { get; set; }
}
