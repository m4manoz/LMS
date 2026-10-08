using System.Text.Json;

namespace Lms.Api.Features.Assessments;

public sealed record RubricLevel(string Label, int Points, string? Description = null);
public sealed record RubricCriterion(Guid Id, string Name, string? Description, List<RubricLevel> Levels)
{
    /// <summary>The most this criterion can earn: its best level.</summary>
    public int MaxPoints => Levels.Count == 0 ? 0 : Levels.Max(level => level.Points);
}

/// <summary>What a grader gave one criterion. The criterion's name and maximum are copied in so a later change to the rubric never alters a finished grade.</summary>
public sealed record CriterionScore(Guid CriterionId, string Name, int MaxPoints, int Points);

/// <summary>Checks, stores and totals rubrics. A rubric has up to 20 criteria of 2–8 levels each, and totals at most 100 points (a question's limit).</summary>
public static class RubricRules
{
    public const int MaxCriteria = 20;
    public const int MaxTotalPoints = 100;

    public static List<RubricCriterion> Read(string json)
    {
        try { return JsonSerializer.Deserialize<List<RubricCriterion>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    public static string Write(IEnumerable<RubricCriterion> criteria) => JsonSerializer.Serialize(criteria);

    public static int Total(IEnumerable<RubricCriterion> criteria) => criteria.Sum(item => item.MaxPoints);

    /// <summary>Cleans the criteria a person typed (trimming, giving new ones an id) and returns a message for the first problem, or null.</summary>
    public static string? Clean(IReadOnlyList<RubricCriterionInput>? input, out List<RubricCriterion> cleaned)
    {
        cleaned = [];
        if (input is null || input.Count == 0) return "Add at least one criterion.";
        if (input.Count > MaxCriteria) return $"A rubric can have at most {MaxCriteria} criteria.";
        var seen = new HashSet<Guid>();
        foreach (var item in input)
        {
            var name = item.Name?.Trim() ?? string.Empty;
            if (name.Length is 0 or > 200) return "Every criterion needs a name of up to 200 characters.";
            if (item.Description?.Trim().Length > 1000) return $"The description of “{name}” is too long.";
            if (item.Levels is null || item.Levels.Count is < 2 or > 8) return $"“{name}” needs 2 to 8 levels.";
            var levels = new List<RubricLevel>();
            foreach (var level in item.Levels)
            {
                var label = level.Label?.Trim() ?? string.Empty;
                if (label.Length is 0 or > 100) return $"Every level of “{name}” needs a label of up to 100 characters.";
                if (level.Points is < 0 or > MaxTotalPoints) return $"Level points must be from 0 to {MaxTotalPoints}.";
                if (level.Description?.Trim().Length > 500) return $"A level description of “{name}” is too long.";
                levels.Add(new RubricLevel(label, level.Points, string.IsNullOrWhiteSpace(level.Description) ? null : level.Description.Trim()));
            }
            if (levels.Select(level => level.Points).Distinct().Count() != levels.Count) return $"The levels of “{name}” must have different points.";
            var id = item.Id is { } given && given != Guid.Empty && seen.Add(given) ? given : NewId(seen);
            cleaned.Add(new RubricCriterion(id, name, string.IsNullOrWhiteSpace(item.Description) ? null : item.Description.Trim(), levels.OrderByDescending(level => level.Points).ToList()));
        }
        if (Total(cleaned) is < 1 or > MaxTotalPoints) return $"The criteria together must be worth between 1 and {MaxTotalPoints} points.";
        return null;
    }

    private static Guid NewId(HashSet<Guid> seen)
    {
        Guid id;
        do { id = Guid.NewGuid(); } while (!seen.Add(id));
        return id;
    }

    /// <summary>Checks a grader's scores against the rubric: every criterion scored once, within its maximum.</summary>
    public static string? Score(IReadOnlyList<RubricCriterion> criteria, IReadOnlyList<CriterionScoreInput>? given, out List<CriterionScore> scores)
    {
        scores = [];
        given ??= [];
        foreach (var criterion in criteria)
        {
            var matches = given.Where(item => item.CriterionId == criterion.Id).ToList();
            if (matches.Count != 1) return $"Score “{criterion.Name}” once.";
            var points = matches[0].Points;
            if (points < 0 || points > criterion.MaxPoints) return $"“{criterion.Name}” is scored from 0 to {criterion.MaxPoints}.";
            scores.Add(new CriterionScore(criterion.Id, criterion.Name, criterion.MaxPoints, points));
        }
        if (given.Any(item => criteria.All(criterion => criterion.Id != item.CriterionId))) return "A score names a criterion that is not in the rubric.";
        return null;
    }
}

public sealed record RubricLevelInput(string? Label, int Points, string? Description = null);
public sealed record RubricCriterionInput(Guid? Id, string? Name, string? Description, List<RubricLevelInput>? Levels);
public sealed record CriterionScoreInput(Guid CriterionId, int Points);
