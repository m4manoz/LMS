namespace Lms.Api.Domain.Community;

/// <summary>A discussion thread. CourseId null means a general (tenant-wide) forum.</summary>
public sealed class ForumThread
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? CourseId { get; set; }
    public Guid AuthorUserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool IsPinned { get; set; }
    public bool IsLocked { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset LastActivityAtUtc { get; set; }
}

public sealed class ForumReply
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ThreadId { get; set; }
    public Guid AuthorUserId { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

/// <summary>A tenant-wide or course-targeted announcement.</summary>
public sealed class Announcement
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? CourseId { get; set; }
    public Guid AuthorUserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool IsPinned { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
}
