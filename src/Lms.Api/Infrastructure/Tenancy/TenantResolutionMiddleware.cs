using Lms.Api.Domain.Tenants;
using Lms.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Infrastructure.Tenancy;

public sealed class TenantResolutionMiddleware(
    RequestDelegate next,
    ILogger<TenantResolutionMiddleware> logger)
{
    public async Task InvokeAsync(
        HttpContext httpContext,
        ITenantContext tenantContext,
        LmsDbContext db,
        IConfiguration configuration)
    {
        if (httpContext.Request.Path.StartsWithSegments("/health")
            || httpContext.Request.Path.StartsWithSegments("/api/v1/platform"))
        {
            await next(httpContext);
            return;
        }

        var requestedSlug = GetTenantSlug(httpContext);
        if (string.IsNullOrWhiteSpace(requestedSlug))
        {
            // No header: an organization's own address (a custom domain or a subdomain) says which organization this is.
            if (await TenantHosts.FindAsync(db, configuration, httpContext.Request.Host.Host, httpContext.RequestAborted) is { } byAddress)
            {
                tenantContext.Set(byAddress.Id, byAddress.Slug);
                httpContext.Response.Headers["X-Tenant-Id"] = byAddress.Id.ToString("D");
            }
            await next(httpContext);
            return;
        }

        var normalizedSlug = TenantSlug.Normalize(requestedSlug);
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(
            item => item.Slug == normalizedSlug,
            httpContext.RequestAborted);

        if (tenant is null)
        {
            logger.LogWarning("Tenant resolution failed for slug {TenantSlug}.", normalizedSlug);
            await WriteErrorAsync(httpContext, StatusCodes.Status404NotFound, "Tenant was not found.");
            return;
        }

        if (tenant.Status != TenantStatus.Active)
        {
            await WriteErrorAsync(httpContext, StatusCodes.Status403Forbidden, "Tenant is not active.");
            return;
        }

        tenantContext.Set(tenant.Id, tenant.Slug);
        httpContext.Response.Headers["X-Tenant-Id"] = tenant.Id.ToString("D");
        await next(httpContext);
    }

    private static string? GetTenantSlug(HttpContext httpContext)
    {
        var headerSlug = httpContext.Request.Headers["X-Tenant-Slug"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(headerSlug))
        {
            return headerSlug;
        }

        return null;
    }

    private static Task WriteErrorAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(new { message }, context.RequestAborted);
    }
}

