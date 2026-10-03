using System.Security.Claims;
using Lms.Api.Domain.Courses;
using Lms.Api.Domain.Identity;
using Lms.Api.Domain.Learning;
using Lms.Api.Domain.Messaging;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Messaging;

/// <summary>
/// Direct messages and course chats.
/// Privacy rule: learners may start direct conversations only with staff (people who can manage courses or grades),
/// never with other learners, unless the tenant sets Messaging:AllowLearnerToLearner.
/// </summary>
public static class MessagingEndpoints
{
    private const int MaxBodyLength = 5000;
    private const int PageSize = 100;

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
                message is null ? null : Preview(message.Body), message?.CreatedAtUtc ?? conversation.CreatedAtUtc, unread.GetValueOrDefault(conversation.Id));
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
        return Results.Ok(messages.Select(item => new MessageResponse(item.Id, item.SenderUserId, names.GetValueOrDefault(item.SenderUserId, "Unknown"), item.Body, item.CreatedAtUtc, item.SenderUserId == userId)));
    }

    private static async Task<IResult> SendAsync(Guid conversationId, HttpContext httpContext, ITenantContext tenantContext, LmsDbContext db, SendMessageRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var body = request.Body?.Trim() ?? string.Empty;
        if (body.Length is < 1 or > MaxBodyLength) return Results.BadRequest(new { message = $"A message must be between 1 and {MaxBodyLength} characters." });
        var conversation = await FindAccessibleAsync(db, httpContext, userId, conversationId, cancellationToken);
        if (conversation is null) return Results.NotFound();

        var now = DateTimeOffset.UtcNow;
        var message = new ConversationMessage { Id = Guid.NewGuid(), TenantId = tenantId, ConversationId = conversationId, SenderUserId = userId, Body = body, CreatedAtUtc = now };
        db.ConversationMessages.Add(message);
        var tracked = await db.Conversations.SingleAsync(item => item.Id == conversationId, cancellationToken);
        tracked.LastMessageAtUtc = now;
        await TouchParticipantAsync(db, tenantId, conversationId, userId, now, cancellationToken); // sending counts as reading up to now
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/messages/conversations/{conversationId}/messages", new MessageResponse(message.Id, userId, "You", body, now, true));
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
        var messages = await db.ConversationMessages.AsNoTracking().Where(item => ids.Contains(item.ConversationId) && item.SenderUserId != userId)
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
public sealed record MessageResponse(Guid Id, Guid SenderUserId, string SenderName, string Body, DateTimeOffset CreatedAtUtc, bool IsMine);
