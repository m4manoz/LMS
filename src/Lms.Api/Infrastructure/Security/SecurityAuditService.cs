using System.Text.Json;
using System.Security.Claims;
using Lms.Api.Domain.Security;
using Lms.Api.Infrastructure.Persistence;

namespace Lms.Api.Infrastructure.Security;

public sealed class SecurityAuditService
{
    public void Add(
        LmsDbContext db,
        HttpContext httpContext,
        Guid tenantId,
        string action,
        string resourceType,
        Guid? resourceId = null,
        object? details = null)
    {
        Guid? actorId = Guid.TryParse(httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? httpContext.User.FindFirstValue("sub"), out var id) ? id : null;
        db.SecurityAuditEvents.Add(new SecurityAuditEvent
        {
            Id = Guid.NewGuid(), TenantId = tenantId, ActorUserId = actorId,
            Action = action, ResourceType = resourceType, ResourceId = resourceId,
            DetailsJson = JsonSerializer.Serialize(details ?? new { }),
            IpAddress = httpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = httpContext.Request.Headers.UserAgent.ToString(), CreatedAtUtc = DateTimeOffset.UtcNow
        });
    }
}
