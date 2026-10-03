using System.Security.Claims;
using Lms.Api.Domain.Notifications;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Notifications;

public static class NotificationEndpoints
{
    public static void MapNotificationEndpoints(this WebApplication app)
    {
        var tenant = app.MapGroup("/api/v1/tenant").RequireAuthorization("tenant.authenticated");
        tenant.AddEndpointFilter(async (context, next) =>
        {
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (!tenantContext.IsResolved) return Results.BadRequest(new { message = "A tenant is required. Use the X-Tenant-Slug header or a tenant subdomain." });
            return await next(context);
        });
        tenant.MapGet("/notifications", ListNotificationsAsync).RequireAuthorization("tenant.notification.read");
        tenant.MapPost("/notifications/{notificationId:guid}/read", MarkReadAsync).RequireAuthorization("tenant.notification.read");
        tenant.MapPost("/notifications/read-all", MarkAllReadAsync).RequireAuthorization("tenant.notification.read");
        tenant.MapGet("/notification-preferences", ListPreferencesAsync).RequireAuthorization("tenant.notification.read");
        tenant.MapPut("/notification-preferences", SavePreferenceAsync).RequireAuthorization("tenant.notification.read");
        tenant.MapGet("/notification-templates", ListTemplatesAsync).RequireAuthorization("tenant.notification.manage");
        tenant.MapPost("/notification-templates", CreateTemplateAsync).RequireAuthorization("tenant.notification.manage");
    }

    private static async Task<IResult> ListNotificationsAsync(HttpContext httpContext, LmsDbContext db, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var unreadOnly = bool.TryParse(httpContext.Request.Query["unread"], out var parsed) && parsed;
        var query = db.NotificationMessages.AsNoTracking().Where(item => item.RecipientUserId == userId && item.Channel == NotificationChannel.InApp && item.Status != NotificationStatus.DeadLetter);
        if (unreadOnly) query = query.Where(item => item.ReadAtUtc == null);
        var messages = await query.OrderByDescending(item => item.CreatedAtUtc).Take(100).Select(item => new NotificationResponse(item.Id, item.TemplateCode, item.Subject, item.Body, item.Status.ToString(), item.CreatedAtUtc, item.SentAtUtc, item.ReadAtUtc)).ToListAsync(cancellationToken);
        return Results.Ok(messages);
    }

    private static async Task<IResult> MarkReadAsync(HttpContext httpContext, LmsDbContext db, Guid notificationId, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var notification = await db.NotificationMessages.SingleOrDefaultAsync(item => item.Id == notificationId && item.RecipientUserId == userId, cancellationToken);
        if (notification is null) return Results.NotFound();
        notification.ReadAtUtc ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Marks every unread in-app notification of the signed-in person as read. Nobody else's are touched.</summary>
    private static async Task<IResult> MarkAllReadAsync(HttpContext httpContext, LmsDbContext db, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var unread = await db.NotificationMessages.Where(item => item.RecipientUserId == userId && item.Channel == NotificationChannel.InApp && item.ReadAtUtc == null).ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        foreach (var item in unread) item.ReadAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new { marked = unread.Count });
    }

    private static async Task<IResult> ListPreferencesAsync(HttpContext httpContext, LmsDbContext db, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var preferences = await db.NotificationPreferences.AsNoTracking().Where(item => item.UserId == userId).OrderBy(item => item.TemplateCode).Select(item => new NotificationPreferenceResponse(item.TemplateCode, item.Channel.ToString(), item.Enabled, item.UpdatedAtUtc)).ToListAsync(cancellationToken);
        return Results.Ok(preferences);
    }

    private static async Task<IResult> SavePreferenceAsync(HttpContext httpContext, LmsDbContext db, ITenantContext tenantContext, SavePreferenceRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        if (!Enum.TryParse<NotificationChannel>(request.Channel, true, out var channel)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["channel"] = ["Use InApp or Email."] });
        if (string.IsNullOrWhiteSpace(request.TemplateCode)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["templateCode"] = ["Template code is required."] });
        var preference = await db.NotificationPreferences.SingleOrDefaultAsync(item => item.UserId == userId && item.TemplateCode == request.TemplateCode.Trim().ToUpperInvariant() && item.Channel == channel, cancellationToken);
        if (preference is null)
        {
            preference = new NotificationPreference { Id = Guid.NewGuid(), TenantId = tenantId, UserId = userId, TemplateCode = request.TemplateCode.Trim().ToUpperInvariant(), Channel = channel };
            db.NotificationPreferences.Add(preference);
        }
        preference.Enabled = request.Enabled;
        preference.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new NotificationPreferenceResponse(preference.TemplateCode, preference.Channel.ToString(), preference.Enabled, preference.UpdatedAtUtc));
    }

    private static async Task<IResult> ListTemplatesAsync(LmsDbContext db, CancellationToken cancellationToken)
    {
        var templates = await db.NotificationTemplates.AsNoTracking().OrderBy(item => item.Code).Select(item => new NotificationTemplateResponse(item.Id, item.Code, item.Name, item.Channel.ToString(), item.SubjectTemplate, item.BodyTemplate, item.IsActive)).ToListAsync(cancellationToken);
        return Results.Ok(templates);
    }

    private static async Task<IResult> CreateTemplateAsync(ITenantContext tenantContext, LmsDbContext db, CreateTemplateRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId) return Results.BadRequest(new { message = "A tenant is required." });
        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.SubjectTemplate) || string.IsNullOrWhiteSpace(request.BodyTemplate)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["template"] = ["Code, name, subject, and body are required."] });
        if (!Enum.TryParse<NotificationChannel>(request.Channel, true, out var channel)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["channel"] = ["Use InApp or Email."] });
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.NotificationTemplates.AnyAsync(item => item.Code == code && item.Channel == channel, cancellationToken)) return Results.Conflict(new { message = "A template with this code and channel already exists." });
        var now = DateTimeOffset.UtcNow;
        var template = new NotificationTemplate { Id = Guid.NewGuid(), TenantId = tenantId, Code = code, Name = request.Name.Trim(), Channel = channel, SubjectTemplate = request.SubjectTemplate.Trim(), BodyTemplate = request.BodyTemplate.Trim(), IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
        db.NotificationTemplates.Add(template);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/notification-templates/{template.Id:D}", new NotificationTemplateResponse(template.Id, template.Code, template.Name, template.Channel.ToString(), template.SubjectTemplate, template.BodyTemplate, template.IsActive));
    }

    private static Guid? GetUserId(HttpContext httpContext) => Guid.TryParse(httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}

public sealed record SavePreferenceRequest(string TemplateCode, string Channel, bool Enabled);
public sealed record CreateTemplateRequest(string Code, string Name, string Channel, string SubjectTemplate, string BodyTemplate);
public sealed record NotificationResponse(Guid Id, string TemplateCode, string Subject, string Body, string Status, DateTimeOffset CreatedAtUtc, DateTimeOffset? SentAtUtc, DateTimeOffset? ReadAtUtc);
public sealed record NotificationPreferenceResponse(string TemplateCode, string Channel, bool Enabled, DateTimeOffset UpdatedAtUtc);
public sealed record NotificationTemplateResponse(Guid Id, string Code, string Name, string Channel, string SubjectTemplate, string BodyTemplate, bool IsActive);
