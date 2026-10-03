using Lms.Api.Domain.Videos;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Storage;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Infrastructure.Videos;

public static class TranscriptStore
{
    /// <summary>Makes the video's transcript exactly these lines (the previous lines are removed). Does not save.</summary>
    public static async Task<VideoTranscript> ReplaceAsync(LmsDbContext db, Guid tenantId, Guid videoId, IReadOnlyList<TranscriptCue> cues, string source, string? language, string? provider, Guid userId, CancellationToken cancellationToken)
    {
        var transcript = await ResetAsync(db, tenantId, videoId, source, provider, userId, cancellationToken);
        transcript.Status = TranscriptStatus.Ready;
        transcript.Language = language is { Length: > 0 } ? (language.Length > 40 ? language[..40] : language) : null;
        for (var index = 0; index < cues.Count; index++)
            db.VideoTranscriptSegments.Add(new VideoTranscriptSegment { Id = Guid.NewGuid(), TenantId = tenantId, VideoId = videoId, Index = index, StartMs = cues[index].StartMs, EndMs = cues[index].EndMs, Text = cues[index].Text });
        return transcript;
    }

    /// <summary>The transcript row for a video with all its lines removed, created when there is none. Does not save.</summary>
    public static async Task<VideoTranscript> ResetAsync(LmsDbContext db, Guid tenantId, Guid videoId, string source, string? provider, Guid userId, CancellationToken cancellationToken)
    {
        var old = await db.VideoTranscriptSegments.Where(item => item.VideoId == videoId).ToListAsync(cancellationToken);
        db.VideoTranscriptSegments.RemoveRange(old);
        var now = DateTimeOffset.UtcNow;
        var transcript = await db.VideoTranscripts.SingleOrDefaultAsync(item => item.VideoId == videoId, cancellationToken);
        if (transcript is null)
        {
            transcript = new VideoTranscript { Id = Guid.NewGuid(), TenantId = tenantId, VideoId = videoId, CreatedByUserId = userId, CreatedAtUtc = now };
            db.VideoTranscripts.Add(transcript);
        }
        transcript.Source = source;
        transcript.Provider = provider;
        transcript.Language = null;
        transcript.StatusMessage = null;
        transcript.Attempts = 0;
        transcript.UpdatedAtUtc = now;
        return transcript;
    }

    /// <summary>Queues a generated transcript for the video. Does not save.</summary>
    public static async Task QueueAsync(LmsDbContext db, Guid tenantId, Guid videoId, string provider, Guid userId, CancellationToken cancellationToken)
    {
        var transcript = await ResetAsync(db, tenantId, videoId, "Generated", provider, userId, cancellationToken);
        transcript.Status = TranscriptStatus.Queued;
    }
}

/// <summary>Makes transcripts that were asked for: pulls the sound out of the video and sends it to the organization's speech-to-text service.</summary>
public sealed class VideoTranscriptionService(IServiceScopeFactory scopes, IVideoTranscoder transcoder, ILogger<VideoTranscriptionService> logger)
{
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        List<(Guid VideoId, Guid TenantId)> waiting;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LmsDbContext>();
            waiting = (await db.VideoTranscripts.IgnoreQueryFilters().AsNoTracking().Where(item => item.Status == TranscriptStatus.Queued)
                .OrderBy(item => item.UpdatedAtUtc).Take(2).Select(item => new { item.VideoId, item.TenantId }).ToListAsync(cancellationToken))
                .Select(item => (item.VideoId, item.TenantId)).ToList();
        }
        foreach (var (videoId, tenantId) in waiting) await ProcessAsync(videoId, tenantId, cancellationToken);
        return waiting.Count;
    }

    private async Task ProcessAsync(Guid videoId, Guid tenantId, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LmsDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IContentAssetStorage>();
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(item => item.Id == tenantId, cancellationToken);
        if (tenant is null) return;
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenant.Id, tenant.Slug);

        var transcript = await db.VideoTranscripts.SingleOrDefaultAsync(item => item.VideoId == videoId && item.Status == TranscriptStatus.Queued, cancellationToken);
        if (transcript is null) return;
        transcript.Status = TranscriptStatus.Processing;
        transcript.Attempts++;
        await db.SaveChangesAsync(cancellationToken);

        var work = Path.Combine(Path.GetTempPath(), "lms-audio-" + Guid.NewGuid().ToString("N"));
        try
        {
            var video = await db.Videos.AsNoTracking().SingleOrDefaultAsync(item => item.Id == videoId, cancellationToken) ?? throw new VideoAiException("The video no longer exists.");
            var asset = video.ContentAssetId is Guid assetId ? await db.ContentAssets.AsNoTracking().SingleOrDefaultAsync(item => item.Id == assetId, cancellationToken) : null;
            if (asset is null) throw new VideoAiException("The video file is not available.");
            var client = await scope.ServiceProvider.GetRequiredService<VideoAiResolver>().ResolveAsync(db, cancellationToken);
            if (!client.CanTranscribe) throw new VideoAiException("No speech-to-text service is set up. Ask an administrator to add one under Integrations → Video AI.");

            Directory.CreateDirectory(work);
            await using (var source = await storage.OpenReadAsync(asset.StorageKey, cancellationToken) ?? throw new VideoAiException("The video file is not available."))
            await using (var target = File.Create(Path.Combine(work, VideoFiles.Input)))
                await source.CopyToAsync(target, cancellationToken);
            await transcoder.ExtractAudioAsync(work, cancellationToken);
            var (language, cues) = await client.TranscribeAsync(Path.Combine(work, VideoFiles.Audio), cancellationToken);

            await TranscriptStore.ReplaceAsync(db, tenantId, videoId, cues, "Generated", language, client.Name, transcript.CreatedByUserId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Transcript for video {VideoId} failed.", videoId);
            transcript.Status = TranscriptStatus.Failed;
            transcript.StatusMessage = exception is VideoAiException or InvalidOperationException ? Truncate(exception.Message, 480) : "The transcript could not be made.";
            transcript.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (IOException) { }
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}

public sealed class VideoTranscriptionWorker(VideoTranscriptionService service, IConfiguration configuration, ILogger<VideoTranscriptionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Videos:Processing:WorkerEnabled", true)) return;   // tests run the service by hand
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(4));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await service.RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Transcription cycle failed."); }
        }
    }
}
