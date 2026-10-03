using Lms.Api.Domain.LiveClasses;
using Lms.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Infrastructure.LiveClasses;

public sealed class LiveClassWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<LiveClassWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Live-class recording worker cycle failed.");
            }
        }
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LmsDbContext>();
        var provider = scope.ServiceProvider.GetRequiredService<ILiveClassProvider>();
        var now = DateTimeOffset.UtcNow;

        var expired = await db.SessionRecordings
            .IgnoreQueryFilters()
            .Where(item => item.Status == RecordingStatus.Available && item.RetainUntilUtc != null && item.RetainUntilUtc <= now)
            .ToListAsync(cancellationToken);
        foreach (var recording in expired)
        {
            recording.Status = RecordingStatus.Expired;
            recording.RecordingUrl = null;
            recording.LastError = null;
        }

        var due = await db.SessionRecordings
            .IgnoreQueryFilters()
            .Where(item => (item.Status == RecordingStatus.Requested || item.Status == RecordingStatus.Failed)
                && item.AttemptCount < item.MaxAttempts
                && (item.NextAttemptAtUtc == null || item.NextAttemptAtUtc <= now))
            .OrderBy(item => item.RequestedAtUtc)
            .Take(10)
            .ToListAsync(cancellationToken);

        if (expired.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        foreach (var recording in due)
        {
            var session = await db.LiveClassSessions.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(item => item.Id == recording.SessionId, cancellationToken);
            if (session is null)
            {
                recording.Status = RecordingStatus.Failed;
                recording.LastError = "The live session no longer exists.";
                recording.NextAttemptAtUtc = null;
                continue;
            }

            // Only the built-in provider produces recordings itself; for classes held elsewhere the recording is attached by link.
            if (!string.Equals(session.Provider, provider.Name, StringComparison.OrdinalIgnoreCase))
            {
                recording.Status = RecordingStatus.Failed;
                recording.LastError = "This class is held in another tool. Add the recording link instead.";
                recording.NextAttemptAtUtc = null;
                continue;
            }

            recording.Status = RecordingStatus.Processing;
            recording.AttemptCount++;
            recording.NextAttemptAtUtc = null;
            await db.SaveChangesAsync(cancellationToken);

            try
            {
                var result = await provider.CreateRecordingAsync(new LiveRecordingRequest(session.Id, session.Title), cancellationToken);
                recording.Provider = result.Provider;
                recording.ProviderRecordingId = result.RecordingId;
                recording.RecordingUrl = result.RecordingUrl;
                recording.Status = RecordingStatus.Available;
                recording.AvailableAtUtc = DateTimeOffset.UtcNow;
                recording.RetainUntilUtc = recording.AvailableAtUtc.Value.AddDays(Math.Max(1, configuration.GetValue("LiveClasses:RecordingRetentionDays", 365)));
                recording.LastError = null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                recording.Status = RecordingStatus.Failed;
                recording.LastError = exception.Message.Length > 4000 ? exception.Message[..4000] : exception.Message;
                recording.NextAttemptAtUtc = recording.AttemptCount < recording.MaxAttempts
                    ? DateTimeOffset.UtcNow.AddSeconds(Math.Pow(2, recording.AttemptCount))
                    : null;
                logger.LogWarning(exception, "Recording provisioning failed for session {SessionId}; attempt {AttemptCount}.", session.Id, recording.AttemptCount);
            }

            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
