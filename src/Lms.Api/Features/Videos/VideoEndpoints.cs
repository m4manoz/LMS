using System.Security.Claims;
using Lms.Api.Domain.Courses;
using Lms.Api.Domain.Identity;
using Lms.Api.Domain.Learning;
using Lms.Api.Domain.Videos;
using Lms.Api.Infrastructure.CourseAccess;
using Lms.Api.Infrastructure.LiveClasses;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Storage;
using Lms.Api.Infrastructure.Tenancy;
using Lms.Api.Infrastructure.Videos;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Videos;

/// <summary>
/// The video library. Staff upload and manage videos for their courses; learners watch the ready videos of courses they are enrolled in.
/// Playback uses a short-lived link, never a permanent address, and progress is recorded so people can resume and teachers can see where attention drops.
/// </summary>
public static class VideoEndpoints
{
    private const int MaxBlocksPerLesson = 100;
    private static readonly System.Text.RegularExpressions.Regex LanguageTag = new("^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private const int MaxDurationSeconds = 24 * 3600;
    private static readonly string[] DefaultEmbedHosts = ["www.youtube.com", "www.youtube-nocookie.com", "player.vimeo.com"];

    public static void MapVideoEndpoints(this WebApplication app)
    {
        var limit = Math.Clamp(app.Configuration.GetValue("Videos:MaxMegabytes", 1024L), 1L, 20_480L) * 1024 * 1024;

        var tenant = app.MapGroup("/api/v1/tenant/videos").RequireAuthorization("tenant.authenticated");
        tenant.AddEndpointFilter(async (context, next) =>
        {
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (!tenantContext.IsResolved)
                return Results.BadRequest(new { message = "A tenant is required. Use the X-Tenant-Slug header or a tenant subdomain." });
            return await next(context);
        });

        tenant.MapGet("", ListAsync).RequireAuthorization("tenant.course.read");
        tenant.MapGet("/usage", UsageAsync).RequireAuthorization("tenant.course.manage");
        tenant.MapGet("/{videoId:guid}", GetAsync).RequireAuthorization("tenant.course.read");
        tenant.MapGet("/{videoId:guid}/link", LinkAsync).RequireAuthorization("tenant.course.read");
        tenant.MapPost("/{videoId:guid}/progress", ProgressAsync).RequireAuthorization("tenant.course.read");
        tenant.MapGet("/{videoId:guid}/analytics", AnalyticsAsync).RequireAuthorization("tenant.course.manage");
        tenant.MapPost("", UploadAsync).RequireAuthorization("tenant.course.manage")
            .WithMetadata(new RequestSizeLimitAttribute(limit), new RequestFormLimitsAttribute { MultipartBodyLengthLimit = limit });
        tenant.MapPost("/external", CreateExternalAsync).RequireAuthorization("tenant.course.manage");
        tenant.MapPut("/{videoId:guid}", UpdateAsync).RequireAuthorization("tenant.course.manage");
        tenant.MapPost("/{videoId:guid}/attach", AttachAsync).RequireAuthorization("tenant.course.manage");
        tenant.MapDelete("/{videoId:guid}", DeleteAsync).RequireAuthorization("tenant.course.manage");
        tenant.MapPost("/{videoId:guid}/reprocess", ReprocessAsync).RequireAuthorization("tenant.course.manage");

        // The player fetches this itself, so it cannot carry a sign-in header: the signed token is the credential.
        app.MapGet("/api/v1/tenant/videos/{videoId:guid}/stream", StreamAsync).AllowAnonymous();
        app.MapGet("/api/v1/tenant/videos/{videoId:guid}/hls/{file}", HlsAsync).AllowAnonymous();
        app.MapGet("/api/v1/tenant/videos/{videoId:guid}/poster", PosterAsync).AllowAnonymous();
        app.MapGet("/api/v1/tenant/videos/{videoId:guid}/captions.vtt", CaptionsAsync).AllowAnonymous();
    }

    // ---------- reading ----------
    private static async Task<IResult> ListAsync(HttpContext httpContext, LmsDbContext db, Guid? courseId, string? type, string? status, string? search, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var query = await VisibleAsync(httpContext, db, userId, cancellationToken);
        if (courseId is Guid course) query = query.Where(item => item.CourseId == course);
        if (Enum.TryParse<VideoType>(type, true, out var parsedType)) query = query.Where(item => item.Type == parsedType);
        if (CanManage(httpContext) && Enum.TryParse<VideoStatus>(status, true, out var parsedStatus)) query = query.Where(item => item.Status == parsedStatus);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var needle = search.Trim().ToLower();
            query = query.Where(item => item.Title.ToLower().Contains(needle) || (item.Description != null && item.Description.ToLower().Contains(needle)));
        }
        var videos = await query.OrderByDescending(item => item.CreatedAtUtc).Take(300).ToListAsync(cancellationToken);
        return Results.Ok(await ToResponsesAsync(httpContext, db, videos, userId, cancellationToken));
    }

    private static async Task<IResult> GetAsync(HttpContext httpContext, LmsDbContext db, Guid videoId, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var video = await (await VisibleAsync(httpContext, db, userId, cancellationToken)).SingleOrDefaultAsync(item => item.Id == videoId, cancellationToken);
        return video is null ? Results.NotFound() : Results.Ok((await ToResponsesAsync(httpContext, db, [video], userId, cancellationToken))[0]);
    }

    private static async Task<IResult> UsageAsync(LmsDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.Videos.AsNoTracking().Select(item => new { item.Type, item.Status, item.SizeBytes }).ToListAsync(cancellationToken);
        return Results.Ok(new VideoUsageResponse(rows.Count, rows.Sum(item => item.SizeBytes),
            rows.GroupBy(item => item.Type).Select(group => new VideoUsageGroup(group.Key.ToString(), group.Count(), group.Sum(item => item.SizeBytes))).ToArray(),
            rows.GroupBy(item => item.Status).Select(group => new VideoUsageGroup(group.Key.ToString(), group.Count(), group.Sum(item => item.SizeBytes))).ToArray()));
    }

    // ---------- playback ----------
    private static async Task<IResult> LinkAsync(HttpContext httpContext, LmsDbContext db, ITenantContext tenantContext, IContentAssetStorage storage, VideoPlaybackTokens tokens, IConfiguration configuration, Guid videoId, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId || tenantContext.TenantId is not Guid tenantId) return Results.Unauthorized();
        var video = await (await VisibleAsync(httpContext, db, userId, cancellationToken)).SingleOrDefaultAsync(item => item.Id == videoId, cancellationToken);
        if (video is null) return Results.NotFound();
        if (video.Status != VideoStatus.Ready && !CanManage(httpContext)) return Results.NotFound();

        if (video.Type == VideoType.External)
            return Results.Ok(new PlaybackLink("external", video.ExternalUrl!, null, IsEmbeddable(video.ExternalUrl, configuration)));

        var asset = video.ContentAssetId is Guid assetId ? await db.ContentAssets.AsNoTracking().SingleOrDefaultAsync(item => item.Id == assetId, cancellationToken) : null;
        if (asset is null || !await storage.ExistsAsync(asset.StorageKey, cancellationToken)) return Results.Conflict(new { message = "The video file is not available." });

        httpContext.Response.Headers.CacheControl = "no-store"; // the link is a credential; never cache it
        // Object storage hands out its own direct, expiring link (and can sit behind a CDN); local disk is streamed through us with a signed token.
        var (token, expires) = tokens.Create(tenantId, video.Id, userId);
        var streamUrl = $"/api/v1/tenant/videos/{video.Id:D}/stream?token={Uri.EscapeDataString(token)}";
        // A streaming version (HLS) is played through the API so each piece can carry the signed token; the original file stays as a fallback.
        // Captions come from the transcript. They travel through the API too, so only videos that do are offered them.
        var transcript = await db.VideoTranscripts.AsNoTracking().SingleOrDefaultAsync(item => item.VideoId == video.Id && item.Status == TranscriptStatus.Ready, cancellationToken);
        var captions = transcript is null ? null : $"/api/v1/tenant/videos/{video.Id:D}/captions.vtt?token={Uri.EscapeDataString(token)}";
        var language = transcript?.Language is { } code && LanguageTag.IsMatch(code) ? code : "und";
        if (VideoFiles.HasStreaming(video))
            return Results.Ok(new PlaybackLink("hls", $"/api/v1/tenant/videos/{video.Id:D}/hls/{VideoFiles.StartPlaylist(video)}?token={Uri.EscapeDataString(token)}", expires, false, streamUrl, captions, language));
        var direct = await storage.CreateTemporaryUrlAsync(asset.StorageKey, asset.ContentType, asset.OriginalFileName, true, cancellationToken);
        if (direct is not null)
            return Results.Ok(new PlaybackLink("direct", direct.ToString(), DateTimeOffset.UtcNow.Add(VideoPlaybackTokens.Lifetime), false));
        return Results.Ok(new PlaybackLink("stream", streamUrl, expires, false, null, captions, language));
    }

    private static async Task<IResult> StreamAsync(HttpContext httpContext, LmsDbContext db, ITenantContext tenantContext, IContentAssetStorage storage, VideoPlaybackTokens tokens, Guid videoId, string? token, CancellationToken cancellationToken)
    {
        if (tokens.Read(token) is not { } claims || claims.VideoId != videoId) return Results.NotFound();
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(item => item.Id == claims.TenantId, cancellationToken);
        if (tenant is null || tenant.Status != Domain.Tenants.TenantStatus.Active) return Results.NotFound();
        tenantContext.Set(tenant.Id, tenant.Slug); // the token, not a header, decides which organization this is
        var video = await db.Videos.AsNoTracking().SingleOrDefaultAsync(item => item.Id == videoId && item.Type != VideoType.External, cancellationToken);
        var asset = video?.ContentAssetId is Guid assetId ? await db.ContentAssets.AsNoTracking().SingleOrDefaultAsync(item => item.Id == assetId, cancellationToken) : null;
        if (video is null || asset is null) return Results.NotFound();
        var stream = await storage.OpenReadAsync(asset.StorageKey, cancellationToken);
        if (stream is null) return Results.NotFound();
        httpContext.Response.Headers.CacheControl = "private, no-store";
        httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.Stream(stream, asset.ContentType, enableRangeProcessing: stream.CanSeek); // range support is what lets people seek
    }

    /// <summary>The video a signed token stands for, or null. Sets the organization for the rest of the request.</summary>
    private static async Task<Video?> VideoFromTokenAsync(LmsDbContext db, ITenantContext tenantContext, VideoPlaybackTokens tokens, Guid videoId, string? token, CancellationToken cancellationToken)
    {
        if (tokens.Read(token) is not { } claims || claims.VideoId != videoId) return null;
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(item => item.Id == claims.TenantId, cancellationToken);
        if (tenant is null || tenant.Status != Domain.Tenants.TenantStatus.Active) return null;
        tenantContext.Set(tenant.Id, tenant.Slug);
        return await db.Videos.AsNoTracking().SingleOrDefaultAsync(item => item.Id == videoId && item.Type != VideoType.External, cancellationToken);
    }

    private static async Task<IResult> HlsAsync(HttpContext httpContext, LmsDbContext db, ITenantContext tenantContext, IContentAssetStorage storage, VideoPlaybackTokens tokens, Guid videoId, string file, string? token, CancellationToken cancellationToken)
    {
        if (!VideoFiles.AllowedName.IsMatch(file) || file == VideoFiles.Poster) return Results.NotFound();
        var video = await VideoFromTokenAsync(db, tenantContext, tokens, videoId, token, cancellationToken);
        if (video is null || !VideoFiles.HasStreaming(video) || video.Status != VideoStatus.Ready || !VideoFiles.Exists(video, file)) return Results.NotFound();
        await using var stream = await storage.OpenReadAsync(VideoFiles.Key(video.TenantId, video.CourseId, video.Id, file), cancellationToken);
        if (stream is null) return Results.NotFound();
        httpContext.Response.Headers.CacheControl = "private, no-store";
        httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
        var buffer = new MemoryStream();
        if (!file.EndsWith(".m3u8", StringComparison.Ordinal))
        {
            await stream.CopyToAsync(buffer, cancellationToken);
            buffer.Position = 0;
            return Results.File(buffer, VideoFiles.ContentType(file), enableRangeProcessing: true);
        }
        // Players ask for each segment by itself, so every address in the playlist carries the same signed token.
        using var reader = new StreamReader(stream);
        var lines = (await reader.ReadToEndAsync(cancellationToken)).Split('\n').Select(line => line.TrimEnd('\r'))
            .Select(line => line.Length > 0 && !line.StartsWith('#') ? $"{line}?token={Uri.EscapeDataString(token!)}" : line);
        return Results.Text(string.Join('\n', lines), VideoFiles.ContentType(file));
    }

    /// <summary>The transcript as a WebVTT captions file, for the player's own captions button.</summary>
    private static async Task<IResult> CaptionsAsync(HttpContext httpContext, LmsDbContext db, ITenantContext tenantContext, VideoPlaybackTokens tokens, Guid videoId, string? token, CancellationToken cancellationToken)
    {
        var video = await VideoFromTokenAsync(db, tenantContext, tokens, videoId, token, cancellationToken);
        if (video is null || video.Status != VideoStatus.Ready) return Results.NotFound();
        if (!await db.VideoTranscripts.AsNoTracking().AnyAsync(item => item.VideoId == videoId && item.Status == TranscriptStatus.Ready, cancellationToken)) return Results.NotFound();
        var segments = await db.VideoTranscriptSegments.AsNoTracking().Where(item => item.VideoId == videoId).OrderBy(item => item.Index).ToListAsync(cancellationToken);
        httpContext.Response.Headers.CacheControl = "private, no-store";
        httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.Text(CaptionFile.Build(segments.Select(item => new TranscriptCue(item.StartMs, item.EndMs, item.Text))), "text/vtt; charset=utf-8");
    }

    private static async Task<IResult> PosterAsync(HttpContext httpContext, LmsDbContext db, ITenantContext tenantContext, IContentAssetStorage storage, VideoPlaybackTokens tokens, Guid videoId, string? token, CancellationToken cancellationToken)
    {
        var video = await VideoFromTokenAsync(db, tenantContext, tokens, videoId, token, cancellationToken);
        if (video is null || !video.HasPoster) return Results.NotFound();
        var stream = await storage.OpenReadAsync(VideoFiles.Key(video.TenantId, video.CourseId, video.Id, VideoFiles.Poster), cancellationToken);
        if (stream is null) return Results.NotFound();
        httpContext.Response.Headers.CacheControl = "private, max-age=300";
        httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.Stream(stream, "image/jpeg");
    }

    // ---------- progress and analytics ----------
    private static async Task<IResult> ProgressAsync(HttpContext httpContext, LmsDbContext db, ITenantContext tenantContext, Guid videoId, ProgressRequest request, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId || tenantContext.TenantId is not Guid tenantId) return Results.Unauthorized();
        var video = await (await VisibleAsync(httpContext, db, userId, cancellationToken)).SingleOrDefaultAsync(item => item.Id == videoId, cancellationToken);
        if (video is null || video.Status != VideoStatus.Ready) return Results.NotFound();

        var duration = request.DurationSeconds is > 0 and <= MaxDurationSeconds ? request.DurationSeconds : null;
        var tracked = await db.Videos.SingleAsync(item => item.Id == videoId, cancellationToken);
        if (tracked.DurationSeconds is null && duration is not null) tracked.DurationSeconds = duration; // the first player to know the length tells us
        var length = tracked.DurationSeconds;

        var now = DateTimeOffset.UtcNow;
        var watch = await db.VideoWatches.SingleOrDefaultAsync(item => item.VideoId == videoId && item.UserId == userId, cancellationToken);
        if (watch is null)
        {
            watch = new VideoWatch { Id = Guid.NewGuid(), TenantId = tenantId, VideoId = videoId, UserId = userId, FirstWatchedAtUtc = now };
            db.VideoWatches.Add(watch);
        }
        var position = Math.Clamp(request.PositionSeconds, 0, length ?? MaxDurationSeconds);
        watch.LastPositionSeconds = position;
        watch.MaxPositionSeconds = Math.Max(watch.MaxPositionSeconds, position);
        watch.WatchedSeconds += Math.Clamp(request.WatchedSecondsDelta ?? 0, 0, 120); // a report covers a short interval; never trust a huge one
        if (request.Started == true) watch.Plays++;
        if (request.Completed == true || (length is > 0 && watch.MaxPositionSeconds >= length * 0.9)) watch.Completed = true;
        watch.UpdatedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToProgress(watch, length));
    }

    private static async Task<IResult> AnalyticsAsync(LmsDbContext db, Guid videoId, CancellationToken cancellationToken)
    {
        var video = await db.Videos.AsNoTracking().SingleOrDefaultAsync(item => item.Id == videoId, cancellationToken);
        if (video is null) return Results.NotFound();
        var eligible = await db.Enrollments.AsNoTracking().CountAsync(item => item.CourseId == video.CourseId && (item.Status == EnrollmentStatus.Active || item.Status == EnrollmentStatus.Completed), cancellationToken);
        var watches = await db.VideoWatches.AsNoTracking().Where(item => item.VideoId == videoId).ToListAsync(cancellationToken);
        var started = watches.Where(item => item.Plays > 0 || item.MaxPositionSeconds > 0 || item.WatchedSeconds > 0).ToList();
        var length = video.DurationSeconds;
        int Reached(double fraction) => length is > 0 ? started.Count(item => item.MaxPositionSeconds >= length * fraction) : 0;
        int? average = length is > 0 && started.Count > 0 ? (int)Math.Round(started.Average(item => Math.Min(100.0, item.WatchedSeconds * 100.0 / length.Value))) : null;
        return Results.Ok(new VideoAnalyticsResponse(video.Id, video.Title, length, eligible, started.Count, watches.Count(item => item.Completed),
            started.Sum(item => item.Plays), started.Sum(item => item.WatchedSeconds), average,
            length is > 0 ? new[] { new FunnelStep(25, Reached(0.25)), new FunnelStep(50, Reached(0.5)), new FunnelStep(75, Reached(0.75)), new FunnelStep(100, watches.Count(item => item.Completed)) } : []));
    }

    // ---------- writing ----------
    private static async Task<IResult> UploadAsync(HttpRequest request, LmsDbContext db, ITenantContext tenantContext, IContentAssetStorage storage, IVideoTranscoder transcoder, CancellationToken cancellationToken)
    {
        var httpContext = request.HttpContext;
        if (tenantContext.TenantId is not Guid tenantId || GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        if (!request.HasFormContentType) return Problem("Send the video as a form with a file.");
        var form = await request.ReadFormAsync(cancellationToken);
        var file = form.Files.FirstOrDefault();
        if (file is null || file.Length == 0) return Problem("Choose a video file.");
        if (!Guid.TryParse(form["courseId"], out var courseId)) return Problem("Choose the course this video belongs to.");
        var title = form["title"].ToString().Trim();
        var description = form["description"].ToString().Trim();
        var checkedFields = await CheckFieldsAsync(db, courseId, form["lessonId"], title, description, cancellationToken);
        if (checkedFields.Error is not null) return Problem(checkedFields.Error);

        var header = new byte[16];
        await using (var stream = file.OpenReadStream()) _ = await stream.ReadAsync(header, cancellationToken);
        var fileError = BlockFileRules.Validate(BlockType.Video, file.ContentType, header);
        if (fileError is not null) return Problem(fileError);

        var stored = await storage.SaveAsync(tenantId, courseId, file, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var asset = new ContentAsset
        {
            Id = Guid.NewGuid(), TenantId = tenantId, CourseId = courseId, CourseVersionId = null, OriginalFileName = stored.OriginalFileName, StorageKey = stored.StorageKey,
            ContentType = stored.ContentType, SizeBytes = stored.SizeBytes, Sha256 = stored.Sha256, CreatedByUserId = userId, CreatedAtUtc = now
        };
        var video = new Video
        {
            Id = Guid.NewGuid(), TenantId = tenantId, CourseId = courseId, LessonId = checkedFields.LessonId, Title = title, Description = description.Length == 0 ? null : description,
            Type = VideoType.Uploaded, Status = transcoder.Enabled ? VideoStatus.Processing : VideoStatus.Ready, ContentAssetId = asset.Id, ContentType = stored.ContentType, SizeBytes = stored.SizeBytes,
            DurationSeconds = int.TryParse(form["durationSeconds"], out var seconds) && seconds is > 0 and <= MaxDurationSeconds ? seconds : null,
            CreatedByUserId = userId, CreatedAtUtc = now, UpdatedAtUtc = now
        };
        db.ContentAssets.Add(asset);
        db.Videos.Add(video);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/videos/{video.Id:D}", (await ToResponsesAsync(httpContext, db, [video], userId, cancellationToken))[0]);
    }

    private static async Task<IResult> CreateExternalAsync(HttpContext httpContext, LmsDbContext db, ITenantContext tenantContext, IConfiguration configuration, ExternalVideoRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var url = LiveClassUrls.NormalizeHttps(request.Url, configuration.GetValue("Integrations:AllowInsecureLiveClassHosts", false));
        if (url is null) return Problem("Paste the video’s link (an https address).");
        var checkedFields = await CheckFieldsAsync(db, request.CourseId, request.LessonId?.ToString(), request.Title?.Trim() ?? string.Empty, request.Description?.Trim() ?? string.Empty, cancellationToken);
        if (checkedFields.Error is not null) return Problem(checkedFields.Error);
        var now = DateTimeOffset.UtcNow;
        var video = new Video
        {
            Id = Guid.NewGuid(), TenantId = tenantId, CourseId = request.CourseId, LessonId = checkedFields.LessonId, Title = request.Title!.Trim(), Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            Type = VideoType.External, Status = VideoStatus.Ready, ExternalUrl = url, DurationSeconds = request.DurationSeconds is > 0 and <= MaxDurationSeconds ? request.DurationSeconds : null,
            CreatedByUserId = userId, CreatedAtUtc = now, UpdatedAtUtc = now
        };
        db.Videos.Add(video);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/videos/{video.Id:D}", (await ToResponsesAsync(httpContext, db, [video], userId, cancellationToken))[0]);
    }

    private static async Task<IResult> UpdateAsync(HttpContext httpContext, LmsDbContext db, Guid videoId, UpdateVideoRequest request, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var video = await db.Videos.SingleOrDefaultAsync(item => item.Id == videoId, cancellationToken);
        if (video is null) return Results.NotFound();
        var title = request.Title?.Trim() ?? string.Empty;
        if (title.Length is 0 or > 250) return Problem("Give the video a title of 250 characters or fewer.");
        if (request.Description is { Length: > 2000 }) return Problem("The description must be 2000 characters or fewer.");
        video.Title = title;
        video.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (request.DurationSeconds is > 0 and <= MaxDurationSeconds) video.DurationSeconds = request.DurationSeconds;
        video.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok((await ToResponsesAsync(httpContext, db, [video], userId, cancellationToken))[0]);
    }

    /// <summary>Puts the video into a lesson as a content block, so learners meet it where they learn. The lesson must be in the version that can be edited.</summary>
    private static async Task<IResult> AttachAsync(HttpContext httpContext, LmsDbContext db, IConfiguration configuration, Guid videoId, AttachRequest request, CancellationToken cancellationToken)
    {
        var video = await db.Videos.SingleOrDefaultAsync(item => item.Id == videoId, cancellationToken);
        if (video is null) return Results.NotFound();
        var course = await db.Courses.AsNoTracking().SingleOrDefaultAsync(item => item.Id == video.CourseId, cancellationToken);
        if (course is null) return Results.NotFound();
        var lesson = await (from l in db.CourseLessons.AsNoTracking()
                            join m in db.CourseModules.AsNoTracking() on l.CourseModuleId equals m.Id
                            where l.Id == request.LessonId
                            select new { Lesson = l, m.CourseVersionId }).SingleOrDefaultAsync(cancellationToken);
        if (lesson is null) return Results.NotFound(new { message = "That lesson was not found." });
        if (!await db.CourseModules.AnyAsync(item => item.Id == lesson.Lesson.CourseModuleId && db.CourseVersions.Any(version => version.Id == item.CourseVersionId && version.CourseId == video.CourseId), cancellationToken))
            return Problem("That lesson belongs to a different course than the video.");
        if (await CourseVersionRules.EditableVersionIdAsync(db, course, cancellationToken) != lesson.CourseVersionId)
            return Results.Conflict(new { message = "Content can only be added while the course is a draft, or in a new version that has not been submitted for review." });
        if (video.Status != VideoStatus.Ready) return Results.Conflict(new { message = "The video is not ready yet." });
        if (await db.LessonBlocks.CountAsync(item => item.CourseLessonId == request.LessonId, cancellationToken) >= MaxBlocksPerLesson) return Problem($"A lesson can have at most {MaxBlocksPerLesson} blocks.");

        var now = DateTimeOffset.UtcNow;
        var order = (await db.LessonBlocks.Where(item => item.CourseLessonId == request.LessonId).Select(item => (int?)item.DisplayOrder).MaxAsync(cancellationToken) ?? 0) + 1;
        var block = new LessonBlock { Id = Guid.NewGuid(), TenantId = video.TenantId, CourseLessonId = request.LessonId, DisplayOrder = order, Title = video.Title, CreatedAtUtc = now, UpdatedAtUtc = now };
        if (video.Type == VideoType.External)
        {
            block.Type = IsEmbeddable(video.ExternalUrl, configuration) ? BlockType.Embed : BlockType.Link;
            block.Url = video.ExternalUrl;
        }
        else { block.Type = BlockType.Video; block.ContentAssetId = video.ContentAssetId; }
        db.LessonBlocks.Add(block);
        video.LessonId = request.LessonId;
        video.UpdatedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/courses/{video.CourseId}/lessons/{request.LessonId}/blocks", new AttachedBlock(block.Id, block.Type.ToString(), request.LessonId));
    }

    /// <summary>Converts the video again: for one that failed, or an older upload that has no streaming version yet.</summary>
    private static async Task<IResult> ReprocessAsync(HttpContext httpContext, LmsDbContext db, IVideoTranscoder transcoder, Guid videoId, CancellationToken cancellationToken)
    {
        var video = await db.Videos.SingleOrDefaultAsync(item => item.Id == videoId, cancellationToken);
        if (video is null) return Results.NotFound();
        if (video.Type == VideoType.External) return Problem("Only uploaded videos and class recordings can be converted.");
        if (!transcoder.Enabled) return Results.Conflict(new { message = "Video conversion is not set up on this server." });
        if (video.Status == VideoStatus.Processing) return Results.Conflict(new { message = "The video is already being converted." });
        video.Status = VideoStatus.Processing;
        video.StatusMessage = null;
        video.ProcessingAttempts = 0;
        video.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Accepted($"/api/v1/tenant/videos/{video.Id:D}", (await ToResponsesAsync(httpContext, db, [video], GetUserId(httpContext) ?? Guid.Empty, cancellationToken))[0]);
    }

    private static async Task<IResult> DeleteAsync(LmsDbContext db, IContentAssetStorage storage, Guid videoId, CancellationToken cancellationToken)
    {
        var video = await db.Videos.SingleOrDefaultAsync(item => item.Id == videoId, cancellationToken);
        if (video is null) return Results.NotFound();
        string? keyToDelete = null;
        if (video.ContentAssetId is Guid assetId)
        {
            // A lesson that shows this video keeps working: the file stays while any block still uses it.
            var asset = await db.ContentAssets.SingleOrDefaultAsync(item => item.Id == assetId, cancellationToken);
            if (asset is not null && !await db.LessonBlocks.AnyAsync(item => item.ContentAssetId == assetId, cancellationToken))
            {
                keyToDelete = asset.StorageKey;
                db.ContentAssets.Remove(asset);
            }
        }
        // What was made from the video goes with it (the database cascades too; this keeps every provider consistent).
        db.VideoTranscriptSegments.RemoveRange(await db.VideoTranscriptSegments.Where(item => item.VideoId == videoId).ToListAsync(cancellationToken));
        db.VideoTranscripts.RemoveRange(await db.VideoTranscripts.Where(item => item.VideoId == videoId).ToListAsync(cancellationToken));
        db.VideoInsights.RemoveRange(await db.VideoInsights.Where(item => item.VideoId == videoId).ToListAsync(cancellationToken));
        db.Videos.Remove(video); // watch history goes with it
        await db.SaveChangesAsync(cancellationToken);
        if (keyToDelete is not null) await storage.DeleteAsync(keyToDelete, cancellationToken);
        await VideoProcessingService.DeleteDerivedFilesAsync(storage, video, cancellationToken);
        return Results.NoContent();
    }

    // ---------- helpers ----------
    /// <summary>Everything the signed-in person may see: staff see all; everyone else only ready videos of published courses they are enrolled in.</summary>
    internal static async Task<IQueryable<Video>> VisibleAsync(HttpContext httpContext, LmsDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        if (CanManage(httpContext)) return db.Videos.AsNoTracking();
        var courseIds = await db.Enrollments.AsNoTracking()
            .Where(item => item.LearnerUserId == userId && (item.Status == EnrollmentStatus.Active || item.Status == EnrollmentStatus.Completed))
            .Select(item => item.CourseId).ToListAsync(cancellationToken);
        var published = await db.Courses.AsNoTracking().Where(item => courseIds.Contains(item.Id) && item.Status == CourseStatus.Published).Select(item => item.Id).ToListAsync(cancellationToken);
        return db.Videos.AsNoTracking().Where(item => published.Contains(item.CourseId) && item.Status == VideoStatus.Ready);
    }

    private static async Task<(string? Error, Guid? LessonId)> CheckFieldsAsync(LmsDbContext db, Guid courseId, string? lessonText, string title, string description, CancellationToken cancellationToken)
    {
        if (title.Length is 0 or > 250) return ("Give the video a title of 250 characters or fewer.", null);
        if (description.Length > 2000) return ("The description must be 2000 characters or fewer.", null);
        var course = await db.Courses.AsNoTracking().SingleOrDefaultAsync(item => item.Id == courseId, cancellationToken);
        if (course is null) return ("That course was not found.", null);
        if (course.Status == CourseStatus.Archived) return ("An archived course cannot get new videos.", null);
        if (string.IsNullOrWhiteSpace(lessonText)) return (null, null);
        if (!Guid.TryParse(lessonText, out var lessonId)) return ("That lesson was not found.", null);
        var inCourse = await db.CourseLessons.AsNoTracking().AnyAsync(l => l.Id == lessonId && db.CourseModules.Any(m => m.Id == l.CourseModuleId && db.CourseVersions.Any(v => v.Id == m.CourseVersionId && v.CourseId == courseId)), cancellationToken);
        return inCourse ? (null, lessonId) : ("That lesson does not belong to the course.", null);
    }

    private static bool IsEmbeddable(string? url, IConfiguration configuration)
    {
        var hosts = configuration.GetSection("Content:EmbedHosts").Get<string[]>() is { Length: > 0 } configured ? configured : DefaultEmbedHosts;
        return BlockFileRules.IsEmbedAllowed(url, hosts);
    }

    private static async Task<List<VideoResponse>> ToResponsesAsync(HttpContext httpContext, LmsDbContext db, List<Video> videos, Guid userId, CancellationToken cancellationToken)
    {
        var courseIds = videos.Select(item => item.CourseId).Distinct().ToList();
        var courses = await db.Courses.AsNoTracking().Where(item => courseIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, item => item.Title, cancellationToken);
        var creatorIds = videos.Select(item => item.CreatedByUserId).Distinct().ToList();
        var creators = await db.Users.AsNoTracking().Where(item => creatorIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, item => item.DisplayName, cancellationToken);
        var videoIds = videos.Select(item => item.Id).ToList();
        var mine = await db.VideoWatches.AsNoTracking().Where(item => item.UserId == userId && videoIds.Contains(item.VideoId)).ToDictionaryAsync(item => item.VideoId, cancellationToken);
        var tokens = httpContext.RequestServices.GetRequiredService<VideoPlaybackTokens>();
        var tenantId = httpContext.RequestServices.GetRequiredService<ITenantContext>().TenantId ?? Guid.Empty;
        string? PosterUrl(Video item) => item.HasPoster ? $"/api/v1/tenant/videos/{item.Id:D}/poster?token={Uri.EscapeDataString(tokens.Create(tenantId, item.Id, userId).Token)}" : null;
        return videos.Select(item => new VideoResponse(item.Id, item.CourseId, courses.GetValueOrDefault(item.CourseId, "Course"), item.LessonId, item.Title, item.Description, item.Type.ToString(), item.Status.ToString(), item.StatusMessage,
            item.ContentType, item.SizeBytes, item.DurationSeconds, item.ExternalUrl, creators.GetValueOrDefault(item.CreatedByUserId, "Unknown"), item.CreatedAtUtc,
            mine.TryGetValue(item.Id, out var watch) ? ToProgress(watch, item.DurationSeconds) : null, item.HlsSegmentCount is > 0, PosterUrl(item))).ToList();
    }

    private static VideoProgress ToProgress(VideoWatch watch, int? length)
        => new(watch.LastPositionSeconds, length is > 0 ? Math.Min(100, (int)Math.Round(watch.MaxPositionSeconds * 100.0 / length.Value)) : null, watch.Completed);

    internal static bool CanManage(HttpContext context) => context.User.Claims.Any(claim => claim.Type == "permission" && claim.Value.Equals(LmsPermissions.CourseManage, StringComparison.OrdinalIgnoreCase));
    internal static Guid? GetUserId(HttpContext context) => Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub"), out var id) ? id : null;
    internal static IResult Problem(string message) => Results.BadRequest(new { message });
}

public sealed record ExternalVideoRequest(Guid CourseId, string? Title, string? Description, string? Url, Guid? LessonId = null, int? DurationSeconds = null);
public sealed record UpdateVideoRequest(string? Title, string? Description, int? DurationSeconds = null);
public sealed record AttachRequest(Guid LessonId);
public sealed record ProgressRequest(int PositionSeconds, int? DurationSeconds = null, int? WatchedSecondsDelta = null, bool? Started = null, bool? Completed = null);
public sealed record VideoProgress(int LastPositionSeconds, int? Percent, bool Completed);
public sealed record PlaybackLink(string Kind, string Url, DateTimeOffset? ExpiresAtUtc, bool Embeddable, string? FallbackUrl = null, string? CaptionsUrl = null, string? CaptionsLanguage = null);
public sealed record AttachedBlock(Guid BlockId, string BlockType, Guid LessonId);
public sealed record VideoResponse(Guid Id, Guid CourseId, string CourseTitle, Guid? LessonId, string Title, string? Description, string Type, string Status, string? StatusMessage,
    string? ContentType, long SizeBytes, int? DurationSeconds, string? ExternalUrl, string CreatedBy, DateTimeOffset CreatedAtUtc, VideoProgress? MyProgress, bool HasStreaming = false, string? PosterUrl = null);
public sealed record VideoUsageGroup(string Name, int Count, long SizeBytes);
public sealed record VideoUsageResponse(int Count, long TotalBytes, VideoUsageGroup[] ByType, VideoUsageGroup[] ByStatus);
public sealed record FunnelStep(int Percent, int Viewers);
public sealed record VideoAnalyticsResponse(Guid VideoId, string Title, int? DurationSeconds, int EligibleLearners, int Viewers, int Completed, int Plays, int WatchedSeconds, int? AverageWatchedPercent, FunnelStep[] Funnel);
