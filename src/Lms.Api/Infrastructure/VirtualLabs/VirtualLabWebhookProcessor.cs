using System.Text.Json;
using Lms.Api.Domain.Identity;
using Lms.Api.Domain.VirtualLabs;
using Lms.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Infrastructure.VirtualLabs;

public sealed class VirtualLabWebhookProcessor
{
    public async Task ProcessAsync(LmsDbContext db, VirtualLabWebhookEvent webhook, CancellationToken cancellationToken)
    {
        if (webhook.Status == "Processed") return;
        webhook.AttemptCount++;
        try
        {
            using var document = JsonDocument.Parse(webhook.PayloadJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("userId", out var userElement) || !Guid.TryParse(userElement.GetString(), out var userId)) throw new InvalidOperationException("Webhook userId is required.");
            if (!root.TryGetProperty("externalAttemptId", out var attemptElement) || string.IsNullOrWhiteSpace(attemptElement.GetString())) throw new InvalidOperationException("Webhook externalAttemptId is required.");
            var externalAttemptId = attemptElement.GetString()!.Trim();
            if (externalAttemptId.Length > 160) throw new InvalidOperationException("Webhook externalAttemptId is too long.");
            var completed = root.TryGetProperty("completed", out var completedElement) && completedElement.ValueKind == JsonValueKind.True;
            decimal? score = null;
            if (root.TryGetProperty("scorePercent", out var scoreElement) && scoreElement.ValueKind is JsonValueKind.Number && scoreElement.TryGetDecimal(out var parsedScore))
            {
                if (parsedScore is < 0 or > 100) throw new InvalidOperationException("Webhook scorePercent must be between 0 and 100.");
                score = parsedScore;
            }
            var lab = await db.VirtualLabs.IgnoreQueryFilters().SingleOrDefaultAsync(item => item.Id == webhook.VirtualLabId && item.TenantId == webhook.TenantId, cancellationToken) ?? throw new InvalidOperationException("The virtual lab no longer exists.");
            if (!await db.TenantMemberships.IgnoreQueryFilters().AnyAsync(item => item.TenantId == webhook.TenantId && item.UserId == userId && item.Status == MembershipStatus.Active, cancellationToken)) throw new InvalidOperationException("The webhook user is not an active tenant member.");
            var existing = await db.VirtualLabResults.IgnoreQueryFilters().SingleOrDefaultAsync(item => item.TenantId == webhook.TenantId && item.VirtualLabId == lab.Id && item.UserId == userId && item.ExternalAttemptId == externalAttemptId, cancellationToken);
            if (existing is null)
            {
                db.VirtualLabResults.Add(new VirtualLabResult { Id = Guid.NewGuid(), TenantId = webhook.TenantId, VirtualLabId = lab.Id, UserId = userId, ExternalAttemptId = externalAttemptId, ScorePercent = score, Completed = completed, PayloadJson = webhook.PayloadJson, ReceivedAtUtc = DateTimeOffset.UtcNow });
            }
            webhook.Status = "Processed";
            webhook.ProcessedAtUtc = DateTimeOffset.UtcNow;
            webhook.NextAttemptAtUtc = null;
            webhook.LastError = null;
        }
        catch (Exception exception)
        {
            webhook.Status = webhook.AttemptCount >= webhook.MaxAttempts ? "DeadLetter" : "Failed";
            webhook.LastError = exception.Message[..Math.Min(exception.Message.Length, 2000)];
            webhook.NextAttemptAtUtc = webhook.Status == "Failed" ? DateTimeOffset.UtcNow.AddMinutes(Math.Min(30, Math.Pow(2, Math.Max(0, webhook.AttemptCount - 1)))) : null;
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class VirtualLabWebhookWorker(IServiceScopeFactory scopeFactory, ILogger<VirtualLabWebhookWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<LmsDbContext>();
                var processor = scope.ServiceProvider.GetRequiredService<VirtualLabWebhookProcessor>();
                var now = DateTimeOffset.UtcNow;
                var pending = await db.VirtualLabWebhookEvents.IgnoreQueryFilters().Where(item => item.Status == "Failed" && item.NextAttemptAtUtc != null && item.NextAttemptAtUtc <= now).OrderBy(item => item.NextAttemptAtUtc).Take(25).ToListAsync(stoppingToken);
                foreach (var webhook in pending) await processor.ProcessAsync(db, webhook, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) { logger.LogError(exception, "Virtual lab webhook worker failed."); }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
