using Lms.Api.Domain.Videos;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Storage;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Infrastructure.Videos;

/// <summary>Converts uploaded videos waiting in the Processing state, one at a time, and removes what a deleted video left behind.</summary>
public sealed class VideoProcessingService(IServiceScopeFactory scopes, IVideoTranscoder transcoder, IConfiguration configuration, ILogger<VideoProcessingService> logger)
{
    public const int MaxAttempts = 3;

    /// <summary>Processes up to a few waiting videos. Returns how many were handled.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!transcoder.Enabled) return 0;
        List<(Guid Id, Guid TenantId)> waiting;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LmsDbContext>();
            waiting = (await db.Videos.IgnoreQueryFilters().AsNoTracking()
                .Where(item => item.Status == VideoStatus.Processing && item.ContentAssetId != null && (item.Type == VideoType.Uploaded || item.Type == VideoType.LiveRecording))
                .OrderBy(item => item.UpdatedAtUtc).Take(3).Select(item => new { item.Id, item.TenantId }).ToListAsync(cancellationToken))
                .Select(item => (item.Id, item.TenantId)).ToList();
        }
        foreach (var (id, tenantId) in waiting) await ProcessAsync(id, tenantId, cancellationToken);
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

        var video = await db.Videos.SingleOrDefaultAsync(item => item.Id == videoId && item.Status == VideoStatus.Processing, cancellationToken);
        if (video is null) return;
        var asset = video.ContentAssetId is Guid assetId ? await db.ContentAssets.AsNoTracking().SingleOrDefaultAsync(item => item.Id == assetId, cancellationToken) : null;
        var work = Path.Combine(Path.GetTempPath(), "lms-video-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (asset is null) throw new InvalidOperationException("The uploaded file is missing.");
            Directory.CreateDirectory(work);
            await using (var source = await storage.OpenReadAsync(asset.StorageKey, cancellationToken) ?? throw new InvalidOperationException("The uploaded file is missing."))
            await using (var target = File.Create(Path.Combine(work, VideoFiles.Input)))
                await source.CopyToAsync(target, cancellationToken);

            video.ProcessingAttempts++;
            var result = await transcoder.PackageAsync(work, cancellationToken);

            // A video converted before is replaced as a whole, so no piece of the old version is left behind.
            if (VideoFiles.HasStreaming(video) || video.HasPoster) await DeleteDerivedFilesAsync(storage, video, cancellationToken);
            video.HlsSegmentCount = result.Renditions[0].Segments;
            video.HlsLayout = VideoFiles.FormatLayout(result.Renditions);
            video.HasPoster = result.HasPoster;
            foreach (var name in VideoFiles.FilesOf(video).Where(name => VideoFiles.Exists(video, name)))
            {
                await using var content = File.OpenRead(Path.Combine(work, name));
                await storage.PutAsync(VideoFiles.Key(video.TenantId, video.CourseId, video.Id, name), content, VideoFiles.ContentType(name), cancellationToken);
            }

            if (result.DurationSeconds is > 0) video.DurationSeconds = result.DurationSeconds;
            video.Status = VideoStatus.Ready;
            video.StatusMessage = null;
            // Organizations that want every video transcribed get it queued as soon as the video is ready.
            if (await db.VideoAiSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken) is { AutoTranscribe: true, Provider: VideoAiProviders.OpenAiCompatible })
                await TranscriptStore.QueueAsync(db, video.TenantId, video.Id, VideoAiProviders.OpenAiCompatible, video.CreatedByUserId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Video {VideoId} could not be processed (attempt {Attempt}).", videoId, video.ProcessingAttempts);
            // Typically a file FFmpeg cannot read. An administrator can retry from the library once it is fixed or the server is set up.
            video.Status = VideoStatus.Failed;
            video.StatusMessage = Truncate(exception.Message, 480);
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (IOException) { }
        }
        video.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Removes the streaming files and poster of a video.</summary>
    public static async Task DeleteDerivedFilesAsync(IContentAssetStorage storage, Video video, CancellationToken cancellationToken)
    {
        foreach (var name in VideoFiles.FilesOf(video))
            await storage.DeleteAsync(VideoFiles.Key(video.TenantId, video.CourseId, video.Id, name), cancellationToken);
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}

public sealed class VideoProcessingWorker(VideoProcessingService service, IConfiguration configuration, ILogger<VideoProcessingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Videos:Processing:WorkerEnabled", true)) return;   // tests run the service by hand
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await service.RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Video processing cycle failed."); }
        }
    }
}
