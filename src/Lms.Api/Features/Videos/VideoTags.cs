using System.Text.RegularExpressions;

namespace Lms.Api.Features.Videos;

/// <summary>Tags are short lowercase words (letters, digits, spaces, hyphens) that staff use to group videos.</summary>
public static partial class VideoTags
{
    public const int MaxPerVideo = 10;
    public const int MaxLength = 30;

    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N} \-]*$")]
    private static partial Regex Allowed();

    /// <summary>The tags cleaned up (trimmed, lowercase, no repeats), or what is wrong with them.</summary>
    public static (string[] Tags, string? Error) Clean(IEnumerable<string?>? tags)
    {
        var cleaned = (tags ?? []).Select(item => Regex.Replace((item ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", " ")).Where(item => item.Length > 0).Distinct().ToArray();
        if (cleaned.Length > MaxPerVideo) return ([], $"A video can have at most {MaxPerVideo} tags.");
        var bad = cleaned.FirstOrDefault(item => item.Length > MaxLength || !Allowed().IsMatch(item));
        return bad is null ? (cleaned, null) : ([], $"“{(bad.Length > 40 ? bad[..40] : bad)}” is not a good tag. Use words of up to {MaxLength} letters, digits, spaces or hyphens.");
    }

    public static string Store(IEnumerable<string> tags) => tags.Any() ? $"|{string.Join('|', tags)}|" : string.Empty;
    public static string[] Read(string? stored) => (stored ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries);
    public static string Pattern(string tag) => $"|{tag}|";
}
