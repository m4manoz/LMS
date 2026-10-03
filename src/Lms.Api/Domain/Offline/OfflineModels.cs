namespace Lms.Api.Domain.Offline;

public enum OfflineDeviceStatus
{
    Active = 1,
    Revoked = 2
}

public sealed class OfflineDevice
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string FingerprintHash { get; set; } = string.Empty;
    public string SecretHash { get; set; } = string.Empty;
    public OfflineDeviceStatus Status { get; set; }
    public int MaxActivePackages { get; set; } = 5;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset LastSeenAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
}

public sealed class OfflinePackageLicense
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OfflineDeviceId { get; set; }
    public Guid UserId { get; set; }
    public Guid CourseId { get; set; }
    public DateTimeOffset IssuedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public int DownloadCount { get; set; }
    public DateTimeOffset? LastDownloadedAtUtc { get; set; }
}

public sealed class OfflineSyncConflict
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OfflineDeviceId { get; set; }
    public Guid UserId { get; set; }
    public Guid CourseId { get; set; }
    public Guid LessonId { get; set; }
    public DateTimeOffset ClientOccurredAtUtc { get; set; }
    public DateTimeOffset ServerUpdatedAtUtc { get; set; }
    public int ClientPositionSeconds { get; set; }
    public string ClientStatus { get; set; } = string.Empty;
    public int ServerPositionSeconds { get; set; }
    public string ServerStatus { get; set; } = string.Empty;
    public string Status { get; set; } = "Open";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ResolvedAtUtc { get; set; }
}
