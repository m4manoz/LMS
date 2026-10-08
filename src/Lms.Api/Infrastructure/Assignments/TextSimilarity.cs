using System.Text.RegularExpressions;

namespace Lms.Api.Infrastructure.Assignments;

/// <summary>
/// Finds work that was copied between learners. Each text becomes a set of overlapping five-word phrases ("shingles"); two texts are as similar
/// as the share of the shorter one's phrases that also appear in the other, so copying a paragraph into a longer essay is caught, not diluted.
/// Wording that comes from the assignment's own instructions is ignored, so quoting the question is not copying. Everything is compared
/// locally: no text leaves the system. It finds similarity between learners' submissions; it does not search the internet.
/// </summary>
public static partial class TextSimilarity
{
    public const int ShingleSize = 5;
    /// <summary>Texts shorter than this have too few phrases to compare fairly.</summary>
    public const int MinimumWords = 25;

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:['’][\p{L}]+)?")]
    private static partial Regex WordPattern();

    public static List<string> Words(string? text)
        => string.IsNullOrWhiteSpace(text) ? [] : WordPattern().Matches(text).Select(match => match.Value.ToLowerInvariant().Replace('’', '\'')).ToList();

    /// <summary>The phrases of a text, in order of first appearance, leaving out any that also appear in <paramref name="ignore"/>.</summary>
    public static List<string> Shingles(IReadOnlyList<string> words, ISet<string>? ignore = null)
    {
        var seen = new HashSet<string>();
        var list = new List<string>();
        for (var index = 0; index + ShingleSize <= words.Count; index++)
        {
            var phrase = string.Join(' ', words.Skip(index).Take(ShingleSize));
            if (ignore is not null && ignore.Contains(phrase)) continue;
            if (seen.Add(phrase)) list.Add(phrase);
        }
        return list;
    }

    public sealed record Match(int Percent, int SharedPhrases, IReadOnlyList<string> Examples);

    /// <summary>How much of the shorter text is found in the other, with a few of the shared phrases (merged where they run on) to show why.</summary>
    public static Match Compare(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return new Match(0, 0, []);
        var other = new HashSet<string>(b);
        var shared = a.Where(other.Contains).ToList();
        var percent = (int)Math.Round(shared.Count * 100.0 / Math.Min(a.Count, b.Count));
        return new Match(Math.Min(100, percent), shared.Count, Examples(shared));
    }

    /// <summary>Joins phrases that overlap by four words into longer passages and returns the longest few.</summary>
    private static List<string> Examples(List<string> shared)
    {
        var passages = new List<List<string>>();
        foreach (var phrase in shared)
        {
            var words = phrase.Split(' ');
            var last = passages.Count > 0 ? passages[^1] : null;
            if (last is not null && last.Count >= ShingleSize - 1 && last.Skip(last.Count - (ShingleSize - 1)).SequenceEqual(words.Take(ShingleSize - 1))) last.Add(words[^1]);
            else passages.Add([.. words]);
        }
        return passages.OrderByDescending(item => item.Count).Take(3).Select(item => string.Join(' ', item.Take(40)) + (item.Count > 40 ? " …" : "")).ToList();
    }
}
