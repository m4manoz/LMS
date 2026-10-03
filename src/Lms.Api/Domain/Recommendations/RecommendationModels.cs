namespace Lms.Api.Domain.Recommendations;

public sealed class RecommendationDismissal
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid LearnerUserId { get; set; }
    public Guid CourseId { get; set; }
    public string Variant { get; set; } = string.Empty;
    public DateTimeOffset DismissedAtUtc { get; set; }
}
