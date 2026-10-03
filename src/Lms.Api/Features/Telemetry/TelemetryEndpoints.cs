using System.Security.Claims;
using System.Text.Json;
using Lms.Api.Domain.Telemetry;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Telemetry;

public static class TelemetryEndpoints
{
    private static readonly string[] AllowedEvents = ["pwa.installed", "pwa.offline_package_prepared", "pwa.offline_sync", "pwa.offline_conflict", "virtual_lab.launch", "virtual_lab.result"];

    public static void MapTelemetryEndpoints(this WebApplication app)
    {
        var tenant = app.MapGroup("/api/v1/tenant/telemetry").RequireAuthorization("tenant.authenticated");
        tenant.AddEndpointFilter(async (context, next) =>
        {
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            return tenantContext.IsResolved ? await next(context) : Results.BadRequest(new { message = "A tenant is required." });
        });
        tenant.MapPost("/events", RecordAsync);
        tenant.MapGet("/summary", SummaryAsync).RequireAuthorization("tenant.report.read");
    }

    private static async Task<IResult> RecordAsync(HttpContext httpContext, LmsDbContext db, RecordTelemetryRequest request, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        if (!AllowedEvents.Contains(request.Name, StringComparer.OrdinalIgnoreCase)) return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Name)] = ["This telemetry event is not supported."] });
        if (!string.IsNullOrWhiteSpace(request.PropertiesJson))
        {
            try { JsonDocument.Parse(request.PropertiesJson); }
            catch (JsonException) { return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.PropertiesJson)] = ["Properties must be valid JSON."] }); }
        }
        var deviceId = Guid.TryParse(request.OfflineDeviceId, out var parsedDeviceId) ? parsedDeviceId : (Guid?)null;
        if (deviceId is Guid id && !await db.OfflineDevices.AnyAsync(item => item.Id == id && item.UserId == userId, cancellationToken)) return Results.NotFound(new { message = "The offline device was not found." });
        db.ProductTelemetryEvents.Add(new ProductTelemetryEvent { Id = Guid.NewGuid(), TenantId = Guid.Parse(httpContext.User.FindFirstValue("tenant_id")!), UserId = userId, OfflineDeviceId = deviceId, Name = request.Name.Trim().ToLowerInvariant(), PropertiesJson = request.PropertiesJson, OccurredAtUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(cancellationToken);
        return Results.Accepted();
    }

    private static async Task<IResult> SummaryAsync(LmsDbContext db, CancellationToken cancellationToken)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-30);
        var eventRows = await db.ProductTelemetryEvents.AsNoTracking().Where(item => item.OccurredAtUtc >= since).Select(item => new { item.Name, item.OccurredAtUtc }).ToListAsync(cancellationToken);
        var events = eventRows.GroupBy(item => item.Name).Select(group => new TelemetryEventSummary(group.Key, group.Count(), group.Max(item => item.OccurredAtUtc))).OrderBy(item => item.Name).ToArray();
        var healthRows = await db.VirtualLabHealthChecks.AsNoTracking().Where(item => item.CheckedAtUtc >= since).Select(item => new { item.Status, item.LatencyMilliseconds }).ToListAsync(cancellationToken);
        var health = healthRows.GroupBy(item => item.Status).Select(group => new ProviderHealthSummary(group.Key, group.Count(), group.Where(item => item.LatencyMilliseconds.HasValue).Select(item => (double)item.LatencyMilliseconds!.Value).DefaultIfEmpty().Average())).OrderBy(item => item.Status).ToArray();
        return Results.Ok(new TelemetrySummaryResponse(since, events, health));
    }

    private static Guid? GetUserId(HttpContext httpContext) => Guid.TryParse(httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}

public sealed record RecordTelemetryRequest(string Name, string? PropertiesJson = null, string? OfflineDeviceId = null);
public sealed record TelemetryEventSummary(string Name, int Count, DateTimeOffset LastOccurredAtUtc);
public sealed record ProviderHealthSummary(string Status, int Count, double? AverageLatencyMilliseconds);
public sealed record TelemetrySummaryResponse(DateTimeOffset SinceUtc, TelemetryEventSummary[] Events, ProviderHealthSummary[] ProviderHealth);
