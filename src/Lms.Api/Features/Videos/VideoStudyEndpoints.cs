using Lms.Api.Domain.Videos;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Videos;

/// <summary>
/// Helping people find their way in a video: chapters that staff set (an outline anyone who can watch the video sees) and notes that learners keep for themselves
/// (a moment, with or without words; only the person who wrote them sees them).
/// </summary>
public static class VideoStudyEndpoints
{
    private const int MaxChapters = 60;
    private const int MaxNotesPerVideo = 300;

    public static void MapVideoStudyEndpoints(this WebApplication app)
    {
        var videos = app.MapGroup("/api/v1/tenant/videos").RequireAuthorization("tenant.authenticated");
        videos.AddEndpointFilter(async (context, next) =>
        {
            if (!context.HttpContext.RequestServices.GetRequiredService<ITenantContext>().IsResolved)
                return Results.BadRequest(new { message = "A tenant is required. Use the X-Tenant-Slug header or a tenant subdomain." });
            return await next(context);
        });
        videos.MapGet("/{videoId:guid}/chapters", ListChaptersAsync).RequireAuthorization("tenant.course.read");
        videos.MapPut("/{videoId:guid}/chapters", SaveChaptersAsync).RequireAuthorization("tenant.course.manage");
        videos.MapGet("/{videoId:guid}/notes", ListNotesAsync).RequireAuthorization("tenant.course.read");
        videos.MapPost("/{videoId:guid}/notes", AddNoteAsync).RequireAuthorization("tenant.course.read");
        videos.MapPut("/{videoId:guid}/notes/{noteId:guid}", UpdateNoteAsync).RequireAuthorization("tenant.course.read");
        videos.MapDelete("/{videoId:guid}/notes/{noteId:guid}", DeleteNoteAsync).RequireAuthorization("tenant.course.read");
    }

    private static async Task<Video?> WatchableAsync(HttpContext httpContext, LmsDbContext db, Guid videoId, CancellationToken cancellationToken)
    {
        if (VideoEndpoints.GetUserId(httpContext) is not Guid userId) return null;
        return await (await VideoEndpoints.VisibleAsync(httpContext, db, userId, cancellationToken)).SingleOrDefaultAsync(item => item.Id == videoId, cancellationToken);
    }

    // ---------- chapters ----------
    private static async Task<IResult> ListChaptersAsync(HttpContext httpContext, LmsDbContext db, Guid videoId, CancellationToken cancellationToken)
    {
        if (await WatchableAsync(httpContext, db, videoId, cancellationToken) is null) return Results.NotFound();
        var chapters = await db.VideoChapters.AsNoTracking().Where(item => item.VideoId == videoId).OrderBy(item => item.StartSeconds).ToListAsync(cancellationToken);
        return Results.Ok(chapters.Select(item => new ChapterResponse(item.StartSeconds, item.Title)).ToArray());
    }

    /// <summary>Replaces the outline. Each chapter starts at its own second; the first starts at the very beginning.</summary>
    private static async Task<IResult> SaveChaptersAsync(HttpContext httpContext, LmsDbContext db, ITenantContext tenantContext, Guid videoId, SaveChaptersRequest request, CancellationToken cancellationToken)
    {
        var video = await db.Videos.AsNoTracking().SingleOrDefaultAsync(item => item.Id == videoId, cancellationToken);
        if (video is null || tenantContext.TenantId is not Guid tenantId) return Results.NotFound();
        if (video.Type == VideoType.External) return VideoEndpoints.Problem("Chapters can only be set on videos held here, not on linked videos.");
        var chapters = (request.Chapters ?? []).Select(item => new ChapterResponse(item.StartSeconds, (item.Title ?? string.Empty).Trim())).OrderBy(item => item.StartSeconds).ToList();
        if (chapters.Count > MaxChapters) return VideoEndpoints.Problem($"A video can have at most {MaxChapters} chapters.");
        if (chapters.Any(item => item.Title.Length is 0 or > 120)) return VideoEndpoints.Problem("Every chapter needs a title of 120 characters or fewer.");
        var length = video.DurationSeconds ?? VideoEndpoints.MaxDurationSeconds;
        if (chapters.Any(item => item.StartSeconds < 0 || item.StartSeconds > length)) return VideoEndpoints.Problem("A chapter cannot start before the video begins or after it ends.");
        if (chapters.GroupBy(item => item.StartSeconds).Any(group => group.Count() > 1)) return VideoEndpoints.Problem("Two chapters cannot start at the same second.");
        if (chapters.Count > 0 && chapters[0].StartSeconds != 0) return VideoEndpoints.Problem("The first chapter must start at 0:00.");

        db.VideoChapters.RemoveRange(await db.VideoChapters.Where(item => item.VideoId == videoId).ToListAsync(cancellationToken));
        foreach (var chapter in chapters)
            db.VideoChapters.Add(new VideoChapter { Id = Guid.NewGuid(), TenantId = tenantId, VideoId = videoId, StartSeconds = chapter.StartSeconds, Title = chapter.Title });
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(chapters.ToArray());
    }

    // ---------- notes ----------
    private static async Task<IResult> ListNotesAsync(HttpContext httpContext, LmsDbContext db, Guid videoId, CancellationToken cancellationToken)
    {
        if (await WatchableAsync(httpContext, db, videoId, cancellationToken) is null || VideoEndpoints.GetUserId(httpContext) is not Guid userId) return Results.NotFound();
        var notes = await db.VideoNotes.AsNoTracking().Where(item => item.VideoId == videoId && item.UserId == userId).OrderBy(item => item.PositionSeconds).ThenBy(item => item.CreatedAtUtc).ToListAsync(cancellationToken);
        return Results.Ok(notes.Select(ToResponse).ToArray());
    }

    private static async Task<IResult> AddNoteAsync(HttpContext httpContext, LmsDbContext db, ITenantContext tenantContext, Guid videoId, NoteRequest request, CancellationToken cancellationToken)
    {
        var video = await WatchableAsync(httpContext, db, videoId, cancellationToken);
        if (video is null || VideoEndpoints.GetUserId(httpContext) is not Guid userId || tenantContext.TenantId is not Guid tenantId) return Results.NotFound();
        var problem = Check(request, video);
        if (problem is not null) return VideoEndpoints.Problem(problem);
        if (await db.VideoNotes.CountAsync(item => item.VideoId == videoId && item.UserId == userId, cancellationToken) >= MaxNotesPerVideo) return VideoEndpoints.Problem($"You can keep at most {MaxNotesPerVideo} notes on one video.");
        var now = DateTimeOffset.UtcNow;
        var note = new VideoNote { Id = Guid.NewGuid(), TenantId = tenantId, VideoId = videoId, UserId = userId, PositionSeconds = request.PositionSeconds, Text = (request.Text ?? string.Empty).Trim(), CreatedAtUtc = now, UpdatedAtUtc = now };
        db.VideoNotes.Add(note);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/videos/{videoId:D}/notes/{note.Id:D}", ToResponse(note));
    }

    private static async Task<IResult> UpdateNoteAsync(HttpContext httpContext, LmsDbContext db, Guid videoId, Guid noteId, NoteRequest request, CancellationToken cancellationToken)
    {
        var video = await WatchableAsync(httpContext, db, videoId, cancellationToken);
        if (video is null || VideoEndpoints.GetUserId(httpContext) is not Guid userId) return Results.NotFound();
        var note = await db.VideoNotes.SingleOrDefaultAsync(item => item.Id == noteId && item.VideoId == videoId && item.UserId == userId, cancellationToken);
        if (note is null) return Results.NotFound();
        var problem = Check(request, video);
        if (problem is not null) return VideoEndpoints.Problem(problem);
        note.PositionSeconds = request.PositionSeconds;
        note.Text = (request.Text ?? string.Empty).Trim();
        note.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToResponse(note));
    }

    private static async Task<IResult> DeleteNoteAsync(HttpContext httpContext, LmsDbContext db, Guid videoId, Guid noteId, CancellationToken cancellationToken)
    {
        if (VideoEndpoints.GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var note = await db.VideoNotes.SingleOrDefaultAsync(item => item.Id == noteId && item.VideoId == videoId && item.UserId == userId, cancellationToken);
        if (note is null) return Results.NotFound();
        db.VideoNotes.Remove(note);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static string? Check(NoteRequest request, Video video)
    {
        if (video.Type == VideoType.External) return "Notes can only be kept on videos held here, not on linked videos.";
        if (request.PositionSeconds < 0 || request.PositionSeconds > (video.DurationSeconds ?? VideoEndpoints.MaxDurationSeconds)) return "That moment is outside the video.";
        return (request.Text ?? string.Empty).Trim().Length > 1000 ? "A note can be at most 1000 characters." : null;
    }

    private static NoteResponse ToResponse(VideoNote note) => new(note.Id, note.PositionSeconds, note.Text, note.CreatedAtUtc, note.UpdatedAtUtc);
}

public sealed record ChapterResponse(int StartSeconds, string Title);
public sealed record SaveChaptersRequest(ChapterResponse[]? Chapters);
public sealed record NoteRequest(int PositionSeconds, string? Text);
public sealed record NoteResponse(Guid Id, int PositionSeconds, string Text, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
