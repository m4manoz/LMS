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
    public DateTimeOffset? EditedAtUtc { get; set; }
}

public sealed class ForumReply
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ThreadId { get; set; }
    public Guid AuthorUserId { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? EditedAtUtc { get; set; }
}

/// <summary>What a thread or reply said before one edit. ReplyId null means the thread's own post.</summary>
public sealed class ForumEdit
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ThreadId { get; set; }
    public Guid? ReplyId { get; set; }
    public string? PreviousTitle { get; set; }
    public string PreviousBody { get; set; } = string.Empty;
    public Guid EditedByUserId { get; set; }
    public DateTimeOffset EditedAtUtc { get; set; }
}

/// <summary>A file attached to a thread's post (ReplyId null) or to one reply.</summary>
public sealed class ForumAttachment
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ThreadId { get; set; }
    public Guid? ReplyId { get; set; }
    public string StorageKey { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public Guid UploadedByUserId { get; set; }
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
