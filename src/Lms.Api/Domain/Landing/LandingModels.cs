namespace Lms.Api.Domain.Landing;

/// <summary>What an organization shows on its public front page. One row per organization; no row means the default page.</summary>
public sealed class LandingPage
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    /// <summary>The page's words, banners and course rows as JSON (see <c>LandingContent</c>).</summary>
    public string ContentJson { get; set; } = "{}";
    public Guid? UpdatedByUserId { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>An address (such as learn.school.edu) at which an organization's own website is served. Set up by the platform operator.</summary>
public sealed class TenantDomain
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    /// <summary>Lower case, no port.</summary>
    public string Host { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public enum ApplicationStatus
{
    Pending = 1,
    Approved = 2,
    Declined = 3
}

/// <summary>Someone who asked, from the public page, to take a course. Staff approve it (which sends an invitation) or decline it.</summary>
public sealed class CourseApplication
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CourseId { get; set; }
    public string FullName { get; set; } = string.Empty;
    /// <summary>Lower-cased.</summary>
    public string Email { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Message { get; set; }
    public ApplicationStatus Status { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? DecidedAtUtc { get; set; }
    public Guid? DecidedByUserId { get; set; }
    /// <summary>The invitation sent when the application was approved.</summary>
    public Guid? InvitationId { get; set; }
}

/// <summary>A picture an organization uploaded for its public page (its logo, a banner or the main image). Only pictures: no SVG or other formats a browser could run.</summary>
public sealed class LandingImage
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string StorageKey { get; set; } = string.Empty;
    public Guid UploadedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
