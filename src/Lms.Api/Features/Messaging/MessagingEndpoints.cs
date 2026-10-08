using System.Security.Claims;
using System.Text.Json;
using Lms.Api.Domain.Courses;
using Lms.Api.Domain.Identity;
using Lms.Api.Domain.Learning;
using Lms.Api.Domain.Messaging;
using Lms.Api.Infrastructure.Messaging;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Storage;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Messaging;

/// <summary>
/// Direct messages and course chats: text, one file per message, editing and deleting your own messages, and live updates.
/// Privacy rule: learners may start direct conversations only with staff (people who can manage courses or grades),
/// never with other learners, unless the tenant sets Messaging:AllowLearnerToLearner.
/// </summary>
public static class MessagingEndpoints
{
    private const int MaxBodyLength = 5000;
    private const int PageSize = 100;
    /// <summary>A live connection is closed after this long and the browser reconnects, so a connection never outlives the sign-in it started with by much.</summary>
    private static readonly TimeSpan StreamLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StreamPing = TimeSpan.FromSeconds(25);

    public static void MapMessagingEndpoints(this WebApplication app)
    {
        var tenant = app.MapGroup("/api/v1/tenant/messages").RequireAuthorization("tenant.authenticated");
        tenant.AddEndpointFilter(async (context, next) =>
        {
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (!tenantContext.IsResolved)
                return Results.BadRequest(new { message = "A tenant is required. Use the X-Tenant-Slug header or a tenant subdomain." });
            return await next(context);
        });

        tenant.MapGet("/contacts", ListContactsAsync).RequireAuthorization("tenant.collaboration.read");
        tenant.MapGet("/conversations", ListConversationsAsync).RequireAuthorization("tenant.collaboration.read");
        tenant.MapGet("/unread-count", UnreadCountAsync).RequireAuthorization("tenant.collaboration.read");
        tenant.MapPost("/conversations/direct", StartDirectAsync).RequireAuthorization("tenant.collaboration.manage");
        tenant.MapPost("/conversations/course", OpenCourseChatAsync).RequireAuthorization("tenant.collaboration.manage");
        tenant.MapGet("/conversations/{conversationId:guid}/messages", ListMessagesAsync).RequireAuthorization("tenant.collaboration.read");
        tenant.MapPost("/conversations/{conversationId:guid}/messages", SendAsync).RequireAuthorization("tenant.collaboration.manage");
        tenant.MapPost("/conversations/{conversationId:guid}/read", MarkReadAsync).RequireAuthorization("tenant.collaboration.read");
        tenant.MapPost("/conversations/{conversationId:guid}/messages/upload", SendWithFileAsync).RequireAuthorization("tenant.collaboration.manage");
        tenant.MapPut("/conversations/{conversationId:guid}/messages/{messageId:guid}", EditAsync).RequireAuthorization("tenant.collaboration.manage");
        tenant.MapDelete("/conversations/{conversationId:guid}/messages/{messageId:guid}", DeleteAsync).RequireAuthorization("tenant.collaboration.manage");
        tenant.MapGet("/conversations/{conversationId:guid}/messages/{messageId:guid}/attachment", DownloadAsync).RequireAuthorization("tenant.collaboration.read");
        tenant.MapGet("/stream", StreamAsync).RequireAuthorization("tenant.collaboration.read");
    }

    // ---------- contacts ----------
    private static async Task<IResult> ListContactsAsync(HttpContext httpContext, LmsDbContext db, IConfiguration configuration, string? q, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var staffIds = await StaffIdsAsync(db, cancellationToken);
        var memberIds = await db.TenantMemberships.AsNoTracking().Where(item => item.Status == MembershipStatus.Active && item.UserId != userId).Select(item => item.UserId).ToListAsync(cancellationToken);
        var users = db.Users.AsNoTracking().Where(item => memberIds.Contains(item.Id) && item.Status == UserStatus.Active);
        var term = q?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(term)) users = users.Where(item => item.DisplayName.ToLower().Contains(term) || item.Email.ToLower().Contains(term));
        var list = await users.OrderBy(item => item.DisplayName).Take(200).ToListAsync(cancellationToken);
        var allowed = list.Where(item => CanContact(httpContext, configuration, staffIds, item.Id)).Take(50)
            .Select(item => new ContactResponse(item.Id, item.DisplayName, item.Email, staffIds.Contains(item.Id)));
        return Results.Ok(allowed);
    }

    // ---------- conversations ----------
    private static async Task<IResult> ListConversationsAsync(HttpContext httpContext, LmsDbContext db, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var access = await AccessibleAsync(db, httpContext, userId, cancellationToken);
        var ids = access.Select(item => item.Conversation.Id).ToList();
        var last = await db.ConversationMessages.AsNoTracking().Where(item => ids.Contains(item.ConversationId))
            .GroupBy(item => item.ConversationId).Select(group => group.OrderByDescending(m => m.CreatedAtUtc).First()).ToListAsync(cancellationToken);
        var lastByConversation = last.ToDictionary(item => item.ConversationId);

        var otherUserIds = access.Where(item => item.Conversation.Kind == ConversationKind.Direct).Select(item => OtherUser(item.Conversation, userId)).Where(id => id != Guid.Empty).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(item => otherUserIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, item => item.DisplayName, cancellationToken);
        var courseTitles = await db.Courses.AsNoTracking().ToDictionaryAsync(item => item.Id, item => item.Title, cancellationToken);
        var unread = await UnreadByConversationAsync(db, userId, access, cancellationToken);

        var result = access.Select(item =>
        {
            var conversation = item.Conversation;
            lastByConversation.TryGetValue(conversation.Id, out var message);
            var title = conversation.Kind == ConversationKind.Direct
                ? names.GetValueOrDefault(OtherUser(conversation, userId), "Unknown")
                : courseTitles.GetValueOrDefault(conversation.CourseId ?? Guid.Empty, "Course") + " · course chat";
            return new ConversationSummary(conversation.Id, conversation.Kind.ToString(), title, conversation.CourseId,
                message is null ? null : PreviewOf(message), message?.CreatedAtUtc ?? conversation.CreatedAtUtc, unread.GetValueOrDefault(conversation.Id));
        }).OrderByDescending(item => item.LastActivityAtUtc).ToList();
        return Results.Ok(result);
    }

    private static async Task<IResult> UnreadCountAsync(HttpContext httpContext, LmsDbContext db, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var access = await AccessibleAsync(db, httpContext, userId, cancellationToken);
        var unread = await UnreadByConversationAsync(db, userId, access, cancellationToken);
        return Results.Ok(new { count = unread.Values.Sum() });
    }

    private static async Task<IResult> StartDirectAsync(HttpContext httpContext, ITenantContext tenantContext, LmsDbContext db, IConfiguration configuration, StartDirectRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        if (request.UserId == userId) return Results.BadRequest(new { message = "You cannot message yourself." });
        var isMember = await db.TenantMemberships.AnyAsync(item => item.UserId == request.UserId && item.Status == MembershipStatus.Active, cancellationToken);
        if (!isMember) return Results.NotFound(new { message = "That person is not part of your organization." });
        var staffIds = await StaffIdsAsync(db, cancellationToken);
        if (!CanContact(httpContext, configuration, staffIds, request.UserId))
            return Results.Json(new { message = "You can only start conversations with teachers and staff." }, statusCode: StatusCodes.Status403Forbidden);

        var key = DirectKey(userId, request.UserId);
        var conversation = await db.Conversations.SingleOrDefaultAsync(item => item.DirectKey == key, cancellationToken);
        if (conversation is null)
        {
            var now = DateTimeOffset.UtcNow;
            conversation = new Conversation { Id = Guid.NewGuid(), TenantId = tenantId, Kind = ConversationKind.Direct, DirectKey = key, CreatedByUserId = userId, CreatedAtUtc = now, LastMessageAtUtc = now };
            db.Conversations.Add(conversation);
            db.ConversationParticipants.Add(new ConversationParticipant { Id = Guid.NewGuid(), TenantId = tenantId, ConversationId = conversation.Id, UserId = userId, JoinedAtUtc = now, LastReadAtUtc = now });
            db.ConversationParticipants.Add(new ConversationParticipant { Id = Guid.NewGuid(), TenantId = tenantId, ConversationId = conversation.Id, UserId = request.UserId, JoinedAtUtc = now, LastReadAtUtc = now });
            await db.SaveChangesAsync(cancellationToken);
        }
        return Results.Ok(new { id = conversation.Id });
    }

    private static async Task<IResult> OpenCourseChatAsync(HttpContext httpContext, ITenantContext tenantContext, LmsDbContext db, OpenCourseChatRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var course = await db.Courses.AsNoTracking().SingleOrDefaultAsync(item => item.Id == request.CourseId, cancellationToken);
        if (course is null || course.Status == CourseStatus.Draft) return Results.NotFound();
        if (!await CanAccessCourseAsync(db, httpContext, userId, course.Id, cancellationToken)) return Results.NotFound();

        var conversation = await db.Conversations.SingleOrDefaultAsync(item => item.Kind == ConversationKind.Course && item.CourseId == course.Id, cancellationToken);
        if (conversation is null)
        {
            var now = DateTimeOffset.UtcNow;
            conversation = new Conversation { Id = Guid.NewGuid(), TenantId = tenantId, Kind = ConversationKind.Course, CourseId = course.Id, CreatedByUserId = userId, CreatedAtUtc = now, LastMessageAtUtc = now };
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync(cancellationToken);
        }
        return Results.Ok(new { id = conversation.Id });
    }

    // ---------- messages ----------
    private static async Task<IResult> ListMessagesAsync(Guid conversationId, HttpContext httpContext, LmsDbContext db, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        if (await FindAccessibleAsync(db, httpContext, userId, conversationId, cancellationToken) is null) return Results.NotFound();
        var messages = await db.ConversationMessages.AsNoTracking().Where(item => item.ConversationId == conversationId)
            .OrderByDescending(item => item.CreatedAtUtc).Take(PageSize).ToListAsync(cancellationToken);
        messages.Reverse();
        var senderIds = messages.Select(item => item.SenderUserId).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(item => senderIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, item => item.DisplayName, cancellationToken);
        return Results.Ok(messages.Select(item => ToResponse(item, names.GetValueOrDefault(item.SenderUserId, "Unknown"), userId)));
    }

    private static MessageResponse ToResponse(ConversationMessage item, string senderName, Guid viewerId)
        => new(item.Id, item.SenderUserId, senderName, item.DeletedAtUtc is null ? item.Body : string.Empty, item.CreatedAtUtc, item.SenderUserId == viewerId,
            item.EditedAtUtc, item.DeletedAtUtc is not null,
            item.DeletedAtUtc is null && item.AttachmentKey is not null ? new AttachmentView(item.AttachmentName ?? "file", item.AttachmentSizeBytes ?? 0, item.AttachmentContentType ?? "application/octet-stream") : null);

    private static string PreviewOf(ConversationMessage message)
        => message.DeletedAtUtc is not null ? "Message deleted"
            : message.Body.Length == 0 && message.AttachmentName is not null ? $"Attachment: {message.AttachmentName}" : Preview(message.Body);

    /// <summary>Who must be told about a change: the two people of a direct conversation, or (null) everyone who can reach the course.</summary>
    private static async Task<IReadOnlyList<Guid>?> RecipientsAsync(LmsDbContext db, Conversation conversation, CancellationToken cancellationToken)
        => conversation.Kind == ConversationKind.Direct
            ? await db.ConversationParticipants.AsNoTracking().Where(item => item.ConversationId == conversation.Id).Select(item => item.UserId).ToListAsync(cancellationToken)
            : null;

    private static async Task PublishAsync(MessageHub hub, LmsDbContext db, string type, Conversation conversation, Guid? messageId, CancellationToken cancellationToken)
        => hub.Publish(new MessageEvent(type, conversation.TenantId, conversation.Id, messageId, conversation.CourseId, await RecipientsAsync(db, conversation, cancellationToken)));

    private static Task<IResult> SendAsync(Guid conversationId, HttpContext httpContext, ITenantContext tenantContext, LmsDbContext db, MessageHub hub, SendMessageRequest request, CancellationToken cancellationToken)
        => StoreMessageAsync(conversationId, httpContext, tenantContext, db, hub, request.Body, null, null, cancellationToken);

    private static async Task<IResult> SendWithFileAsync(Guid conversationId, HttpRequest request, ITenantContext tenantContext, LmsDbContext db, MessageHub hub, IContentAssetStorage storage, CancellationToken cancellationToken)
    {
        var (file, form) = await AttachmentRules.ReadAsync(request, cancellationToken);
        if (AttachmentRules.Problem(file) is { } rejected) return Results.BadRequest(new { message = rejected });
        return await StoreMessageAsync(conversationId, request.HttpContext, tenantContext, db, hub, form?["body"].ToString(), file, storage, cancellationToken);
    }

    private static async Task<IResult> StoreMessageAsync(Guid conversationId, HttpContext httpContext, ITenantContext tenantContext, LmsDbContext db, MessageHub hub, string? text, IFormFile? file, IContentAssetStorage? storage, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var body = text?.Trim() ?? string.Empty;
        // A message needs words, a file, or both.
        if (body.Length > MaxBodyLength || (body.Length == 0 && file is null)) return Results.BadRequest(new { message = $"A message must be between 1 and {MaxBodyLength} characters." });
        var conversation = await FindAccessibleAsync(db, httpContext, userId, conversationId, cancellationToken);
        if (conversation is null) return Results.NotFound();

        var now = DateTimeOffset.UtcNow;
        var message = new ConversationMessage { Id = Guid.NewGuid(), TenantId = tenantId, ConversationId = conversationId, SenderUserId = userId, Body = body, CreatedAtUtc = now };
        if (file is not null && storage is not null)
        {
            var stored = await storage.SaveAsync(tenantId, conversation.CourseId ?? Guid.Empty, file, cancellationToken);
            message.AttachmentKey = stored.StorageKey; message.AttachmentName = stored.OriginalFileName; message.AttachmentContentType = stored.ContentType; message.AttachmentSizeBytes = stored.SizeBytes;
        }
        db.ConversationMessages.Add(message);
        var tracked = await db.Conversations.SingleAsync(item => item.Id == conversationId, cancellationToken);
        tracked.LastMessageAtUtc = now;
        await TouchParticipantAsync(db, tenantId, conversationId, userId, now, cancellationToken); // sending counts as reading up to now
        await db.SaveChangesAsync(cancellationToken);
        await PublishAsync(hub, db, "message", conversation, message.Id, cancellationToken);
        return Results.Created($"/api/v1/tenant/messages/conversations/{conversationId}/messages", ToResponse(message, "You", userId));
    }

    private static async Task<IResult> EditAsync(Guid conversationId, Guid messageId, HttpContext httpContext, LmsDbContext db, MessageHub hub, SendMessageRequest request, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var conversation = await FindAccessibleAsync(db, httpContext, userId, conversationId, cancellationToken);
        var message = conversation is null ? null : await db.ConversationMessages.SingleOrDefaultAsync(item => item.Id == messageId && item.ConversationId == conversationId, cancellationToken);
        if (conversation is null || message is null || message.DeletedAtUtc is not null) return Results.NotFound();
        // Only the sender changes their own words; nobody, moderators included, can put words in someone else's mouth.
        if (message.SenderUserId != userId) return Results.Json(new { message = "You can only edit your own messages." }, statusCode: StatusCodes.Status403Forbidden);
        var body = request.Body?.Trim() ?? string.Empty;
        if (body.Length > MaxBodyLength || (body.Length == 0 && message.AttachmentKey is null)) return Results.BadRequest(new { message = $"A message must be between 1 and {MaxBodyLength} characters." });
        if (body != message.Body)
        {
            message.Body = body;
            message.EditedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await PublishAsync(hub, db, "edited", conversation, message.Id, cancellationToken);
        }
        return Results.Ok(ToResponse(message, "You", userId));
    }

    private static async Task<IResult> DeleteAsync(Guid conversationId, Guid messageId, HttpContext httpContext, LmsDbContext db, MessageHub hub, IContentAssetStorage storage, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var conversation = await FindAccessibleAsync(db, httpContext, userId, conversationId, cancellationToken);
        var message = conversation is null ? null : await db.ConversationMessages.SingleOrDefaultAsync(item => item.Id == messageId && item.ConversationId == conversationId, cancellationToken);
        if (conversation is null || message is null) return Results.NotFound();
        // The sender may delete their message; in a course chat, a moderator may remove anyone's.
        var moderates = conversation.Kind == ConversationKind.Course && HasPermission(httpContext, LmsPermissions.ForumModerate);
        if (message.SenderUserId != userId && !moderates) return Results.Json(new { message = "You can only delete your own messages." }, statusCode: StatusCodes.Status403Forbidden);
        if (message.DeletedAtUtc is not null) return Results.NoContent();

        var key = message.AttachmentKey;
        message.DeletedAtUtc = DateTimeOffset.UtcNow;
        message.Body = string.Empty;
        message.AttachmentKey = null; message.AttachmentName = null; message.AttachmentContentType = null; message.AttachmentSizeBytes = null;
        await db.SaveChangesAsync(cancellationToken);
        if (key is not null) await storage.DeleteAsync(key, cancellationToken);
        await PublishAsync(hub, db, "deleted", conversation, message.Id, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> DownloadAsync(Guid conversationId, Guid messageId, HttpContext httpContext, LmsDbContext db, IContentAssetStorage storage, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        if (await FindAccessibleAsync(db, httpContext, userId, conversationId, cancellationToken) is null) return Results.NotFound();
        var message = await db.ConversationMessages.AsNoTracking().SingleOrDefaultAsync(item => item.Id == messageId && item.ConversationId == conversationId, cancellationToken);
        if (message?.AttachmentKey is null || message.DeletedAtUtc is not null) return Results.NotFound();
        var stream = await storage.OpenReadAsync(message.AttachmentKey, cancellationToken);
        return stream is null ? Results.NotFound() : Results.File(stream, "application/octet-stream", message.AttachmentName ?? "attachment");
    }

    /// <summary>
    /// Server-sent events: the browser keeps this request open and is told the moment a conversation it can reach changes.
    /// The signal holds no message text; the browser fetches the messages through the normal, access-checked calls.
    /// </summary>
    private static async Task StreamAsync(HttpContext httpContext, ITenantContext tenantContext, LmsDbContext db, MessageHub hub, CancellationToken requestAborted)
    {
        if (tenantContext.TenantId is not Guid tenantId || GetUserId(httpContext) is not Guid userId) { httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
        var response = httpContext.Response;
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";   // tell a reverse proxy not to hold the events back
        await response.WriteAsync(": connected\n\n", requestAborted);
        await response.Body.FlushAsync(requestAborted);

        using var subscription = hub.Subscribe(tenantId, userId);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        lifetime.CancelAfter(StreamLifetime);
        var manager = HasPermission(httpContext, LmsPermissions.CourseManage);
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                wait.CancelAfter(StreamPing);
                MessageEvent? signal = null;
                try { if (await subscription.Events.Reader.WaitToReadAsync(wait.Token)) subscription.Events.Reader.TryRead(out signal); else break; }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { /* quiet for a while: send a ping so the connection stays open */ }

                if (signal is null) { await response.WriteAsync(": ping\n\n", lifetime.Token); }
                else
                {
                    // A course chat's signal goes to people who can reach that course (checked now, so leaving a course stops the signals).
                    if (signal.Recipients is null && !manager && !(signal.CourseId is Guid courseId && await db.Enrollments.AsNoTracking().AnyAsync(item => item.CourseId == courseId && item.LearnerUserId == userId && (item.Status == EnrollmentStatus.Active || item.Status == EnrollmentStatus.Completed), lifetime.Token))) continue;
                    var data = JsonSerializer.Serialize(new { type = signal.Type, conversationId = signal.ConversationId, messageId = signal.MessageId }, JsonSerializerOptions.Web);
                    await response.WriteAsync($"event: message\ndata: {data}\n\n", lifetime.Token);
                }
                await response.Body.FlushAsync(lifetime.Token);
            }
        }
        catch (OperationCanceledException) { /* the browser went away, or the connection reached its lifetime: the browser reconnects */ }
    }

    private static async Task<IResult> MarkReadAsync(Guid conversationId, HttpContext httpContext, ITenantContext tenantContext, LmsDbContext db, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        if (await FindAccessibleAsync(db, httpContext, userId, conversationId, cancellationToken) is null) return Results.NotFound();
        await TouchParticipantAsync(db, tenantId, conversationId, userId, DateTimeOffset.UtcNow, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    // ---------- access ----------
    private sealed record Access(Conversation Conversation, DateTimeOffset LastRead);

    /// <summary>Direct conversations the user is in, plus course chats for courses the user can reach.</summary>
    private static async Task<List<Access>> AccessibleAsync(LmsDbContext db, HttpContext httpContext, Guid userId, CancellationToken cancellationToken)
    {
        var participants = await db.ConversationParticipants.AsNoTracking().Where(item => item.UserId == userId).ToListAsync(cancellationToken);
        var participantByConversation = participants.ToDictionary(item => item.ConversationId);
        var directIds = participants.Select(item => item.ConversationId).ToList();
        var access = (await db.Conversations.AsNoTracking().Where(item => item.Kind == ConversationKind.Direct && directIds.Contains(item.Id)).ToListAsync(cancellationToken))
            .Select(item => new Access(item, participantByConversation[item.Id].LastReadAtUtc)).ToList();

        var manager = HasPermission(httpContext, LmsPermissions.CourseManage);
        var enrollments = await db.Enrollments.AsNoTracking().Where(item => item.LearnerUserId == userId && (item.Status == EnrollmentStatus.Active || item.Status == EnrollmentStatus.Completed))
            .ToDictionaryAsync(item => item.CourseId, item => item.EnrolledAtUtc, cancellationToken);
        var courseConversations = await db.Conversations.AsNoTracking().Where(item => item.Kind == ConversationKind.Course).ToListAsync(cancellationToken);
        foreach (var conversation in courseConversations)
        {
            var courseId = conversation.CourseId ?? Guid.Empty;
            if (!manager && !enrollments.ContainsKey(courseId)) continue;
            // Before first opening, only messages sent after you joined (enrolled) or after the chat was created count as unread.
            var lastRead = participantByConversation.TryGetValue(conversation.Id, out var participant) ? participant.LastReadAtUtc
                : enrollments.TryGetValue(courseId, out var enrolledAt) ? enrolledAt : conversation.CreatedAtUtc;
            access.Add(new Access(conversation, lastRead));
        }
        return access;
    }

    private static async Task<Conversation?> FindAccessibleAsync(LmsDbContext db, HttpContext httpContext, Guid userId, Guid conversationId, CancellationToken cancellationToken)
        => (await AccessibleAsync(db, httpContext, userId, cancellationToken)).Select(item => item.Conversation).FirstOrDefault(item => item.Id == conversationId);

    private static async Task<bool> CanAccessCourseAsync(LmsDbContext db, HttpContext httpContext, Guid userId, Guid courseId, CancellationToken cancellationToken)
        => HasPermission(httpContext, LmsPermissions.CourseManage)
           || await db.Enrollments.AnyAsync(item => item.CourseId == courseId && item.LearnerUserId == userId && (item.Status == EnrollmentStatus.Active || item.Status == EnrollmentStatus.Completed), cancellationToken);

    private static async Task<Dictionary<Guid, int>> UnreadByConversationAsync(LmsDbContext db, Guid userId, List<Access> access, CancellationToken cancellationToken)
    {
        var ids = access.Select(item => item.Conversation.Id).ToList();
        var messages = await db.ConversationMessages.AsNoTracking().Where(item => ids.Contains(item.ConversationId) && item.SenderUserId != userId && item.DeletedAtUtc == null)
            .Select(item => new { item.ConversationId, item.CreatedAtUtc }).ToListAsync(cancellationToken);
        return access.ToDictionary(item => item.Conversation.Id, item => messages.Count(m => m.ConversationId == item.Conversation.Id && m.CreatedAtUtc > item.LastRead));
    }

    private static async Task TouchParticipantAsync(LmsDbContext db, Guid tenantId, Guid conversationId, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var participant = await db.ConversationParticipants.SingleOrDefaultAsync(item => item.ConversationId == conversationId && item.UserId == userId, cancellationToken);
        if (participant is null)
            db.ConversationParticipants.Add(new ConversationParticipant { Id = Guid.NewGuid(), TenantId = tenantId, ConversationId = conversationId, UserId = userId, JoinedAtUtc = now, LastReadAtUtc = now });
        else participant.LastReadAtUtc = now;
    }

    /// <summary>Staff = anyone whose role can manage courses or grades.</summary>
    private static async Task<HashSet<Guid>> StaffIdsAsync(LmsDbContext db, CancellationToken cancellationToken)
        => (await db.TenantMemberships.AsNoTracking()
            .Where(item => item.Status == MembershipStatus.Active && item.Role.Permissions.Any(p => p.PermissionCode == LmsPermissions.CourseManage || p.PermissionCode == LmsPermissions.GradeManage))
            .Select(item => item.UserId).ToListAsync(cancellationToken)).ToHashSet();

    private static bool CanContact(HttpContext httpContext, IConfiguration configuration, HashSet<Guid> staffIds, Guid otherUserId)
        => HasPermission(httpContext, LmsPermissions.CourseManage) || HasPermission(httpContext, LmsPermissions.GradeManage)
           || staffIds.Contains(otherUserId) || configuration.GetValue("Messaging:AllowLearnerToLearner", false);

    // ---------- helpers ----------
    private static string DirectKey(Guid a, Guid b) => string.CompareOrdinal(a.ToString("N"), b.ToString("N")) < 0 ? $"{a:N}:{b:N}" : $"{b:N}:{a:N}";

    private static Guid OtherUser(Conversation conversation, Guid userId)
    {
        var parts = (conversation.DirectKey ?? string.Empty).Split(':');
        return parts.Length == 2 && Guid.TryParseExact(parts[0] == userId.ToString("N") ? parts[1] : parts[0], "N", out var other) ? other : Guid.Empty;
    }

    private static string Preview(string body) => body.Length > 80 ? body[..80] + "…" : body;

    private static Guid? GetUserId(HttpContext context)
        => Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub"), out var id) ? id : null;

    private static bool HasPermission(HttpContext context, string permission)
        => context.User.Claims.Any(claim => claim.Type == "permission" && claim.Value.Equals(permission, StringComparison.OrdinalIgnoreCase));
}

public sealed record StartDirectRequest(Guid UserId);
public sealed record OpenCourseChatRequest(Guid CourseId);
public sealed record SendMessageRequest(string? Body);
public sealed record ContactResponse(Guid UserId, string Name, string Email, bool IsStaff);
public sealed record ConversationSummary(Guid Id, string Kind, string Title, Guid? CourseId, string? LastMessagePreview, DateTimeOffset LastActivityAtUtc, int UnreadCount);
public sealed record AttachmentView(string FileName, long SizeBytes, string ContentType);
public sealed record MessageResponse(Guid Id, Guid SenderUserId, string SenderName, string Body, DateTimeOffset CreatedAtUtc, bool IsMine, DateTimeOffset? EditedAtUtc = null, bool IsDeleted = false, AttachmentView? Attachment = null);
