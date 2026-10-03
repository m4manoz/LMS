using Lms.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Infrastructure.Retention;

public sealed class RetentionWorker(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<RetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (configuration.GetValue("Retention:Enabled", true))
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    await RunOnceAsync(scope.ServiceProvider.GetRequiredService<LmsDbContext>(), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                catch (Exception exception) { logger.LogError(exception, "Retention job failed; data was not fully pruned."); }
            }

            try { await Task.Delay(TimeSpan.FromHours(24), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }

    public async Task<RetentionResult> RunOnceAsync(LmsDbContext db, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var telemetry = await db.ProductTelemetryEvents.IgnoreQueryFilters().Where(item => item.OccurredAtUtc < now.AddDays(-configuration.GetValue("Retention:TelemetryDays", 180))).ExecuteDeleteAsync(cancellationToken);
        var health = await db.VirtualLabHealthChecks.IgnoreQueryFilters().Where(item => item.CheckedAtUtc < now.AddDays(-configuration.GetValue("Retention:ProviderHealthDays", 180))).ExecuteDeleteAsync(cancellationToken);
        var webhooks = await db.VirtualLabWebhookEvents.IgnoreQueryFilters().Where(item => item.ProcessedAtUtc != null && item.ProcessedAtUtc < now.AddDays(-configuration.GetValue("Retention:WebhookDays", 365))).ExecuteDeleteAsync(cancellationToken);
        var licenses = await db.OfflinePackageLicenses.IgnoreQueryFilters().Where(item => item.ExpiresAtUtc < now.AddDays(-configuration.GetValue("Retention:OfflineLicenseDays", 30))).ExecuteDeleteAsync(cancellationToken);
        var conflicts = await db.OfflineSyncConflicts.IgnoreQueryFilters().Where(item => item.Status == "Resolved" && item.ResolvedAtUtc != null && item.ResolvedAtUtc < now.AddDays(-configuration.GetValue("Retention:OfflineConflictDays", 180))).ExecuteDeleteAsync(cancellationToken);
        var reviews = await db.GamificationAbuseReviews.IgnoreQueryFilters().Where(item => item.Status == "Resolved" && item.ResolvedAtUtc != null && item.ResolvedAtUtc < now.AddDays(-configuration.GetValue("Retention:GamificationReviewDays", 365))).ExecuteDeleteAsync(cancellationToken);
        return new RetentionResult(telemetry, health, webhooks, licenses, conflicts, reviews);
    }
}

public sealed record RetentionResult(int Telemetry, int ProviderHealth, int Webhooks, int OfflineLicenses, int OfflineConflicts, int GamificationReviews);
