namespace Lms.Api.Domain.Tenants;

public sealed class Tenant
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public TenantStatus Status { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public enum TenantStatus
{
    Active,
    Suspended,
    Archived
}

public static class TenantSlug
{
    public static string Normalize(string value) => value.Trim().ToLowerInvariant();

    public static bool IsValid(string value) =>
        value.Length is >= 3 and <= 63
        && value[0] != '-'
        && value[^1] != '-'
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
}

