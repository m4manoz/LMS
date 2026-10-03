using Microsoft.AspNetCore.DataProtection;

namespace Lms.Api.Infrastructure.Videos;

/// <summary>
/// A link that lets a browser's video player fetch one video for a short time without an Authorization header
/// (a player cannot send one). The token names the organization, the video and the person, is tamper-proof and expires.
/// It is only handed out after the usual access checks.
/// </summary>
public sealed class VideoPlaybackTokens(IDataProtectionProvider protection)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private readonly ITimeLimitedDataProtector protector = protection.CreateProtector("lms.video.playback.v1").ToTimeLimitedDataProtector();

    public (string Token, DateTimeOffset ExpiresAtUtc) Create(Guid tenantId, Guid videoId, Guid userId)
    {
        var expires = DateTimeOffset.UtcNow.Add(Lifetime);
        return (protector.Protect($"{tenantId:N}|{videoId:N}|{userId:N}", expires), expires);
    }

    /// <summary>The organization, video and person in a valid token, or null for anything forged, mangled or expired.</summary>
    public (Guid TenantId, Guid VideoId, Guid UserId)? Read(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 1000) return null;
        try
        {
            var parts = protector.Unprotect(token).Split('|');
            return parts.Length == 3 && Guid.TryParseExact(parts[0], "N", out var tenant) && Guid.TryParseExact(parts[1], "N", out var video) && Guid.TryParseExact(parts[2], "N", out var user)
                ? (tenant, video, user) : null;
        }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }
}
