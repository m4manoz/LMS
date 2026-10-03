using Lms.Api.Domain.Courses;

namespace Lms.Api.Infrastructure.Storage;

/// <summary>
/// What an uploaded file may be for each block type. The declared content type is not trusted on its own:
/// the first bytes of the file must match the format as well, so a script cannot be uploaded as an "image".
/// </summary>
public static class BlockFileRules
{
    private static readonly Dictionary<BlockType, string[]> AllowedTypes = new()
    {
        [BlockType.Image] = ["image/png", "image/jpeg", "image/gif", "image/webp"],
        [BlockType.Pdf] = ["application/pdf"],
        [BlockType.Video] = ["video/mp4", "video/webm", "video/ogg"],
        [BlockType.Audio] = ["audio/mpeg", "audio/ogg", "audio/wav", "audio/x-wav", "audio/mp4", "audio/webm"],
    };

    /// <summary>Types a browser may show inline. Everything else is always offered as a download.</summary>
    public static bool IsInlineType(string? contentType)
    {
        var declared = (contentType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
        return AllowedTypes.Values.Any(types => types.Contains(declared));
    }

    public static bool UsesFile(BlockType type) => type is BlockType.Image or BlockType.Pdf or BlockType.Video or BlockType.Audio or BlockType.Download;

    /// <summary>Returns an error message, or null when the file is acceptable for the block type.</summary>
    public static string? Validate(BlockType type, string declaredContentType, ReadOnlySpan<byte> header)
    {
        if (type == BlockType.Download) return null; // always served as an attachment, never rendered
        var declared = (declaredContentType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
        if (!AllowedTypes.TryGetValue(type, out var allowed) || !allowed.Contains(declared))
            return $"A {type.ToString().ToLowerInvariant()} block accepts: {string.Join(", ", AllowedTypes[type])}.";
        return MatchesSignature(declared, header) ? null : "The file contents do not match its declared type.";
    }

    public static bool MatchesSignature(string contentType, ReadOnlySpan<byte> h) => contentType switch
    {
        "image/png" => h.Length >= 4 && h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47,
        "image/jpeg" => h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF,
        "image/gif" => h.Length >= 4 && h[0] == (byte)'G' && h[1] == (byte)'I' && h[2] == (byte)'F' && h[3] == (byte)'8',
        "image/webp" => h.Length >= 12 && Ascii(h, 0, "RIFF") && Ascii(h, 8, "WEBP"),
        "application/pdf" => h.Length >= 4 && Ascii(h, 0, "%PDF"),
        "video/mp4" or "audio/mp4" => h.Length >= 8 && Ascii(h, 4, "ftyp"),
        "video/webm" or "audio/webm" => h.Length >= 4 && h[0] == 0x1A && h[1] == 0x45 && h[2] == 0xDF && h[3] == 0xA3,
        "video/ogg" or "audio/ogg" => h.Length >= 4 && Ascii(h, 0, "OggS"),
        "audio/mpeg" => h.Length >= 3 && ((h[0] == (byte)'I' && h[1] == (byte)'D' && h[2] == (byte)'3') || (h[0] == 0xFF && (h[1] & 0xE0) == 0xE0)),
        "audio/wav" or "audio/x-wav" => h.Length >= 12 && Ascii(h, 0, "RIFF") && Ascii(h, 8, "WAVE"),
        _ => false
    };

    private static bool Ascii(ReadOnlySpan<byte> bytes, int offset, string text)
    {
        if (bytes.Length < offset + text.Length) return false;
        for (var i = 0; i < text.Length; i++) if (bytes[offset + i] != (byte)text[i]) return false;
        return true;
    }

    /// <summary>Embeds are limited to https pages on hosts that are meant to be framed.</summary>
    public static bool IsEmbedAllowed(string? url, IEnumerable<string> allowedHosts)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
           && allowedHosts.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
}
