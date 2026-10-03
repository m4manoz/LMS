using Lms.Api.Domain.VirtualLabs;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Operations;

public static class OperationsEndpoints
{
    public static void MapOperationsEndpoints(this WebApplication app)
    {
        var tenant = app.MapGroup("/api/v1/tenant/operations").RequireAuthorization("tenant.report.read");
        tenant.AddEndpointFilter(async (context, next) =>
        {
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            return tenantContext.IsResolved ? await next(context) : Results.BadRequest(new { message = "A tenant is required." });
        });
        tenant.MapGet("/summary", SummaryAsync);
    }

    private static async Task<IResult> SummaryAsync(LmsDbContext db, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var telemetrySince = now.AddDays(-30);
        var healthSince = now.AddDays(-30);

        var telemetryRows = await db.ProductTelemetryEvents.AsNoTracking()
            .Where(item => item.OccurredAtUtc >= telemetrySince)
            .Select(item => new { item.Name, item.OccurredAtUtc })
            .ToListAsync(cancellationToken);
        var telemetry = telemetryRows.GroupBy(item => item.Name)
            .Select(group => new OperationsTelemetrySummary(group.Key, group.Count(), group.Max(item => item.OccurredAtUtc)))
            .OrderBy(item => item.Name).ToArray();

        var healthRows = await db.VirtualLabHealthChecks.AsNoTracking()
            .Where(item => item.CheckedAtUtc >= healthSince)
            .Select(item => new { item.VirtualLabId, item.Status, item.CheckedAtUtc })
            .ToListAsync(cancellationToken);
        var latestHealth = healthRows.GroupBy(item => item.VirtualLabId)
            .Select(group => group.OrderByDescending(item => item.CheckedAtUtc).First())
            .ToArray();
        var activeLabIds = await db.VirtualLabs.AsNoTracking()
            .Where(item => item.Status == VirtualLabStatus.Active)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        var latestHealthByLab = latestHealth.ToDictionary(item => item.VirtualLabId);
        var unhealthyLabs = activeLabIds.Count(id => !latestHealthByLab.TryGetValue(id, out var health) || !health.Status.Equals("Healthy", StringComparison.OrdinalIgnoreCase));

        var activeDevices = await db.OfflineDevices.AsNoTracking().CountAsync(item => item.Status == Domain.Offline.OfflineDeviceStatus.Active, cancellationToken);
        var activeLicenses = await db.OfflinePackageLicenses.AsNoTracking().CountAsync(item => item.RevokedAtUtc == null && item.ExpiresAtUtc > now, cancellationToken);
        var openConflicts = await db.OfflineSyncConflicts.AsNoTracking().CountAsync(item => item.Status == "Open", cancellationToken);
        var deadLetterWebhooks = await db.VirtualLabWebhookEvents.AsNoTracking().CountAsync(item => item.Status == "DeadLetter", cancellationToken);
        var providerChecks = healthRows.Count;
        var healthyProviderChecks = healthRows.Count(item => item.Status.Equals("Healthy", StringComparison.OrdinalIgnoreCase));
        var healthyProviderRatio = providerChecks == 0 ? 0 : (double)healthyProviderChecks / providerChecks;

        var alerts = new List<OperationsAlert>();
        AddAlert(alerts, "offline.conflicts", "Offline sync conflicts need review.", openConflicts, configuration.GetValue("Operations:Alerts:OpenOfflineConflicts", 25), "warning");
        AddAlert(alerts, "virtual-lab.dead-letter", "Virtual lab webhooks are in the dead-letter queue.", deadLetterWebhooks, configuration.GetValue("Operations:Alerts:DeadLetterWebhooks", 1), "critical");
        AddAlert(alerts, "virtual-lab.health", "Active virtual labs have not reported healthy status.", unhealthyLabs, configuration.GetValue("Operations:Alerts:UnhealthyLabs", 1), "warning");

        var minimumInstalls = configuration.GetValue("Operations:DevicePilot:MinimumPwaInstalls", 50);
        var minimumPackages = configuration.GetValue("Operations:DevicePilot:MinimumOfflinePackages", 20);
        var minimumChecks = configuration.GetValue("Operations:DevicePilot:MinimumProviderHealthChecks", 20);
        var minimumRatio = configuration.GetValue("Operations:DevicePilot:MinimumHealthyProviderRatio", 0.95);
        var installCount = telemetryRows.Count(item => item.Name == "pwa.installed");
        var packageCount = telemetryRows.Count(item => item.Name == "pwa.offline_package_prepared");
        var pilotReasons = new List<string>();
        if (installCount < minimumInstalls) pilotReasons.Add($"PWA installs: {installCount}/{minimumInstalls}.");
        if (packageCount < minimumPackages) pilotReasons.Add($"Offline packages: {packageCount}/{minimumPackages}.");
        if (providerChecks < minimumChecks) pilotReasons.Add($"Provider health checks: {providerChecks}/{minimumChecks}.");
        if (healthyProviderRatio < minimumRatio) pilotReasons.Add($"Healthy provider ratio: {healthyProviderRatio:P0}/{minimumRatio:P0}.");

        var pilotDecision = pilotReasons.Count == 0 ? "ReadyForFocusedPilot" : "NotReady";
        return Results.Ok(new OperationsSummaryResponse(
            telemetrySince,
            new OperationsCounts(activeDevices, activeLicenses, openConflicts, deadLetterWebhooks, unhealthyLabs),
            telemetry,
            alerts.ToArray(),
            new DevicePilotDecision(pilotDecision, pilotReasons.ToArray(), new[]
            {
                $"At least {minimumInstalls} PWA installs in the last 30 days.",
                $"At least {minimumPackages} offline packages prepared in the last 30 days.",
                $"At least {minimumChecks} provider health checks with at least {minimumRatio:P0} healthy.",
                "Pilot acceptance requires no critical alerts and documented owner, cohort, rollback, and support plan."
            })));
    }

    private static void AddAlert(List<OperationsAlert> alerts, string code, string message, int actual, int threshold, string severity)
    {
        if (actual >= threshold) alerts.Add(new OperationsAlert(code, severity, message, actual, threshold));
    }
}

public sealed record OperationsCounts(int ActiveOfflineDevices, int ActiveOfflineLicenses, int OpenOfflineConflicts, int DeadLetterWebhooks, int UnhealthyActiveLabs);
public sealed record OperationsTelemetrySummary(string Name, int Count, DateTimeOffset LastOccurredAtUtc);
public sealed record OperationsAlert(string Code, string Severity, string Message, int Actual, int Threshold);
public sealed record DevicePilotDecision(string Status, string[] BlockingReasons, string[] AcceptanceCriteria);
public sealed record OperationsSummaryResponse(DateTimeOffset SinceUtc, OperationsCounts Counts, OperationsTelemetrySummary[] Telemetry, OperationsAlert[] Alerts, DevicePilotDecision DevicePilot);
