namespace Lms.Api.Domain.Learning;

public enum HistoryItemType
{
    Course = 1,
    Lesson = 2,
    Resource = 3,
    Program = 4,
    Path = 5
}

public sealed class LearningHistory
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public HistoryItemType ItemType { get; set; }
    public Guid ItemId { get; set; }
    public decimal Progress { get; set; }
    public decimal? Score { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
