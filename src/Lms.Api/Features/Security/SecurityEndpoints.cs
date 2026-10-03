using Lms.Api.Domain.Identity;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Security;

public static class SecurityEndpoints
{
    public static void MapSecurityEndpoints(this WebApplication app)
    {
        var tenant = app.MapGroup("/api/v1/tenant/security").RequireAuthorization("tenant.authenticated");
        tenant.AddEndpointFilter(async (context, next) =>
        {
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (!tenantContext.IsResolved)
                return Results.BadRequest(new { message = "A tenant is required. Use the X-Tenant-Slug header or a tenant subdomain." });
            return await next(context);
        });
        tenant.MapGet("/permissions", () => Results.Ok(LmsPermissions.All.Order())).RequireAuthorization("tenant.role.read");
        tenant.MapGet("/audit-events", ListAuditEventsAsync).RequireAuthorization("tenant.security.read");
    }

    private static async Task<IResult> ListAuditEventsAsync(LmsDbContext db, CancellationToken cancellationToken)
    {
        var events = await db.SecurityAuditEvents.AsNoTracking().OrderByDescending(item => item.CreatedAtUtc).Take(200)
            .Select(item => new SecurityAuditResponse(item.Id, item.ActorUserId, item.Action, item.ResourceType, item.ResourceId, item.DetailsJson, item.IpAddress, item.CreatedAtUtc))
            .ToListAsync(cancellationToken);
        return Results.Ok(events);
    }
}

public sealed record SecurityAuditResponse(Guid Id, Guid? ActorUserId, string Action, string ResourceType, Guid? ResourceId, string DetailsJson, string? IpAddress, DateTimeOffset CreatedAtUtc);
