namespace Lms.Api.Domain.Learning;

public enum ResourceType
{
    Document = 1,
    Video = 2,
    Audio = 3,
    Link = 4,
    Other = 99
}

public sealed class LearningResource
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public ResourceType Type { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? StorageKey { get; set; }
    public string? ContentType { get; set; }
    public long? SizeBytes { get; set; }
    public string? MetadataJson { get; set; }
    public Guid? UploadedByUserId { get; set; }
    public DateTimeOffset UploadedAtUtc { get; set; }
}
