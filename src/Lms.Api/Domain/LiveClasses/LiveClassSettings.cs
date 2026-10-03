namespace Lms.Api.Domain.LiveClasses;

public static class LiveClassProviders
{
    /// <summary>Development placeholder: the join link points back at this app and no video is carried.</summary>
    public const string Local = "Local";
    /// <summary>The teacher pastes the link of a meeting made in Zoom, Google Meet, Teams or similar.</summary>
    public const string Manual = "Manual";
    /// <summary>Jitsi Meet: a free, open-source video room created for each class.</summary>
    public const string Jitsi = "Jitsi";
    /// <summary>LiveKit: the class is held in a room inside this app (camera, microphone, screen sharing).</summary>
    public const string LiveKit = "LiveKit";

    public static readonly string[] All = [Local, Manual, Jitsi, LiveKit];
    public const string DefaultJitsiBaseUrl = "https://meet.jit.si";
}

/// <summary>How an organization holds its live classes. One row per organization; no row means the placeholder provider.</summary>
public sealed class LiveClassSettings
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Provider { get; set; } = LiveClassProviders.Local;
    /// <summary>Address of the Jitsi server (the public one, or the organization's own).</summary>
    public string? JitsiBaseUrl { get; set; }
    /// <summary>The LiveKit server the browser connects to (wss://...).</summary>
    public string? LiveKitUrl { get; set; }
    public string? LiveKitApiKey { get; set; }
    /// <summary>Data Protection payload of the API secret. Never returned by the API.</summary>
    public string? LiveKitSecretProtected { get; set; }
    /// <summary>Name of a secret in the managed secret store (preferred in production); wins over the protected value.</summary>
    public string? LiveKitSecretReference { get; set; }
    public Guid? UpdatedByUserId { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
