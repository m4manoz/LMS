using System.Text.RegularExpressions;
using Lms.Api.Domain.Tenants;
using Lms.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Infrastructure.Tenancy;

/// <summary>
/// Works out which organization a website address belongs to. An organization can be reached at its own address
/// (a custom domain such as learn.school.edu, set up by the platform operator) or at a subdomain of the platform (school.platform.com).
/// An address that belongs to no organization is the shared portal, where people name their organization to sign in.
/// </summary>
public static partial class TenantHosts
{
    [GeneratedRegex(@"^(?=.{1,253}$)[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$")]
    private static partial Regex ValidHost();

    /// <summary>The address in lower case without a port or a trailing dot, or null when it is not a usable host name.</summary>
    public static string? Normalize(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (text.Length == 0 || text.Contains('/') || text.Contains('@') || text.Contains(' ')) return null;
        if (text.StartsWith('[')) return null;                               // IPv6 literals are never organization addresses
        var colon = text.LastIndexOf(':');
        if (colon >= 0) text = text[..colon];
        text = text.TrimEnd('.');
        return text.Length > 0 && (text == "localhost" || ValidHost().IsMatch(text)) ? text : null;
    }

    /// <summary>True for a name a custom domain may use: a real multi-part host name, not localhost and not an address.</summary>
    public static bool IsCustomDomain(string? value)
        => Normalize(value) is { } host && host != "localhost" && ValidHost().IsMatch(host) && !System.Net.IPAddress.TryParse(host, out _);

    /// <summary>The organization at this address, or null for the shared portal (or an address nobody owns).</summary>
    public static async Task<Tenant?> FindAsync(LmsDbContext db, IConfiguration configuration, string? address, CancellationToken cancellationToken)
    {
        if (Normalize(address) is not { } host || host == "localhost" || System.Net.IPAddress.TryParse(host, out _)) return null;

        var owned = await db.TenantDomains.AsNoTracking().Where(item => item.Host == host).Select(item => item.TenantId).SingleOrDefaultAsync(cancellationToken);
        if (owned != Guid.Empty)
            return await db.Tenants.AsNoTracking().SingleOrDefaultAsync(item => item.Id == owned && item.Status == TenantStatus.Active, cancellationToken);

        // A subdomain of the platform (school.platform.com) names the organization, except on addresses set aside for the portal itself (www, app...).
        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length < 3) return null;
        var portals = (configuration["Tenancy:PortalHosts"] ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(item => item.ToLowerInvariant());
        if (portals.Contains(host) || labels[0] is "www") return null;
        var slug = labels[0];
        return await db.Tenants.AsNoTracking().SingleOrDefaultAsync(item => item.Slug == slug && item.Status == TenantStatus.Active, cancellationToken);
    }
}
