using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lms.Api.Domain.LiveClasses;
using Lms.Api.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;

namespace Lms.Api.Infrastructure.LiveClasses;

public sealed record LiveKitCredentials(string Url, string ApiKey, string ApiSecret);

/// <summary>What the browser needs to join a LiveKit room. The token is private to the person it was made for and expires.</summary>
public sealed record LiveKitJoin(string Url, string Token, string Room, bool CanPublish, bool IsHost);

/// <summary>Mints the access tokens LiveKit asks for. A token is a signed JWT (HS256) that names the room and what the person may do in it.</summary>
public static class LiveKitTokens
{
    public static string Create(LiveKitCredentials credentials, string room, string identity, string name, bool canPublish, bool roomAdmin, TimeSpan lifetime, DateTimeOffset? now = null)
    {
        var issued = now ?? DateTimeOffset.UtcNow;
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = credentials.ApiKey,
            sub = identity,
            name,
            nbf = issued.AddSeconds(-5).ToUnixTimeSeconds(),
            exp = issued.Add(lifetime).ToUnixTimeSeconds(),
            video = new { room, roomJoin = true, canPublish, canSubscribe = true, canPublishData = true, roomAdmin }
        }));
        var signature = Base64Url(HMACSHA256.HashData(Encoding.UTF8.GetBytes(credentials.ApiSecret), Encoding.ASCII.GetBytes($"{header}.{payload}")));
        return $"{header}.{payload}.{signature}";
    }

    /// <summary>A short-lived token that lets this system start, stop and look at recordings of rooms on the LiveKit server.</summary>
    public static string CreateRecordingToken(LiveKitCredentials credentials, TimeSpan lifetime, DateTimeOffset? now = null)
    {
        var issued = now ?? DateTimeOffset.UtcNow;
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iss = credentials.ApiKey, sub = "lms-recorder", nbf = issued.AddSeconds(-5).ToUnixTimeSeconds(), exp = issued.Add(lifetime).ToUnixTimeSeconds(), video = new { roomRecord = true } }));
        return $"{header}.{payload}.{Base64Url(HMACSHA256.HashData(Encoding.UTF8.GetBytes(credentials.ApiSecret), Encoding.ASCII.GetBytes($"{header}.{payload}")))}";
    }

    /// <summary>A short-lived token that lets this system look at one room, switch microphones off in it and close it. It cannot join the room.</summary>
    public static string CreateAdminToken(LiveKitCredentials credentials, string room, TimeSpan lifetime, DateTimeOffset? now = null)
    {
        var issued = now ?? DateTimeOffset.UtcNow;
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iss = credentials.ApiKey, sub = "lms-room-control", nbf = issued.AddSeconds(-5).ToUnixTimeSeconds(), exp = issued.Add(lifetime).ToUnixTimeSeconds(), video = new { room, roomAdmin = true } }));
        return $"{header}.{payload}.{Base64Url(HMACSHA256.HashData(Encoding.UTF8.GetBytes(credentials.ApiSecret), Encoding.ASCII.GetBytes($"{header}.{payload}")))}";
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Keeps an organization's LiveKit secret out of plain text and hands the credentials to the code that needs them.</summary>
public sealed class LiveKitCredentialStore(IDataProtectionProvider protection, IManagedSecretStore secrets)
{
    public const string Purpose = "lms.livekit.secret.v1";

    public string? Protect(string? secret) => string.IsNullOrEmpty(secret) ? null : protection.CreateProtector(Purpose).Protect(secret);

    /// <summary>The credentials, or null when the settings are incomplete or the secret cannot be read.</summary>
    public LiveKitCredentials? Resolve(LiveClassSettings? settings)
    {
        if (settings is null || string.IsNullOrWhiteSpace(settings.LiveKitUrl) || string.IsNullOrWhiteSpace(settings.LiveKitApiKey)) return null;
        string? secret = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(settings.LiveKitSecretReference)) secret = secrets.Get(settings.LiveKitSecretReference);
            else if (!string.IsNullOrWhiteSpace(settings.LiveKitSecretProtected)) secret = protection.CreateProtector(Purpose).Unprotect(settings.LiveKitSecretProtected);
        }
        catch (CryptographicException) { return null; }
        return string.IsNullOrEmpty(secret) ? null : new LiveKitCredentials(settings.LiveKitUrl, settings.LiveKitApiKey, secret);
    }
}

/// <summary>LiveKit: classes are held in a room inside this app. The join link opens the class; the room itself is joined with a token made at join time.</summary>
public sealed class LiveKitLiveClassProvider(IConfiguration configuration) : ILiveClassProvider
{
    public string Name => "livekit";
    // Recording needs LiveKit's separate Egress service, which is not wired up; recordings are attached by link for now.
    public bool CanRecord => false;

    public Task<LiveMeetingProvisioningResult> CreateMeetingAsync(LiveMeetingRequest request, CancellationToken cancellationToken)
    {
        var frontend = configuration["LiveClasses:FrontendBaseUrl"]?.TrimEnd('/') ?? "http://localhost:5173";
        var room = $"lms-{Convert.ToHexString(RandomNumberGenerator.GetBytes(10)).ToLowerInvariant()}";
        return Task.FromResult(new LiveMeetingProvisioningResult(Name, room, $"{frontend}/?liveSession={request.SessionId:D}", $"{frontend}/?liveSession={request.SessionId:D}&host=1"));
    }

    public Task<LiveRecordingProvisioningResult> CreateRecordingAsync(LiveRecordingRequest request, CancellationToken cancellationToken)
        => throw new LiveClassProviderException("Recordings of LiveKit classes are attached by link for now.");
}
