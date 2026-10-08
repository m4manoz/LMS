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
    /// <summary>A rubric the grader scores this question with (essay and file-upload questions); the question's points are the rubric's total.</summary>
    public Guid? RubricId { get; set; }
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
    /// <summary>The questions learners get: those whose link has this version. Starts at 1.</summary>
    public int CurrentVersion { get; set; } = 1;
    /// <summary>The next version being edited (a copy of the current questions) while the current one stays in use; null when there is none.</summary>
    public int? DraftVersion { get; set; }
    public bool ShuffleQuestions { get; set; }
    public bool ShuffleOptions { get; set; }
    /// <summary>Learners cannot start an attempt before this moment. Null means it is open as soon as it is published.</summary>
    public DateTimeOffset? OpensAtUtc { get; set; }
    /// <summary>The deadline: no new attempt can start after it, and an attempt under way ends at it (plus any accommodation extra time). Null means no deadline.</summary>
    public DateTimeOffset? DueAtUtc { get; set; }
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
    public int Version { get; set; } = 1;
    /// <summary>Questions with the same pool name are alternatives: each attempt draws only the pool's draw count of them.</summary>
    public string? PoolName { get; set; }
}

/// <summary>How many questions an attempt draws from the pool of that name, in one version of an assessment.</summary>
public sealed class AssessmentPool
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid AssessmentId { get; set; }
    public int Version { get; set; } = 1;
    public string Name { get; set; } = string.Empty;
    public int DrawCount { get; set; }
}

/// <summary>A reusable scoring guide for a course. Criteria are stored as JSON: [{id, name, description, levels:[{label, points}]}].</summary>
public sealed class Rubric
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CourseId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string CriteriaJson { get; set; } = "[]";
    public int TotalPoints { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>Extra time and attempts for one learner in one course (for example a learning support plan). It applies to every assessment in the course.</summary>
public sealed class LearnerAccommodation
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CourseId { get; set; }
    public Guid LearnerUserId { get; set; }
    public int ExtraTimePercent { get; set; }
    public int ExtraAttempts { get; set; }
    public string? Note { get; set; }
    public Guid UpdatedByUserId { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
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
    /// <summary>The version of the assessment's questions this attempt was started on.</summary>
    public int Version { get; set; } = 1;
    /// <summary>The questions this attempt was dealt, in order, and each choice question's option order (JSON). Null for attempts made before this existed.</summary>
    public string? PlanJson { get; set; }
    /// <summary>The time limit this attempt was given, including any accommodation. Null means the assessment's own limit.</summary>
    public int? TimeLimitMinutes { get; set; }
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
    /// <summary>What the grader scored each rubric criterion (JSON, with the criterion's name and maximum copied in so later rubric changes do not alter it).</summary>
    public string? RubricScoresJson { get; set; }
    public DateTimeOffset AnsweredAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
