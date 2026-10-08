namespace Lms.Api.Domain.Messaging;

public enum ConversationKind
{
    Direct = 1,
    Course = 2
}

/// <summary>
/// A direct conversation between exactly two people, or the single chat room of a course.
/// Direct conversations are unique per pair through <see cref="DirectKey"/>; course chats are unique per course.
/// </summary>
public sealed class Conversation
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public ConversationKind Kind { get; set; }
    public Guid? CourseId { get; set; }
    /// <summary>For direct conversations: the two user ids, ordered, joined with ':'.</summary>
    public string? DirectKey { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset LastMessageAtUtc { get; set; }
}

/// <summary>Tracks what a person has read. Course chats create a row the first time someone opens them.</summary>
public sealed class ConversationParticipant
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset JoinedAtUtc { get; set; }
    public DateTimeOffset LastReadAtUtc { get; set; }
}

public sealed class ConversationMessage
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid SenderUserId { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    /// <summary>When the sender last changed the text; null if never.</summary>
    public DateTimeOffset? EditedAtUtc { get; set; }
    /// <summary>A deleted message stays in the conversation as a marker, with its text and file removed.</summary>
    public DateTimeOffset? DeletedAtUtc { get; set; }
    public string? AttachmentKey { get; set; }
    public string? AttachmentName { get; set; }
    public string? AttachmentContentType { get; set; }
    public long? AttachmentSizeBytes { get; set; }
}
