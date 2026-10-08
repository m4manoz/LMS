using System.Security.Claims;
using Lms.Api.Domain.Community;
using Lms.Api.Domain.Identity;
using Lms.Api.Infrastructure.Notifications;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Storage;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Community;

/// <summary>Discussion forums (posts that can be edited, with the earlier versions kept, and carry attachments) and announcements.</summary>
public static class CommunityEndpoints
{
    private const int MaxTitle = 200;
    private const int MaxBody = 10000;
    private const int MaxAttachmentsPerPost = 5;

    public static void MapCommunityEndpoints(this WebApplication app)
    {
        var tenant = app.MapGroup("/api/v1/tenant/community").RequireAuthorization("tenant.authenticated");
        tenant.AddEndpointFilter(async (context, next) =>
        {
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (!tenantContext.IsResolved)
                return Results.BadRequest(new { message = "A tenant is required. Use the X-Tenant-Slug header or a tenant subdomain." });
            return await next(context);
        });

        tenant.MapGet("/threads", ListThreadsAsync).RequireAuthorization("tenant.collaboration.read");
        tenant.MapPost("/threads", CreateThreadAsync).RequireAuthorization("tenant.collaboration.manage");
        tenant.MapGet("/threads/{threadId:guid}", GetThreadAsync).RequireAuthorization("tenant.collaboration.read");
        tenant.MapPost("/threads/{threadId:guid}/replies", CreateReplyAsync).RequireAuthorization("tenant.collaboration.manage");
        tenant.MapPost("/threads/{threadId:guid}/pin", PinThreadAsync).RequireAuthorization("tenant.forum.moderate");
        tenant.MapPost("/threads/{threadId:guid}/lock", LockThreadAsync).RequireAuthorization("tenant.forum.moderate");
        tenant.MapDelete("/threads/{threadId:guid}", DeleteThreadAsync).RequireAuthorization("tenant.collaboration.manage");
        tenant.MapDelete("/replies/{replyId:guid}", DeleteReplyAsync).RequireAuthorization("tenant.collaboration.manage");
        tenant.MapPut("/threads/{threadId:guid}", EditThreadAsync).RequireAuthorization("tenant.collaboration.manage");
        tenant.MapPut("/replies/{replyId:guid}", EditReplyAsync).RequireAuthorization("tenant.collaboration.manage");
        tenant.MapGet("/threads/{threadId:guid}/history", ThreadHistoryAsync).RequireAuthorization("tenant.collaboration.read");
        tenant.MapGet("/replies/{replyId:guid}/history", ReplyHistoryAsync).RequireAuthorization("tenant.collaboration.read");
        tenant.MapPost("/threads/{threadId:guid}/attachments", AttachAsync).RequireAuthorization("tenant.collaboration.manage");
        tenant.MapGet("/attachments/{attachmentId:guid}", DownloadAttachmentAsync).RequireAuthorization("tenant.collaboration.read");
        tenant.MapDelete("/attachments/{attachmentId:guid}", DeleteAttachmentAsync).RequireAuthorization("tenant.collaboration.manage");

        tenant.MapGet("/announcements", ListAnnouncementsAsync).RequireAuthorization("tenant.notification.read");
        tenant.MapPost("/announcements", CreateAnnouncementAsync).RequireAuthorization("tenant.announcement.manage");
        tenant.MapDelete("/announcements/{announcementId:guid}", DeleteAnnouncementAsync).RequireAuthorization("tenant.announcement.manage");
    }

    // ---------- forums ----------
    private static async Task<IResult> ListThreadsAsync(LmsDbContext db, Guid? courseId, CancellationToken cancellationToken)
    {
        var query = db.ForumThreads.AsNoTracking();
        if (courseId is Guid selectedCourse) query = query.Where(item => item.CourseId == selectedCourse);
        var threads = await query.OrderByDescending(item => item.IsPinned).ThenByDescending(item => item.LastActivityAtUtc).Take(200).ToListAsync(cancellationToken);
        var threadIds = threads.Select(item => item.Id).ToList();
        var replyCounts = await db.ForumReplies.AsNoTracking().Where(item => threadIds.Contains(item.ThreadId))
            .GroupBy(item => item.ThreadId).Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, cancellationToken);
        var names = await AuthorNamesAsync(db, threads.Select(item => item.AuthorUserId), cancellationToken);
        var courseTitles = await CourseTitlesAsync(db, threads.Select(item => item.CourseId), cancellationToken);
        return Results.Ok(threads.Select(item => new ThreadSummary(
            item.Id, item.CourseId, item.CourseId is Guid id ? courseTitles.GetValueOrDefault(id) : null,
            item.Title, names.GetValueOrDefault(item.AuthorUserId, "Unknown"), item.IsPinned, item.IsLocked,
            replyCounts.GetValueOrDefault(item.Id), item.CreatedAtUtc, item.LastActivityAtUtc)));
    }

    private static async Task<IResult> CreateThreadAsync(HttpContext httpContext, ITenantContext tenantContext, LmsDbContext db, CreateThreadRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var title = request.Title?.Trim() ?? string.Empty;
        var body = request.Body?.Trim() ?? string.Empty;
        if (title.Length is < 3 or > MaxTitle) return Results.BadRequest(new { message = $"Title must be between 3 and {MaxTitle} characters." });
        if (body.Length is < 1 or > MaxBody) return Results.BadRequest(new { message = $"Body must be between 1 and {MaxBody} characters." });
        if (request.CourseId is Guid courseId && !await db.Courses.AnyAsync(item => item.Id == courseId, cancellationToken))
            return Results.BadRequest(new { message = "The selected course does not exist." });
        var now = DateTimeOffset.UtcNow;
        var thread = new ForumThread { Id = Guid.NewGuid(), TenantId = tenantId, CourseId = request.CourseId, AuthorUserId = userId, Title = title, Body = body, CreatedAtUtc = now, LastActivityAtUtc = now };
        db.ForumThreads.Add(thread);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/community/threads/{thread.Id}", new { thread.Id });
    }

    private static async Task<IResult> GetThreadAsync(Guid threadId, LmsDbContext db, CancellationToken cancellationToken)
    {
        var thread = await db.ForumThreads.AsNoTracking().SingleOrDefaultAsync(item => item.Id == threadId, cancellationToken);
        if (thread is null) return Results.NotFound();
        var replies = await db.ForumReplies.AsNoTracking().Where(item => item.ThreadId == threadId).OrderBy(item => item.CreatedAtUtc).ToListAsync(cancellationToken);
        var names = await AuthorNamesAsync(db, replies.Select(item => item.AuthorUserId).Append(thread.AuthorUserId), cancellationToken);
        var attachments = await db.ForumAttachments.AsNoTracking().Where(item => item.ThreadId == threadId).OrderBy(item => item.CreatedAtUtc).ToListAsync(cancellationToken);
        List<ForumAttachmentView> AttachmentsOf(Guid? replyId) => attachments.Where(item => item.ReplyId == replyId).Select(item => new ForumAttachmentView(item.Id, item.FileName, item.SizeBytes, item.ContentType)).ToList();
        return Results.Ok(new ThreadDetail(
            thread.Id, thread.CourseId, thread.Title, thread.Body, thread.AuthorUserId, names.GetValueOrDefault(thread.AuthorUserId, "Unknown"),
            thread.IsPinned, thread.IsLocked, thread.CreatedAtUtc,
            replies.Select(item => new ReplyResponse(item.Id, item.AuthorUserId, names.GetValueOrDefault(item.AuthorUserId, "Unknown"), item.Body, item.CreatedAtUtc, item.EditedAtUtc, AttachmentsOf(item.Id))).ToList(),
            thread.EditedAtUtc, AttachmentsOf(null)));
    }

    private static async Task<IResult> CreateReplyAsync(Guid threadId, HttpContext httpContext, ITenantContext tenantContext, LmsDbContext db, CreateReplyRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var body = request.Body?.Trim() ?? string.Empty;
        if (body.Length is < 1 or > MaxBody) return Results.BadRequest(new { message = $"Reply must be between 1 and {MaxBody} characters." });
        var thread = await db.ForumThreads.SingleOrDefaultAsync(item => item.Id == threadId, cancellationToken);
        if (thread is null) return Results.NotFound();
        if (thread.IsLocked) return Results.Conflict(new { message = "This thread is locked." });
        var now = DateTimeOffset.UtcNow;
        db.ForumReplies.Add(new ForumReply { Id = Guid.NewGuid(), TenantId = tenantId, ThreadId = threadId, AuthorUserId = userId, Body = body, CreatedAtUtc = now });
        thread.LastActivityAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/community/threads/{threadId}", new { threadId });
    }

    private static async Task<IResult> PinThreadAsync(Guid threadId, LmsDbContext db, SetFlagRequest request, CancellationToken cancellationToken)
    {
        var thread = await db.ForumThreads.SingleOrDefaultAsync(item => item.Id == threadId, cancellationToken);
        if (thread is null) return Results.NotFound();
        thread.IsPinned = request.Value;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> LockThreadAsync(Guid threadId, LmsDbContext db, SetFlagRequest request, CancellationToken cancellationToken)
    {
        var thread = await db.ForumThreads.SingleOrDefaultAsync(item => item.Id == threadId, cancellationToken);
        if (thread is null) return Results.NotFound();
        thread.IsLocked = request.Value;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteThreadAsync(Guid threadId, HttpContext httpContext, LmsDbContext db, IContentAssetStorage storage, CancellationToken cancellationToken)
    {
        var thread = await db.ForumThreads.SingleOrDefaultAsync(item => item.Id == threadId, cancellationToken);
        if (thread is null) return Results.NotFound();
        if (!CanModify(httpContext, thread.AuthorUserId)) return Results.Forbid();
        var replies = await db.ForumReplies.Where(item => item.ThreadId == threadId).ToListAsync(cancellationToken);
        var attachments = await db.ForumAttachments.Where(item => item.ThreadId == threadId).ToListAsync(cancellationToken);
        db.ForumEdits.RemoveRange(await db.ForumEdits.Where(item => item.ThreadId == threadId).ToListAsync(cancellationToken));
        db.ForumAttachments.RemoveRange(attachments);
        db.ForumReplies.RemoveRange(replies);
        db.ForumThreads.Remove(thread);
        await db.SaveChangesAsync(cancellationToken);
        foreach (var attachment in attachments) await storage.DeleteAsync(attachment.StorageKey, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteReplyAsync(Guid replyId, HttpContext httpContext, LmsDbContext db, IContentAssetStorage storage, CancellationToken cancellationToken)
    {
        var reply = await db.ForumReplies.SingleOrDefaultAsync(item => item.Id == replyId, cancellationToken);
        if (reply is null) return Results.NotFound();
        if (!CanModify(httpContext, reply.AuthorUserId)) return Results.Forbid();
        var attachments = await db.ForumAttachments.Where(item => item.ReplyId == replyId).ToListAsync(cancellationToken);
        db.ForumEdits.RemoveRange(await db.ForumEdits.Where(item => item.ReplyId == replyId).ToListAsync(cancellationToken));
        db.ForumAttachments.RemoveRange(attachments);
        db.ForumReplies.Remove(reply);
        await db.SaveChangesAsync(cancellationToken);
        foreach (var attachment in attachments) await storage.DeleteAsync(attachment.StorageKey, cancellationToken);
        return Results.NoContent();
    }

    // ---------- editing and history ----------
    /// <summary>
    /// The author (or a moderator) may change a post. The words it replaced are kept as history that only the author and moderators can read,
    /// so people can fix a typo without worry yet nobody can quietly rewrite what others replied to. A locked thread can only be edited by moderators.
    /// </summary>
    private static async Task<IResult> EditThreadAsync(Guid threadId, HttpContext httpContext, ITenantContext tenantContext, LmsDbContext db, CreateThreadRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var thread = await db.ForumThreads.SingleOrDefaultAsync(item => item.Id == threadId, cancellationToken);
        if (thread is null) return Results.NotFound();
        if (!CanModify(httpContext, thread.AuthorUserId)) return Results.Forbid();
        if (thread.IsLocked && !HasPermission(httpContext, LmsPermissions.ForumModerate)) return Results.Conflict(new { message = "This thread is locked." });
        var title = request.Title?.Trim() ?? string.Empty;
        var body = request.Body?.Trim() ?? string.Empty;
        if (title.Length is < 3 or > MaxTitle) return Results.BadRequest(new { message = $"Title must be between 3 and {MaxTitle} characters." });
        if (body.Length is < 1 or > MaxBody) return Results.BadRequest(new { message = $"Body must be between 1 and {MaxBody} characters." });
        if (title == thread.Title && body == thread.Body) return Results.NoContent();
        var now = DateTimeOffset.UtcNow;
        db.ForumEdits.Add(new ForumEdit { Id = Guid.NewGuid(), TenantId = tenantId, ThreadId = threadId, ReplyId = null, PreviousTitle = thread.Title, PreviousBody = thread.Body, EditedByUserId = userId, EditedAtUtc = now });
        thread.Title = title; thread.Body = body; thread.EditedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> EditReplyAsync(Guid replyId, HttpContext httpContext, ITenantContext tenantContext, LmsDbContext db, CreateReplyRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var reply = await db.ForumReplies.SingleOrDefaultAsync(item => item.Id == replyId, cancellationToken);
        if (reply is null) return Results.NotFound();
        if (!CanModify(httpContext, reply.AuthorUserId)) return Results.Forbid();
        var thread = await db.ForumThreads.AsNoTracking().SingleAsync(item => item.Id == reply.ThreadId, cancellationToken);
        if (thread.IsLocked && !HasPermission(httpContext, LmsPermissions.ForumModerate)) return Results.Conflict(new { message = "This thread is locked." });
        var body = request.Body?.Trim() ?? string.Empty;
        if (body.Length is < 1 or > MaxBody) return Results.BadRequest(new { message = $"Reply must be between 1 and {MaxBody} characters." });
        if (body == reply.Body) return Results.NoContent();
        var now = DateTimeOffset.UtcNow;
        db.ForumEdits.Add(new ForumEdit { Id = Guid.NewGuid(), TenantId = tenantId, ThreadId = reply.ThreadId, ReplyId = replyId, PreviousBody = reply.Body, EditedByUserId = userId, EditedAtUtc = now });
        reply.Body = body; reply.EditedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ThreadHistoryAsync(Guid threadId, HttpContext httpContext, LmsDbContext db, CancellationToken cancellationToken)
    {
        var thread = await db.ForumThreads.AsNoTracking().SingleOrDefaultAsync(item => item.Id == threadId, cancellationToken);
        if (thread is null) return Results.NotFound();
        if (!CanModify(httpContext, thread.AuthorUserId)) return Results.Forbid();
        return Results.Ok(await HistoryAsync(db, db.ForumEdits.AsNoTracking().Where(item => item.ThreadId == threadId && item.ReplyId == null), cancellationToken));
    }

    private static async Task<IResult> ReplyHistoryAsync(Guid replyId, HttpContext httpContext, LmsDbContext db, CancellationToken cancellationToken)
    {
        var reply = await db.ForumReplies.AsNoTracking().SingleOrDefaultAsync(item => item.Id == replyId, cancellationToken);
        if (reply is null) return Results.NotFound();
        if (!CanModify(httpContext, reply.AuthorUserId)) return Results.Forbid();
        return Results.Ok(await HistoryAsync(db, db.ForumEdits.AsNoTracking().Where(item => item.ReplyId == replyId), cancellationToken));
    }

    private static async Task<List<EditView>> HistoryAsync(LmsDbContext db, IQueryable<ForumEdit> edits, CancellationToken cancellationToken)
    {
        var rows = await edits.OrderByDescending(item => item.EditedAtUtc).Take(100).ToListAsync(cancellationToken);
        var names = await AuthorNamesAsync(db, rows.Select(item => item.EditedByUserId), cancellationToken);
        return rows.Select(item => new EditView(item.Id, names.GetValueOrDefault(item.EditedByUserId, "Unknown"), item.EditedAtUtc, item.PreviousTitle, item.PreviousBody)).ToList();
    }

    // ---------- attachments ----------
    private static async Task<IResult> AttachAsync(Guid threadId, HttpRequest request, ITenantContext tenantContext, LmsDbContext db, IContentAssetStorage storage, CancellationToken cancellationToken)
    {
        var httpContext = request.HttpContext;
        if (tenantContext.TenantId is not Guid tenantId || GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var (file, form) = await AttachmentRules.ReadAsync(request, cancellationToken);
        if (AttachmentRules.Problem(file) is { } rejected) return Results.BadRequest(new { message = rejected });

        var thread = await db.ForumThreads.AsNoTracking().SingleOrDefaultAsync(item => item.Id == threadId, cancellationToken);
        if (thread is null) return Results.NotFound();
        Guid? replyId = Guid.TryParse(form?["replyId"].ToString(), out var parsed) ? parsed : null;
        var owner = thread.AuthorUserId;
        if (replyId is Guid wanted)
        {
            var reply = await db.ForumReplies.AsNoTracking().SingleOrDefaultAsync(item => item.Id == wanted && item.ThreadId == threadId, cancellationToken);
            if (reply is null) return Results.NotFound();
            owner = reply.AuthorUserId;
        }
        if (!CanModify(httpContext, owner)) return Results.Forbid();
        if (thread.IsLocked && !HasPermission(httpContext, LmsPermissions.ForumModerate)) return Results.Conflict(new { message = "This thread is locked." });
        if (await db.ForumAttachments.CountAsync(item => item.ThreadId == threadId && item.ReplyId == replyId, cancellationToken) >= MaxAttachmentsPerPost)
            return Results.Conflict(new { message = $"A post can have at most {MaxAttachmentsPerPost} attachments." });

        var stored = await storage.SaveAsync(tenantId, thread.CourseId ?? Guid.Empty, file!, cancellationToken);
        var attachment = new ForumAttachment { Id = Guid.NewGuid(), TenantId = tenantId, ThreadId = threadId, ReplyId = replyId, StorageKey = stored.StorageKey, FileName = stored.OriginalFileName, ContentType = stored.ContentType, SizeBytes = stored.SizeBytes, UploadedByUserId = userId, CreatedAtUtc = DateTimeOffset.UtcNow };
        db.ForumAttachments.Add(attachment);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/community/attachments/{attachment.Id}", new ForumAttachmentView(attachment.Id, attachment.FileName, attachment.SizeBytes, attachment.ContentType));
    }

    private static async Task<IResult> DownloadAttachmentAsync(Guid attachmentId, LmsDbContext db, IContentAssetStorage storage, CancellationToken cancellationToken)
    {
        var attachment = await db.ForumAttachments.AsNoTracking().SingleOrDefaultAsync(item => item.Id == attachmentId, cancellationToken);
        if (attachment is null) return Results.NotFound();
        var stream = await storage.OpenReadAsync(attachment.StorageKey, cancellationToken);
        return stream is null ? Results.NotFound() : Results.File(stream, "application/octet-stream", attachment.FileName);
    }

    private static async Task<IResult> DeleteAttachmentAsync(Guid attachmentId, HttpContext httpContext, LmsDbContext db, IContentAssetStorage storage, CancellationToken cancellationToken)
    {
        var attachment = await db.ForumAttachments.SingleOrDefaultAsync(item => item.Id == attachmentId, cancellationToken);
        if (attachment is null) return Results.NotFound();
        var owner = attachment.ReplyId is Guid replyId
            ? await db.ForumReplies.AsNoTracking().Where(item => item.Id == replyId).Select(item => (Guid?)item.AuthorUserId).SingleOrDefaultAsync(cancellationToken)
            : await db.ForumThreads.AsNoTracking().Where(item => item.Id == attachment.ThreadId).Select(item => (Guid?)item.AuthorUserId).SingleOrDefaultAsync(cancellationToken);
        if (!CanModify(httpContext, owner ?? attachment.UploadedByUserId)) return Results.Forbid();
        db.ForumAttachments.Remove(attachment);
        await db.SaveChangesAsync(cancellationToken);
        await storage.DeleteAsync(attachment.StorageKey, cancellationToken);
        return Results.NoContent();
    }

    // ---------- announcements ----------
    private static async Task<IResult> ListAnnouncementsAsync(LmsDbContext db, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var items = await db.Announcements.AsNoTracking()
            .Where(item => item.ExpiresAtUtc == null || item.ExpiresAtUtc > now)
            .OrderByDescending(item => item.IsPinned).ThenByDescending(item => item.CreatedAtUtc).Take(100).ToListAsync(cancellationToken);
        var names = await AuthorNamesAsync(db, items.Select(item => item.AuthorUserId), cancellationToken);
        var courseTitles = await CourseTitlesAsync(db, items.Select(item => item.CourseId), cancellationToken);
        return Results.Ok(items.Select(item => new AnnouncementResponse(
            item.Id, item.Title, item.Body, item.IsPinned, item.CourseId, item.CourseId is Guid id ? courseTitles.GetValueOrDefault(id) : null,
            names.GetValueOrDefault(item.AuthorUserId, "Unknown"), item.CreatedAtUtc, item.ExpiresAtUtc)));
    }

    private static async Task<IResult> CreateAnnouncementAsync(HttpContext httpContext, ITenantContext tenantContext, LmsDbContext db, NotificationService notifications, CreateAnnouncementRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var title = request.Title?.Trim() ?? string.Empty;
        var body = request.Body?.Trim() ?? string.Empty;
        if (title.Length is < 3 or > MaxTitle) return Results.BadRequest(new { message = $"Title must be between 3 and {MaxTitle} characters." });
        if (body.Length is < 1 or > MaxBody) return Results.BadRequest(new { message = $"Message must be between 1 and {MaxBody} characters." });
        if (request.ExpiresAtUtc is DateTimeOffset expires && expires <= DateTimeOffset.UtcNow)
            return Results.BadRequest(new { message = "The expiry must be in the future." });
        if (request.CourseId is Guid courseId && !await db.Courses.AnyAsync(item => item.Id == courseId, cancellationToken))
            return Results.BadRequest(new { message = "The selected course does not exist." });
        var announcement = new Announcement
        {
            Id = Guid.NewGuid(), TenantId = tenantId, CourseId = request.CourseId, AuthorUserId = userId, Title = title, Body = body,
            IsPinned = request.IsPinned, CreatedAtUtc = DateTimeOffset.UtcNow, ExpiresAtUtc = request.ExpiresAtUtc
        };
        db.Announcements.Add(announcement);

        // Course announcements go to that course's learners; others to every active member. The author is not notified.
        var recipients = request.CourseId is Guid targetCourse
            ? await db.Enrollments.AsNoTracking().Where(item => item.CourseId == targetCourse && (item.Status == Domain.Learning.EnrollmentStatus.Active || item.Status == Domain.Learning.EnrollmentStatus.Completed)).Select(item => item.LearnerUserId).ToListAsync(cancellationToken)
            : await db.TenantMemberships.AsNoTracking().Where(item => item.Status == MembershipStatus.Active).Select(item => item.UserId).ToListAsync(cancellationToken);
        await notifications.QueueManyAsync(db, tenantId, recipients.Where(id => id != userId), "ANNOUNCEMENT",
            new Dictionary<string, string> { ["Title"] = title, ["Body"] = body.Length > 300 ? body[..300] + "…" : body }, $"announcement:{announcement.Id}", cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/community/announcements/{announcement.Id}", new { announcement.Id });
    }

    private static async Task<IResult> DeleteAnnouncementAsync(Guid announcementId, LmsDbContext db, CancellationToken cancellationToken)
    {
        var announcement = await db.Announcements.SingleOrDefaultAsync(item => item.Id == announcementId, cancellationToken);
        if (announcement is null) return Results.NotFound();
        db.Announcements.Remove(announcement);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    // ---------- helpers ----------
    private static Guid? GetUserId(HttpContext context)
        => Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub"), out var id) ? id : null;

    private static bool HasPermission(HttpContext context, string permission)
        => context.User.Claims.Any(claim => claim.Type == "permission" && claim.Value.Equals(permission, StringComparison.OrdinalIgnoreCase));

    private static bool CanModify(HttpContext context, Guid authorUserId)
        => GetUserId(context) == authorUserId || HasPermission(context, LmsPermissions.ForumModerate);

    private static async Task<Dictionary<Guid, string>> AuthorNamesAsync(LmsDbContext db, IEnumerable<Guid> userIds, CancellationToken cancellationToken)
    {
        var ids = userIds.Distinct().ToList();
        return await db.Users.AsNoTracking().Where(item => ids.Contains(item.Id)).ToDictionaryAsync(item => item.Id, item => item.DisplayName, cancellationToken);
    }

    private static async Task<Dictionary<Guid, string>> CourseTitlesAsync(LmsDbContext db, IEnumerable<Guid?> courseIds, CancellationToken cancellationToken)
    {
        var ids = courseIds.Where(item => item != null).Select(item => item!.Value).Distinct().ToList();
        return await db.Courses.AsNoTracking().Where(item => ids.Contains(item.Id)).ToDictionaryAsync(item => item.Id, item => item.Title, cancellationToken);
    }
}

public sealed record CreateThreadRequest(Guid? CourseId, string? Title, string? Body);
public sealed record CreateReplyRequest(string? Body);
public sealed record SetFlagRequest(bool Value);
public sealed record ThreadSummary(Guid Id, Guid? CourseId, string? CourseTitle, string Title, string AuthorName, bool IsPinned, bool IsLocked, int ReplyCount, DateTimeOffset CreatedAtUtc, DateTimeOffset LastActivityAtUtc);
public sealed record ForumAttachmentView(Guid Id, string FileName, long SizeBytes, string ContentType);
public sealed record EditView(Guid Id, string EditedByName, DateTimeOffset EditedAtUtc, string? PreviousTitle, string PreviousBody);
public sealed record ReplyResponse(Guid Id, Guid AuthorUserId, string AuthorName, string Body, DateTimeOffset CreatedAtUtc, DateTimeOffset? EditedAtUtc = null, IReadOnlyList<ForumAttachmentView>? Attachments = null);
public sealed record ThreadDetail(Guid Id, Guid? CourseId, string Title, string Body, Guid AuthorUserId, string AuthorName, bool IsPinned, bool IsLocked, DateTimeOffset CreatedAtUtc, IReadOnlyList<ReplyResponse> Replies, DateTimeOffset? EditedAtUtc = null, IReadOnlyList<ForumAttachmentView>? Attachments = null);
public sealed record CreateAnnouncementRequest(string? Title, string? Body, Guid? CourseId, bool IsPinned, DateTimeOffset? ExpiresAtUtc);
public sealed record AnnouncementResponse(Guid Id, string Title, string Body, bool IsPinned, Guid? CourseId, string? CourseTitle, string AuthorName, DateTimeOffset CreatedAtUtc, DateTimeOffset? ExpiresAtUtc);
