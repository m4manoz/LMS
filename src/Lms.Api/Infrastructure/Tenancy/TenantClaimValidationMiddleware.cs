namespace Lms.Api.Infrastructure.Tenancy;

public sealed class TenantClaimValidationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext httpContext, ITenantContext tenantContext)
    {
        if (httpContext.User.Identity?.IsAuthenticated == true
            && tenantContext.TenantId is Guid tenantId)
        {
            var claim = httpContext.User.FindFirst("tenant_id")?.Value;
            if (!Guid.TryParse(claim, out var claimTenantId) || claimTenantId != tenantId)
            {
                httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
                await httpContext.Response.WriteAsJsonAsync(
                    new { message = "The token does not belong to the requested tenant." },
                    httpContext.RequestAborted);
                return;
            }
        }

        await next(httpContext);
    }
}

