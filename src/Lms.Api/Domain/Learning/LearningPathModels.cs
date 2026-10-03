namespace Lms.Api.Domain.Learning;

public sealed class LearningPath
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    // Ordered list of references (courseId, moduleId) stored as JSON
    public string? ItemsJson { get; set; }
    public string? PrerequisitesJson { get; set; }
    public TimeSpan? EstimatedDuration { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
