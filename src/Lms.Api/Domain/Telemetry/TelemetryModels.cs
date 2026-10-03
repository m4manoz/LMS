namespace Lms.Api.Domain.Telemetry;

public sealed class ProductTelemetryEvent
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? OfflineDeviceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? PropertiesJson { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}
