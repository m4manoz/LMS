using System.Security.Cryptography;
using Lms.Api.Domain.LiveClasses;
using Lms.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Infrastructure.LiveClasses;

/// <param name="ExternalUrl">The meeting link the teacher supplied, for the Manual provider.</param>
public sealed record LiveMeetingRequest(Guid SessionId, string Title, DateTimeOffset StartAtUtc, DateTimeOffset EndAtUtc, string? ExternalUrl = null, string? JitsiBaseUrl = null);
public sealed record LiveMeetingProvisioningResult(string Provider, string MeetingId, string JoinUrl, string HostUrl);
public sealed record LiveRecordingRequest(Guid SessionId, string Title);
public sealed record LiveRecordingProvisioningResult(string Provider, string RecordingId, string RecordingUrl);

public interface ILiveClassProvider
{
    /// <summary>The value stored on a session, matching <see cref="LiveClassProviders"/> (lower-cased).</summary>
    string Name { get; }
    /// <summary>Whether this application can produce the recording itself. Otherwise the recording is made in the meeting tool and linked.</summary>
    bool CanRecord { get; }
    Task<LiveMeetingProvisioningResult> CreateMeetingAsync(LiveMeetingRequest request, CancellationToken cancellationToken);
    Task<LiveRecordingProvisioningResult> CreateRecordingAsync(LiveRecordingRequest request, CancellationToken cancellationToken);
}

/// <summary>Raised when a class cannot be created because the organization's settings or the supplied link are not usable.</summary>
public sealed class LiveClassProviderException(string message) : Exception(message);

/// <summary>Local adapter for development. The join link points back at this app and no video is carried.</summary>
public sealed class LocalLiveClassProvider(IConfiguration configuration) : ILiveClassProvider
{
    public string Name => "local";
    public bool CanRecord => true;

    public Task<LiveMeetingProvisioningResult> CreateMeetingAsync(LiveMeetingRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var frontend = configuration["LiveClasses:FrontendBaseUrl"]?.TrimEnd('/') ?? "http://localhost:5175";
        var meetingId = $"local-{request.SessionId:N}";
        return Task.FromResult(new LiveMeetingProvisioningResult(
            Name, meetingId, $"{frontend}/?liveSession={request.SessionId:D}", $"{frontend}/?liveSession={request.SessionId:D}&host=1"));
    }

    public Task<LiveRecordingProvisioningResult> CreateRecordingAsync(LiveRecordingRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var frontend = configuration["LiveClasses:FrontendBaseUrl"]?.TrimEnd('/') ?? "http://localhost:5175";
        var recordingId = $"local-recording-{request.SessionId:N}";
        return Task.FromResult(new LiveRecordingProvisioningResult(Name, recordingId, $"{frontend}/?recording={request.SessionId:D}"));
    }
}

/// <summary>The teacher makes the meeting in another tool (Zoom, Google Meet, Teams...) and pastes its link; learners are sent there.</summary>
public sealed class ManualLinkLiveClassProvider : ILiveClassProvider
{
    public string Name => "manual";
    public bool CanRecord => false;

    public Task<LiveMeetingProvisioningResult> CreateMeetingAsync(LiveMeetingRequest request, CancellationToken cancellationToken)
    {
        var link = LiveClassUrls.NormalizeHttps(request.ExternalUrl, allowInsecureHosts: false)
            ?? throw new LiveClassProviderException("Paste the meeting link (an https address) for this class.");
        return Task.FromResult(new LiveMeetingProvisioningResult(Name, "manual", link, link));
    }

    public Task<LiveRecordingProvisioningResult> CreateRecordingAsync(LiveRecordingRequest request, CancellationToken cancellationToken)
        => throw new LiveClassProviderException("Recordings are made in the meeting tool. Add the recording link once it is ready.");
}

/// <summary>Jitsi Meet: every class gets its own hard-to-guess room on the organization's Jitsi server.</summary>
public sealed class JitsiLiveClassProvider(IConfiguration configuration) : ILiveClassProvider
{
    public string Name => "jitsi";
    public bool CanRecord => false;

    public Task<LiveMeetingProvisioningResult> CreateMeetingAsync(LiveMeetingRequest request, CancellationToken cancellationToken)
    {
        var allowInsecure = configuration.GetValue("Integrations:AllowInsecureLiveClassHosts", false);
        var baseUrl = LiveClassUrls.NormalizeHttps(request.JitsiBaseUrl ?? LiveClassProviders.DefaultJitsiBaseUrl, allowInsecure)
            ?? throw new LiveClassProviderException("The Jitsi server address in the organization's settings is not valid.");
        // Anyone who knows a room name can enter it, so the name is random rather than derived from the class id.
        var room = $"lms-{Convert.ToHexString(RandomNumberGenerator.GetBytes(10)).ToLowerInvariant()}";
        var url = $"{baseUrl.TrimEnd('/')}/{room}";
        return Task.FromResult(new LiveMeetingProvisioningResult(Name, room, url, url));
    }

    public Task<LiveRecordingProvisioningResult> CreateRecordingAsync(LiveRecordingRequest request, CancellationToken cancellationToken)
        => throw new LiveClassProviderException("Jitsi recordings are made in the meeting itself. Add the recording link once it is ready.");
}

public static class LiveClassUrls
{
    /// <summary>
    /// A clean https address, or null when the value is empty, not absolute, not https (http is allowed only when told to, for local testing),
    /// carries a user name or password, or is too long. Scripts and other schemes never pass.
    /// </summary>
    public static string? NormalizeHttps(string? value, bool allowInsecureHosts)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 2000) return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return null;
        var secure = uri.Scheme == Uri.UriSchemeHttps;
        if (!secure && !(allowInsecureHosts && uri.Scheme == Uri.UriSchemeHttp)) return null;
        if (!string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrEmpty(uri.Host)) return null;
        return uri.ToString();
    }
}

/// <summary>Picks the provider an organization has chosen.</summary>
public sealed class LiveClassProviderResolver(LocalLiveClassProvider local, ManualLinkLiveClassProvider manual, JitsiLiveClassProvider jitsi, LiveKitLiveClassProvider liveKit)
{
    public async Task<(ILiveClassProvider Provider, LiveClassSettings? Settings)> ResolveAsync(LmsDbContext db, CancellationToken cancellationToken)
    {
        var settings = await db.LiveClassSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return (For(settings?.Provider), settings);
    }

    public ILiveClassProvider For(string? name) => name switch
    {
        LiveClassProviders.Manual => manual,
        LiveClassProviders.Jitsi => jitsi,
        LiveClassProviders.LiveKit => liveKit,
        _ => local
    };

    /// <summary>The provider a stored session was created with (its lower-case name), falling back to the placeholder.</summary>
    public ILiveClassProvider ForSession(string? sessionProvider) => sessionProvider?.ToLowerInvariant() switch
    {
        "manual" => manual,
        "jitsi" => jitsi,
        "livekit" => liveKit,
        _ => local
    };
}
