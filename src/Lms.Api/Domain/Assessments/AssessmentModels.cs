namespace Lms.Api.Domain.Assessments;

public enum QuestionType
{
    MultipleChoice = 1,
    MultipleResponse = 2,
    TrueFalse = 3,
    ShortAnswer = 4,
    Essay = 5,
    FileUpload = 6
}

public enum AssessmentStatus
{
    Draft = 1,
    Published = 2,
    Archived = 3
}

public enum AttemptStatus
{
    InProgress = 1,
    Submitted = 2,
    Graded = 3
}

public sealed class QuestionBank
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CourseId { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid CreatedByUserId { get; set; }
}

public sealed class Question
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid QuestionBankId { get; set; }
    public QuestionType Type { get; set; }
    public string Prompt { get; set; } = string.Empty;
    public string OptionsJson { get; set; } = "[]";
    public string CorrectAnswerJson { get; set; } = "[]";
    public int Points { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class Assessment
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CourseId { get; set; }
    public Guid CourseVersionId { get; set; }
    public Guid QuestionBankId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Instructions { get; set; }
    public AssessmentStatus Status { get; set; }
    public int? TimeLimitMinutes { get; set; }
    public int AttemptLimit { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
}

public sealed class AssessmentQuestion
{
    public Guid AssessmentId { get; set; }
    public Guid QuestionId { get; set; }
    public int DisplayOrder { get; set; }
    public int Points { get; set; }
}

public sealed class AssessmentAttempt
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid AssessmentId { get; set; }
    public Guid CourseId { get; set; }
    public Guid LearnerUserId { get; set; }
    public int AttemptNumber { get; set; }
    public AttemptStatus Status { get; set; }
    public int ScorePoints { get; set; }
    public int PossiblePoints { get; set; }
    public decimal? Percentage { get; set; }
    public bool SubmittedAfterTimeLimit { get; set; }
    public string? TeacherFeedback { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? SubmittedAtUtc { get; set; }
    public DateTimeOffset? GradedAtUtc { get; set; }
}

public sealed class AssessmentAnswer
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid AttemptId { get; set; }
    public Guid QuestionId { get; set; }
    public string AnswerJson { get; set; } = "{}";
    public int ScorePoints { get; set; }
    public bool? IsCorrect { get; set; }
    public string? Feedback { get; set; }
    public DateTimeOffset AnsweredAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
