using Lms.Api.Domain.Courses;
using Lms.Api.Domain.Videos;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Storage;
using Lms.Api.Infrastructure.Tenancy;
using Lms.Api.Infrastructure.Videos;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Videos;

/// <summary>
/// Uploading a video in pieces. The page asks for an upload, sends the pieces one after another and then asks for them to be joined.
/// A piece that fails is sent again; a closed tab or a lost connection is picked up from the pieces already stored (ask for the upload to see which).
/// </summary>
public static class VideoUploadEndpoints
{
    public static void MapVideoUploadEndpoints(this WebApplication app)
    {
        var chunkSize = ChunkSize(app.Configuration);
        var limit = Math.Clamp(app.Configuration.GetValue("Videos:MaxMegabytes", 1024L), 1L, 20_480L) * 1024 * 1024;

        var uploads = app.MapGroup("/api/v1/tenant/videos/uploads").RequireAuthorization("tenant.authenticated");
        uploads.AddEndpointFilter(async (context, next) =>
        {
            if (!context.HttpContext.RequestServices.GetRequiredService<ITenantContext>().IsResolved)
                return Results.BadRequest(new { message = "A tenant is required. Use the X-Tenant-Slug header or a tenant subdomain." });
            return await next(context);
        });
        uploads.MapPost("", CreateAsync).RequireAuthorization("tenant.course.manage");
        uploads.MapGet("/{uploadId:guid}", GetAsync).RequireAuthorization("tenant.course.manage");
        uploads.MapPut("/{uploadId:guid}/chunks/{index:int}", PutChunkAsync).RequireAuthorization("tenant.course.manage")
            .WithMetadata(new RequestSizeLimitAttribute(chunkSize + 1024 * 1024));
        uploads.MapPost("/{uploadId:guid}/complete", CompleteAsync).RequireAuthorization("tenant.course.manage")
            .WithMetadata(new RequestSizeLimitAttribute(limit));
        uploads.MapDelete("/{uploadId:guid}", AbortAsync).RequireAuthorization("tenant.course.manage");
    }

    public static int ChunkSize(IConfiguration configuration) => (int)(Math.Clamp(configuration.GetValue("Videos:ChunkMegabytes", 8), 1, 64) * 1024L * 1024L);
    private static TimeSpan Lifetime(IConfiguration configuration) => TimeSpan.FromHours(Math.Clamp(configuration.GetValue("Videos:UploadExpiryHours", 48), 1, 24 * 14));
    private static string Key(VideoUpload upload, int index) => $"{upload.TenantId:D}/uploads/{upload.Id:D}/{index}";

    private static async Task<IResult> CreateAsync(HttpContext httpContext, LmsDbContext db, ITenantContext tenantContext, IContentAssetStorage storage, IConfiguration configuration, CreateUploadRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || VideoEndpoints.GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var limit = Math.Clamp(configuration.GetValue("Videos:MaxMegabytes", 1024L), 1L, 20_480L) * 1024 * 1024;
        if (request.SizeBytes <= 0) return VideoEndpoints.Problem("Choose a video file.");
        if (request.SizeBytes > limit) return Results.Json(new { message = $"The video is larger than the limit of {limit / (1024 * 1024)} MB." }, statusCode: StatusCodes.Status413PayloadTooLarge);
        if (await VideoEndpoints.QuotaProblemAsync(db, configuration, request.SizeBytes, cancellationToken) is { } full) return Results.Json(new { message = full }, statusCode: StatusCodes.Status413PayloadTooLarge);
        var fileName = Path.GetFileName(request.FileName ?? string.Empty).Trim();
        if (fileName.Length is 0 or > 255) return VideoEndpoints.Problem("The file needs a name of 255 characters or fewer.");
        var contentType = (request.ContentType ?? string.Empty).Trim();
        if (!contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || contentType.Length > 150) return VideoEndpoints.Problem("Choose a video file.");
        var checkedFields = await VideoEndpoints.CheckFieldsAsync(db, request.CourseId, request.LessonId?.ToString(), request.Title?.Trim() ?? string.Empty, request.Description?.Trim() ?? string.Empty, cancellationToken);
        if (checkedFields.Error is not null) return VideoEndpoints.Problem(checkedFields.Error);

        await PurgeExpiredAsync(db, storage, configuration, cancellationToken);
        var size = ChunkSize(configuration);
        var total = (int)((request.SizeBytes + size - 1) / size);
        var now = DateTimeOffset.UtcNow;
        var upload = new VideoUpload
        {
            Id = Guid.NewGuid(), TenantId = tenantId, UserId = userId, CourseId = request.CourseId, LessonId = checkedFields.LessonId, Title = request.Title!.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(), FileName = fileName, ContentType = contentType, SizeBytes = request.SizeBytes,
            DurationSeconds = request.DurationSeconds is > 0 and <= VideoEndpoints.MaxDurationSeconds ? request.DurationSeconds : null,
            ChunkSize = size, TotalChunks = total, ChunkMap = new string('0', total), CreatedAtUtc = now, UpdatedAtUtc = now
        };
        db.VideoUploads.Add(upload);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/videos/uploads/{upload.Id:D}", ToResponse(upload));
    }

    private static async Task<IResult> GetAsync(HttpContext httpContext, LmsDbContext db, Guid uploadId, CancellationToken cancellationToken)
    {
        var upload = await Mine(httpContext, db, uploadId, cancellationToken);
        return upload is null ? Results.NotFound() : Results.Ok(ToResponse(upload));
    }

    private static async Task<IResult> PutChunkAsync(HttpRequest request, LmsDbContext db, IContentAssetStorage storage, Guid uploadId, int index, CancellationToken cancellationToken)
    {
        var httpContext = request.HttpContext;
        var upload = await Mine(httpContext, db, uploadId, cancellationToken);
        if (upload is null) return Results.NotFound();
        if (index < 0 || index >= upload.TotalChunks) return VideoEndpoints.Problem("That piece is not part of this upload.");
        var expected = index == upload.TotalChunks - 1 ? upload.SizeBytes - (long)upload.ChunkSize * (upload.TotalChunks - 1) : upload.ChunkSize;
        var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length != expected) return VideoEndpoints.Problem($"Piece {index + 1} should be {expected} bytes but {buffer.Length} arrived. Send it again.");

        if (index == 0)
        {
            // The first piece shows what the file really is, so a file that is not a video is refused before the rest is sent.
            var header = buffer.GetBuffer().AsSpan(0, (int)Math.Min(16, buffer.Length)).ToArray();
            var padded = new byte[16]; header.CopyTo(padded, 0);
            var fileError = BlockFileRules.Validate(BlockType.Video, upload.ContentType, padded);
            if (fileError is not null) { await DeleteUploadAsync(db, storage, upload, cancellationToken); return VideoEndpoints.Problem(fileError); }
        }
        buffer.Position = 0;
        await storage.PutAsync(Key(upload, index), buffer, "application/octet-stream", cancellationToken);
        var map = upload.ChunkMap.ToCharArray(); map[index] = '1';
        upload.ChunkMap = new string(map);
        upload.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToResponse(upload));
    }

    private static async Task<IResult> CompleteAsync(HttpContext httpContext, LmsDbContext db, ITenantContext tenantContext, IContentAssetStorage storage, IVideoTranscoder transcoder, Guid uploadId, CancellationToken cancellationToken)
    {
        var upload = await Mine(httpContext, db, uploadId, cancellationToken);
        if (upload is null || tenantContext.TenantId is not Guid tenantId) return Results.NotFound();
        var missing = upload.ChunkMap.Count(item => item != '1');
        if (missing > 0) return Results.Conflict(new { message = $"{missing} piece{(missing == 1 ? "" : "s")} of the video {(missing == 1 ? "has" : "have")} not arrived yet.", missing });
        var course = await db.Courses.AsNoTracking().SingleOrDefaultAsync(item => item.Id == upload.CourseId, cancellationToken);
        if (course is null || course.Status == CourseStatus.Archived) { await DeleteUploadAsync(db, storage, upload, cancellationToken); return VideoEndpoints.Problem("That course is not available for new videos."); }

        // Join the pieces in a temporary file, then hand it to storage like any upload (which checks it with the virus scanner and picks the bucket).
        var temp = Path.Combine(Path.GetTempPath(), $"lms-upload-{Guid.NewGuid():N}");
        try
        {
            await using (var joined = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
                for (var index = 0; index < upload.TotalChunks; index++)
                {
                    await using var piece = await storage.OpenReadAsync(Key(upload, index), cancellationToken);
                    if (piece is null) return Results.Conflict(new { message = $"Piece {index + 1} is no longer stored. Send the video again.", missing = 1 });
                    await piece.CopyToAsync(joined, cancellationToken);
                }
            if (new FileInfo(temp).Length != upload.SizeBytes) return Results.Conflict(new { message = "The pieces do not add up to the size of the video. Send it again." });

            StoredAsset stored;
            await using (var source = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true))
            {
                var file = new FormFile(source, 0, source.Length, "file", upload.FileName) { Headers = new HeaderDictionary(), ContentType = upload.ContentType };
                stored = await storage.SaveAsync(tenantId, upload.CourseId, file, cancellationToken);
            }
            var now = DateTimeOffset.UtcNow;
            var asset = new ContentAsset
            {
                Id = Guid.NewGuid(), TenantId = tenantId, CourseId = upload.CourseId, CourseVersionId = null, OriginalFileName = stored.OriginalFileName, StorageKey = stored.StorageKey,
                ContentType = stored.ContentType, SizeBytes = stored.SizeBytes, Sha256 = stored.Sha256, CreatedByUserId = upload.UserId, CreatedAtUtc = now
            };
            var video = new Video
            {
                Id = Guid.NewGuid(), TenantId = tenantId, CourseId = upload.CourseId, LessonId = upload.LessonId, Title = upload.Title, Description = upload.Description,
                Type = VideoType.Uploaded, Status = transcoder.Enabled ? VideoStatus.Processing : VideoStatus.Ready, ContentAssetId = asset.Id, ContentType = stored.ContentType, SizeBytes = stored.SizeBytes,
                DurationSeconds = upload.DurationSeconds, CreatedByUserId = upload.UserId, CreatedAtUtc = now, UpdatedAtUtc = now
            };
            db.ContentAssets.Add(asset);
            db.Videos.Add(video);
            await db.SaveChangesAsync(cancellationToken);
            await DeleteUploadAsync(db, storage, upload, cancellationToken);
            return Results.Created($"/api/v1/tenant/videos/{video.Id:D}", (await VideoEndpoints.ToResponsesAsync(httpContext, db, [video], upload.UserId, cancellationToken))[0]);
        }
        finally { try { File.Delete(temp); } catch (IOException) { /* a leftover in the temporary folder is harmless */ } }
    }

    private static async Task<IResult> AbortAsync(HttpContext httpContext, LmsDbContext db, IContentAssetStorage storage, Guid uploadId, CancellationToken cancellationToken)
    {
        var upload = await Mine(httpContext, db, uploadId, cancellationToken);
        if (upload is null) return Results.NotFound();
        await DeleteUploadAsync(db, storage, upload, cancellationToken);
        return Results.NoContent();
    }

    /// <summary>The upload, if it is the signed-in person's own. Someone else's upload looks like it does not exist.</summary>
    private static async Task<VideoUpload?> Mine(HttpContext httpContext, LmsDbContext db, Guid uploadId, CancellationToken cancellationToken)
        => VideoEndpoints.GetUserId(httpContext) is Guid userId ? await db.VideoUploads.SingleOrDefaultAsync(item => item.Id == uploadId && item.UserId == userId, cancellationToken) : null;

    private static async Task DeleteUploadAsync(LmsDbContext db, IContentAssetStorage storage, VideoUpload upload, CancellationToken cancellationToken)
    {
        for (var index = 0; index < upload.TotalChunks; index++) await storage.DeleteAsync(Key(upload, index), cancellationToken);
        db.VideoUploads.Remove(upload);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Uploads that were started and then abandoned do not keep their pieces forever.</summary>
    private static async Task PurgeExpiredAsync(LmsDbContext db, IContentAssetStorage storage, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow - Lifetime(configuration);
        foreach (var old in await db.VideoUploads.Where(item => item.UpdatedAtUtc < cutoff).Take(20).ToListAsync(cancellationToken))
            await DeleteUploadAsync(db, storage, old, cancellationToken);
    }

    private static UploadResponse ToResponse(VideoUpload upload)
        => new(upload.Id, upload.ChunkSize, upload.TotalChunks, upload.ChunkMap.Select((item, index) => (item, index)).Where(pair => pair.item == '1').Select(pair => pair.index).ToArray());
}

public sealed record CreateUploadRequest(Guid CourseId, string? Title, string? Description, Guid? LessonId, string? FileName, string? ContentType, long SizeBytes, int? DurationSeconds = null);
public sealed record UploadResponse(Guid Id, int ChunkSize, int TotalChunks, int[] Received);
