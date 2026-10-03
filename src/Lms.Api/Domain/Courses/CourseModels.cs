namespace Lms.Api.Domain.Courses;

public enum CourseStatus
{
    Draft = 1,
    InReview = 2,
    Published = 3,
    Archived = 4
}

public enum CourseVersionStatus
{
    Draft = 1,
    InReview = 2,
    Published = 3,
    Archived = 4
}

public sealed class Course
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public CourseStatus Status { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid? CategoryId { get; set; }
    public DateOnly? StartDateAd { get; set; }
    public DateOnly? EndDateAd { get; set; }
    public int? Capacity { get; set; }
    public Guid? CurrentVersionId { get; set; }
    /// <summary>The in-progress next version of a published course; learners keep using CurrentVersionId until it is published.</summary>
    public Guid? DraftVersionId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
}

public sealed class CourseVersion
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CourseId { get; set; }
    public int VersionNumber { get; set; }
    public CourseVersionStatus Status { get; set; }
    public string? ChangeSummary { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
}

public sealed class CourseModule
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CourseVersionId { get; set; }
    /// <summary>The module this one was copied from in the previous version; carries access rules forward.</summary>
    public Guid? SourceModuleId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int DisplayOrder { get; set; }
}

public sealed class CourseChapter
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CourseModuleId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int DisplayOrder { get; set; }
}

public sealed class CourseLesson
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CourseModuleId { get; set; }
    /// <summary>The lesson this one was copied from in the previous version; learner progress, notes and bookmarks follow it.</summary>
    public Guid? SourceLessonId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string? ContentHtml { get; set; }
    public int DisplayOrder { get; set; }
}

public sealed class CourseTopic
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CourseLessonId { get; set; }
    public string Title { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
}

public sealed class CourseActivity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CourseLessonId { get; set; }
    public string ActivityType { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? ConfigurationJson { get; set; }
    public int DisplayOrder { get; set; }
}

public sealed class CourseCategory
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
}

public sealed class CourseTag
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
}

public sealed class CourseTagLink
{
    public Guid CourseId { get; set; }
    public Guid TagId { get; set; }
    public Guid TenantId { get; set; }
}

public sealed class ContentAsset
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CourseId { get; set; }
    public Guid? CourseVersionId { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string StorageKey { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class CourseWorkflowEvent
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CourseId { get; set; }
    public Guid ActorUserId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public CourseStatus? FromStatus { get; set; }
    public CourseStatus ToStatus { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
