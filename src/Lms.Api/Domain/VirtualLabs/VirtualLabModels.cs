namespace Lms.Api.Domain.VirtualLabs;

public enum VirtualLabStatus
{
    Draft = 1,
    Active = 2,
    Archived = 3
}

public sealed class VirtualLab
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string ProviderType { get; set; } = string.Empty;
    public string? LaunchUrl { get; set; }
    public VirtualLabStatus Status { get; set; }
    public Guid CreatedByUserId { get; set; }
    public string HealthStatus { get; set; } = "Unknown";
    public DateTimeOffset? LastHealthCheckUtc { get; set; }
    public string? LastHealthError { get; set; }
    public string? WebhookSecretProtected { get; set; }
    public string? WebhookSecretReference { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class VirtualLabResult
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid VirtualLabId { get; set; }
    public Guid UserId { get; set; }
    public string ExternalAttemptId { get; set; } = string.Empty;
    public decimal? ScorePercent { get; set; }
    public bool Completed { get; set; }
    public string? PayloadJson { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
}

public sealed class VirtualLabHealthCheck
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid VirtualLabId { get; set; }
    public string Status { get; set; } = string.Empty;
    public int? LatencyMilliseconds { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CheckedAtUtc { get; set; }
}

public sealed class VirtualLabWebhookEvent
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid VirtualLabId { get; set; }
    public string ExternalEventId { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
    public string Status { get; set; } = "Received";
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; } = 5;
    public DateTimeOffset? NextAttemptAtUtc { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
    public DateTimeOffset? ProcessedAtUtc { get; set; }
}
