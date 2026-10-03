using System.Text.Json;

namespace Lms.Api.Domain.Gradebook;

/// <summary>One step of a grade scale: scores at or above <see cref="MinPercent"/> earn <see cref="Label"/>.</summary>
public sealed record GradeBand(decimal MinPercent, string Label, decimal? Points);

/// <summary>A named mapping from a percentage to a letter or label, optionally with grade-point values.</summary>
public sealed class GradeScale
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>The scale used by courses that do not pick one. At most one per tenant.</summary>
    public bool IsDefault { get; set; }
    public string BandsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    public IReadOnlyList<GradeBand> Bands
    {
        get => JsonSerializer.Deserialize<List<GradeBand>>(BandsJson) ?? [];
        set => BandsJson = JsonSerializer.Serialize(value);
    }
}

public static class GradeScales
{
    /// <summary>Used when the organization has not defined its own default.</summary>
    public static readonly IReadOnlyList<GradeBand> BuiltIn =
    [
        new(90, "A", 4.0m), new(80, "B", 3.0m), new(70, "C", 2.0m), new(60, "D", 1.0m), new(0, "F", 0m)
    ];

    /// <summary>The highest band whose minimum the percentage reaches, or null when there is no percentage yet.</summary>
    public static GradeBand? Lookup(IEnumerable<GradeBand> bands, decimal? percent)
    {
        if (percent is null) return null;
        return bands.OrderByDescending(band => band.MinPercent).FirstOrDefault(band => percent.Value >= band.MinPercent);
    }

    /// <summary>Returns the reasons a set of bands is unusable, or an empty list.</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<GradeBand>? bands)
    {
        var problems = new List<string>();
        if (bands is null || bands.Count == 0) return ["Add at least one grade band."];
        if (bands.Count > 12) problems.Add("A scale can have at most 12 bands.");
        if (bands.Any(band => band.MinPercent is < 0 or > 100)) problems.Add("Each band's minimum must be between 0 and 100.");
        if (bands.Select(band => band.MinPercent).Distinct().Count() != bands.Count) problems.Add("Two bands cannot start at the same percentage.");
        if (bands.All(band => band.MinPercent != 0)) problems.Add("The lowest band must start at 0 so every score has a grade.");
        if (bands.Any(band => string.IsNullOrWhiteSpace(band.Label) || band.Label.Trim().Length > 20)) problems.Add("Each band needs a label of 1 to 20 characters.");
        if (bands.Select(band => band.Label.Trim().ToLowerInvariant()).Distinct().Count() != bands.Count) problems.Add("Band labels must be different from each other.");
        if (bands.Any(band => band.Points is < 0 or > 10)) problems.Add("Grade points must be between 0 and 10.");
        return problems;
    }
}

/// <summary>Per-course grading choices: which scale to use and the pass mark.</summary>
public sealed class CourseGradingSettings
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CourseId { get; set; }
    public Guid? GradeScaleId { get; set; }
    public decimal PassPercent { get; set; } = 50;
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>A weighted part of a course's grade, such as "Assignments 40%". A course with no categories is graded by total points.</summary>
public sealed class GradeCategory
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CourseId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal WeightPercent { get; set; }
    public int DisplayOrder { get; set; }
}

/// <summary>Puts an assignment or an assessment into a category. Items with no row do not count while categories are in use.</summary>
public sealed class GradeItemCategory
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CourseId { get; set; }
    /// <summary>"assignment" or "assessment".</summary>
    public string ItemKind { get; set; } = string.Empty;
    public Guid ItemId { get; set; }
    public Guid CategoryId { get; set; }
}
