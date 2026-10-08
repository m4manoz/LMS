using Lms.Api.Domain.Identity;
using Lms.Api.Domain.Tenants;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Security;
using Lms.Api.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Platform;

/// <summary>
/// Platform-level management of organizations (tenants), for the platform operator rather than for any one organization's staff.
/// Every call needs the platform key. Creating an organization and giving it a first administrator stay in Program.cs and the identity endpoints.
/// </summary>
public static class PlatformTenantEndpoints
{
    public static void MapPlatformTenantEndpoints(this WebApplication app)
    {
        var platform = app.MapGroup("/api/v1/platform").AllowAnonymous();
        platform.MapGet("/tenants", ListAsync);
        platform.MapGet("/tenants/{slug}", GetAsync);
        platform.MapPut("/tenants/{slug}", RenameAsync);
        platform.MapPost("/tenants/{slug}/suspend", (string slug, HttpContext http, IConfiguration config, LmsDbContext db, SecurityAuditService audit, CancellationToken ct)
            => ChangeStatusAsync(slug, TenantStatus.Suspended, "tenant.suspended", http, config, db, audit, ct));
        platform.MapPost("/tenants/{slug}/activate", (string slug, HttpContext http, IConfiguration config, LmsDbContext db, SecurityAuditService audit, CancellationToken ct)
            => ChangeStatusAsync(slug, TenantStatus.Active, "tenant.activated", http, config, db, audit, ct));
        platform.MapPost("/tenants/{slug}/archive", (string slug, HttpContext http, IConfiguration config, LmsDbContext db, SecurityAuditService audit, CancellationToken ct)
            => ChangeStatusAsync(slug, TenantStatus.Archived, "tenant.archived", http, config, db, audit, ct));
    }

    private static bool HasPlatformAccess(HttpContext httpContext, IConfiguration configuration)
        => !string.IsNullOrWhiteSpace(configuration["Platform:ProvisioningKey"])
           && string.Equals(configuration["Platform:ProvisioningKey"], httpContext.Request.Headers["X-Platform-Key"].FirstOrDefault(), StringComparison.Ordinal);

    private static async Task<IResult> ListAsync(string? status, string? q, HttpContext httpContext, IConfiguration configuration, LmsDbContext db, CancellationToken cancellationToken)
    {
        if (!HasPlatformAccess(httpContext, configuration)) return Results.Unauthorized();
        var tenants = db.Tenants.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<TenantStatus>(status, ignoreCase: true, out var parsed)) return Results.BadRequest(new { message = "Status must be Active, Suspended or Archived." });
            tenants = tenants.Where(item => item.Status == parsed);
        }
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLowerInvariant();
            tenants = tenants.Where(item => item.Slug.Contains(term) || item.Name.ToLower().Contains(term));
        }

        var rows = await tenants.OrderBy(item => item.Name).Take(500).ToListAsync(cancellationToken);
        var ids = rows.Select(item => item.Id).ToList();
        var members = await CountByTenantAsync(db.TenantMemberships.IgnoreQueryFilters().Where(item => ids.Contains(item.TenantId) && item.Status == MembershipStatus.Active).Select(item => item.TenantId), cancellationToken);
        var courses = await CountByTenantAsync(db.Courses.IgnoreQueryFilters().Where(item => ids.Contains(item.TenantId)).Select(item => item.TenantId), cancellationToken);
        return Results.Ok(rows.Select(item => new PlatformTenantSummary(item.Id, item.Slug, item.Name, item.Status.ToString(), item.CreatedAtUtc, item.UpdatedAtUtc,
            members.GetValueOrDefault(item.Id), courses.GetValueOrDefault(item.Id))));
    }

    private static async Task<Dictionary<Guid, int>> CountByTenantAsync(IQueryable<Guid> tenantIds, CancellationToken cancellationToken)
        => (await tenantIds.GroupBy(id => id).Select(group => new { group.Key, Count = group.Count() }).ToListAsync(cancellationToken)).ToDictionary(item => item.Key, item => item.Count);

    private static async Task<IResult> GetAsync(string slug, HttpContext httpContext, IConfiguration configuration, LmsDbContext db, S3StorageOptions storage, CancellationToken cancellationToken)
    {
        if (!HasPlatformAccess(httpContext, configuration)) return Results.Unauthorized();
        var normalized = TenantSlug.Normalize(slug);
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(item => item.Slug == normalized, cancellationToken);
        if (tenant is null) return Results.NotFound();

        var memberships = db.TenantMemberships.IgnoreQueryFilters().Where(item => item.TenantId == tenant.Id);
        var admins = await memberships.Where(item => item.Role.Code == "TENANT_ADMIN")
            .Select(item => new PlatformTenantAdmin(item.User.Id, item.User.Email, item.User.DisplayName, item.Status.ToString())).ToListAsync(cancellationToken);
        var domains = await db.TenantDomains.AsNoTracking().Where(item => item.TenantId == tenant.Id).OrderBy(item => item.Host).Select(item => item.Host).ToListAsync(cancellationToken);
        return Results.Ok(new PlatformTenantDetail(tenant.Id, tenant.Slug, tenant.Name, tenant.Status.ToString(), tenant.CreatedAtUtc, tenant.UpdatedAtUtc,
            await memberships.CountAsync(item => item.Status == MembershipStatus.Active, cancellationToken),
            await db.Courses.IgnoreQueryFilters().CountAsync(item => item.TenantId == tenant.Id, cancellationToken),
            await db.Enrollments.IgnoreQueryFilters().CountAsync(item => item.TenantId == tenant.Id, cancellationToken),
            admins, domains,
            "S3".Equals(configuration["Storage:Provider"], StringComparison.OrdinalIgnoreCase) ? storage.BucketForTenant(tenant.Id) : null,
            storage.TenantBuckets.ContainsKey(tenant.Id.ToString("D"))));
    }

    private static async Task<IResult> RenameAsync(string slug, RenameTenantRequest request, HttpContext httpContext, IConfiguration configuration, LmsDbContext db, SecurityAuditService audit, CancellationToken cancellationToken)
    {
        if (!HasPlatformAccess(httpContext, configuration)) return Results.Unauthorized();
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 200) return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Enter a name of up to 200 characters."] });
        var tenant = await db.Tenants.SingleOrDefaultAsync(item => item.Slug == TenantSlug.Normalize(slug), cancellationToken);
        if (tenant is null) return Results.NotFound();
        var previous = tenant.Name;
        tenant.Name = name;
        tenant.UpdatedAtUtc = DateTimeOffset.UtcNow;
        audit.Add(db, httpContext, tenant.Id, "tenant.renamed", "tenant", tenant.Id, new { from = previous, to = name });
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new { id = tenant.Id, tenant.Slug, tenant.Name, status = tenant.Status.ToString(), tenant.UpdatedAtUtc });
    }

    /// <summary>
    /// Suspending or archiving keeps all data. The organization stops resolving (sign-in and API calls get "Tenant is not active"), its public page closes,
    /// and everyone's refresh sessions are revoked so nobody stays signed in. Activating brings it back.
    /// </summary>
    private static async Task<IResult> ChangeStatusAsync(string slug, TenantStatus target, string action, HttpContext httpContext, IConfiguration configuration, LmsDbContext db, SecurityAuditService audit, CancellationToken cancellationToken)
    {
        if (!HasPlatformAccess(httpContext, configuration)) return Results.Unauthorized();
        var tenant = await db.Tenants.SingleOrDefaultAsync(item => item.Slug == TenantSlug.Normalize(slug), cancellationToken);
        if (tenant is null) return Results.NotFound();
        if (tenant.Status == target) return Results.Ok(new { id = tenant.Id, tenant.Slug, status = tenant.Status.ToString() });
        if (tenant.Status == TenantStatus.Archived && target == TenantStatus.Suspended)
            return Results.Conflict(new { message = "An archived organization must be activated before it can be suspended." });

        var previous = tenant.Status;
        tenant.Status = target;
        tenant.UpdatedAtUtc = DateTimeOffset.UtcNow;
        if (target != TenantStatus.Active)
        {
            var now = DateTimeOffset.UtcNow;
            var sessions = await db.RefreshSessions.IgnoreQueryFilters().Where(item => item.TenantId == tenant.Id && item.RevokedAtUtc == null).ToListAsync(cancellationToken);
            foreach (var session in sessions) session.RevokedAtUtc = now;
        }
        audit.Add(db, httpContext, tenant.Id, action, "tenant", tenant.Id, new { from = previous.ToString(), to = target.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new { id = tenant.Id, tenant.Slug, status = tenant.Status.ToString(), tenant.UpdatedAtUtc });
    }
}

public sealed record PlatformTenantSummary(Guid Id, string Slug, string Name, string Status, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, int Members, int Courses);
public sealed record PlatformTenantAdmin(Guid UserId, string Email, string DisplayName, string Status);
public sealed record PlatformTenantDetail(Guid Id, string Slug, string Name, string Status, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, int Members, int Courses, int Enrollments, IReadOnlyList<PlatformTenantAdmin> Admins, IReadOnlyList<string> Domains, string? StorageBucket = null, bool OwnBucket = false);
public sealed record RenameTenantRequest(string? Name);
