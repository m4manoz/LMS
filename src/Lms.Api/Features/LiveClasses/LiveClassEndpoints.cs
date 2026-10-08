using System.Security.Claims;
using System.Text.Json;
using Lms.Api.Domain.Identity;
using Lms.Api.Domain.LiveClasses;
using Lms.Api.Infrastructure.LiveClasses;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.LiveClasses;

public static class LiveClassEndpoints
{
    public static void MapLiveClassEndpoints(this WebApplication app)
    {
        var tenant = app.MapGroup("/api/v1/tenant/live-classes").RequireAuthorization("tenant.authenticated");
        tenant.AddEndpointFilter(async (context, next) =>
        {
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (!tenantContext.IsResolved) return Results.BadRequest(new { message = "A tenant is required. Use the X-Tenant-Slug header or a tenant subdomain." });
            return await next(context);
        });
        tenant.MapGet("/provider", GetProviderAsync).RequireAuthorization("tenant.liveclass.read");
        tenant.MapGet("/sessions", ListSessionsAsync).RequireAuthorization("tenant.liveclass.read");
        tenant.MapPost("/sessions", CreateSessionAsync).RequireAuthorization("tenant.liveclass.manage");
        tenant.MapPost("/sessions/{sessionId:guid}/join", JoinSessionAsync).RequireAuthorization("tenant.liveclass.read");
        tenant.MapGet("/sessions/{sessionId:guid}/join-status", JoinStatusAsync).RequireAuthorization("tenant.liveclass.read");
        tenant.MapGet("/sessions/{sessionId:guid}/join-requests", ListJoinRequestsAsync).RequireAuthorization("tenant.liveclass.manage");
        tenant.MapPost("/sessions/{sessionId:guid}/join-requests/admit-all", AdmitAllAsync).RequireAuthorization("tenant.liveclass.manage");
        tenant.MapPost("/sessions/{sessionId:guid}/join-requests/{userId:guid}/admit", AdmitAsync).RequireAuthorization("tenant.liveclass.manage");
        tenant.MapPost("/sessions/{sessionId:guid}/join-requests/{userId:guid}/decline", DeclineAsync).RequireAuthorization("tenant.liveclass.manage");
        tenant.MapPost("/sessions/{sessionId:guid}/leave", LeaveSessionAsync).RequireAuthorization("tenant.liveclass.read");
        tenant.MapPost("/sessions/{sessionId:guid}/close", CloseSessionAsync).RequireAuthorization("tenant.liveclass.manage");
        tenant.MapGet("/sessions/{sessionId:guid}/attendance", ListAttendanceAsync).RequireAuthorization("tenant.attendance.read");
        tenant.MapGet("/sessions/{sessionId:guid}/announcements", ListAnnouncementsAsync).RequireAuthorization("tenant.collaboration.read");
        tenant.MapPost("/sessions/{sessionId:guid}/announcements", CreateAnnouncementAsync).RequireAuthorization("tenant.liveclass.manage");
        tenant.MapGet("/sessions/{sessionId:guid}/chat", ListChatAsync).RequireAuthorization("tenant.collaboration.read");
        tenant.MapPost("/sessions/{sessionId:guid}/chat", CreateChatAsync).RequireAuthorization("tenant.collaboration.manage");
        tenant.MapGet("/sessions/{sessionId:guid}/events", StreamEventsAsync).RequireAuthorization("tenant.liveclass.read");
        tenant.MapPost("/sessions/{sessionId:guid}/consent", SetRecordingConsentAsync).RequireAuthorization("tenant.liveclass.read");
        tenant.MapGet("/sessions/{sessionId:guid}/recording", GetRecordingAsync).RequireAuthorization("tenant.liveclass.read");
        tenant.MapPost("/sessions/{sessionId:guid}/recording/request", RequestRecordingAsync).RequireAuthorization("tenant.liveclass.manage");
        tenant.MapPost("/sessions/{sessionId:guid}/recording/link", LinkRecordingAsync).RequireAuthorization("tenant.liveclass.manage");
        tenant.MapPost("/sessions/{sessionId:guid}/recording/start", StartRecordingAsync).RequireAuthorization("tenant.liveclass.manage");
        tenant.MapPost("/sessions/{sessionId:guid}/recording/stop", StopRecordingAsync).RequireAuthorization("tenant.liveclass.manage");
        tenant.MapPost("/sessions/{sessionId:guid}/recording/retry", RetryRecordingAsync).RequireAuthorization("tenant.liveclass.manage");
        tenant.MapGet("/sessions/{sessionId:guid}/recording/tracks", ListTracksAsync).RequireAuthorization("tenant.liveclass.manage");
        // The host switches off one person's microphone, or everyone's but their own (the person can switch it on again).
        tenant.MapPost("/sessions/{sessionId:guid}/mute/{userId:guid}", MuteOneAsync).RequireAuthorization("tenant.liveclass.manage");
        tenant.MapPost("/sessions/{sessionId:guid}/mute-all", MuteAllAsync).RequireAuthorization("tenant.liveclass.manage");
        // LiveKit calls this itself, so it carries no sign-in; the call is checked against the organization's LiveKit secret instead.
        app.MapPost("/api/v1/integrations/livekit/webhook", ReceiveWebhookAsync).AllowAnonymous();
        // Everyone in the class can see whose hand is up; only the host and staff can put someone else's hand down.
        tenant.MapGet("/sessions/{sessionId:guid}/hand-raises", ListHandRaisesAsync).RequireAuthorization("tenant.liveclass.read");
        tenant.MapPost("/sessions/{sessionId:guid}/hand-raises/{userId:guid}/lower", LowerHandRaiseAsync).RequireAuthorization("tenant.liveclass.manage");
        tenant.MapPost("/sessions/{sessionId:guid}/hand-raise", SetHandRaiseAsync).RequireAuthorization("tenant.collaboration.manage");
        tenant.MapGet("/sessions/{sessionId:guid}/polls", ListPollsAsync).RequireAuthorization("tenant.collaboration.read");
        tenant.MapPost("/sessions/{sessionId:guid}/polls", CreatePollAsync).RequireAuthorization("tenant.liveclass.manage");
        tenant.MapPost("/sessions/{sessionId:guid}/polls/{pollId:guid}/vote", VotePollAsync).RequireAuthorization("tenant.collaboration.manage");
        tenant.MapPost("/sessions/{sessionId:guid}/polls/{pollId:guid}/close", ClosePollAsync).RequireAuthorization("tenant.liveclass.manage");
    }

    private static async Task<IResult> ReceiveWebhookAsync(HttpContext httpContext, LiveKitClassService service, CancellationToken cancellationToken)
    {
        if (httpContext.Request.ContentLength is > 1_000_000) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        using var reader = new StreamReader(httpContext.Request.Body);
        var body = await reader.ReadToEndAsync(cancellationToken);
        var outcome = await service.HandleWebhookAsync(body, httpContext.Request.Headers.Authorization.ToString(), cancellationToken);
        return outcome == WebhookOutcome.Rejected ? Results.Unauthorized() : Results.Ok();
    }

    private static async Task<IResult> MuteOneAsync(HttpContext httpContext, LmsDbContext db, LiveKitClassService service, Guid sessionId, Guid userId, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid askedBy) return Results.Unauthorized();
        var session = await db.LiveClassSessions.SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        if (session is null) return Results.NotFound();
        if (!await CanAccessSessionAsync(httpContext, db, session, askedBy, cancellationToken)) return Results.Forbid();
        var result = await service.MuteAsync(session, userId, askedBy, cancellationToken);
        return result.Ok ? Results.Ok(new { muted = result.Muted }) : Results.Json(new { message = result.Message }, statusCode: result.StatusCode);
    }

    private static async Task<IResult> MuteAllAsync(HttpContext httpContext, LmsDbContext db, LiveKitClassService service, Guid sessionId, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid askedBy) return Results.Unauthorized();
        var session = await db.LiveClassSessions.SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        if (session is null) return Results.NotFound();
        if (!await CanAccessSessionAsync(httpContext, db, session, askedBy, cancellationToken)) return Results.Forbid();
        var result = await service.MuteAsync(session, null, askedBy, cancellationToken);
        return result.Ok ? Results.Ok(new { muted = result.Muted }) : Results.Json(new { message = result.Message }, statusCode: result.StatusCode);
    }

    /// <summary>The recordings of each person on their own, with the course file to open for each finished one.</summary>
    private static async Task<IResult> ListTracksAsync(HttpContext httpContext, LmsDbContext db, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await db.LiveClassSessions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        if (session is null) return Results.NotFound();
        if (GetUserId(httpContext) is not Guid userId || !await CanAccessSessionAsync(httpContext, db, session, userId, cancellationToken)) return Results.Forbid();
        var tracks = await db.SessionTrackRecordings.AsNoTracking().Where(item => item.SessionId == sessionId).OrderBy(item => item.StartedAtUtc).ToListAsync(cancellationToken);
        var ids = tracks.Select(item => item.UserId).Distinct().ToArray();
        var names = await db.Users.AsNoTracking().Where(item => ids.Contains(item.Id)).ToDictionaryAsync(item => item.Id, item => item.DisplayName, cancellationToken);
        return Results.Ok(tracks.Select(item => new TrackRecordingResponse(item.Id, item.UserId, names.GetValueOrDefault(item.UserId, "User"), item.Status.ToString(), item.StartedAtUtc, item.FinishedAtUtc, item.DurationSeconds, item.SizeBytes, item.LastError,
            item.ContentAssetId is Guid asset && session.CourseId is Guid course ? $"/api/v1/tenant/courses/{course:D}/assets/{asset:D}" : null)).ToArray());
    }

    private static async Task<IResult> ListSessionsAsync(HttpContext httpContext, LmsDbContext db, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var canManage = HasPermission(httpContext, LmsPermissions.LiveClassManage);
        var sessions = await db.LiveClassSessions.AsNoTracking().Where(item => canManage
                || item.HostUserId == userId
                || (item.CourseId != null && db.Enrollments.Any(enrollment => enrollment.CourseId == item.CourseId && enrollment.LearnerUserId == userId && (enrollment.Status == Lms.Api.Domain.Learning.EnrollmentStatus.Active || enrollment.Status == Lms.Api.Domain.Learning.EnrollmentStatus.Completed))))
            .OrderBy(item => item.StartAtUtc).Take(200).ToListAsync(cancellationToken);
        var courseIds = sessions.Where(item => item.CourseId.HasValue).Select(item => item.CourseId!.Value).Distinct().ToArray();
        var courses = await db.Courses.AsNoTracking().Where(item => courseIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, item => item.Title, cancellationToken);
        return Results.Ok(sessions.Select(item => ToResponse(item, item.CourseId is Guid courseId && courses.TryGetValue(courseId, out var title) ? title : null)).ToArray());
    }

    private static async Task<IResult> CreateSessionAsync(HttpContext httpContext, LmsDbContext db, LiveClassProviderResolver resolver, ITenantContext tenantContext, LiveKitRecordings liveKitRecordings, CreateLiveSessionRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || GetUserId(httpContext) is not Guid hostUserId) return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 250) return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Title)] = ["Title is required and must be at most 250 characters."] });
        if (request.EndAtUtc <= request.StartAtUtc) return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.EndAtUtc)] = ["End time must be after the start time."] });
        if (request.CourseId is Guid courseId && await db.Courses.AsNoTracking().SingleOrDefaultAsync(item => item.Id == courseId, cancellationToken) is null) return Results.NotFound(new { message = "The selected course was not found." });
        var session = new LiveClassSession { Id = Guid.NewGuid(), TenantId = tenantId, CourseId = request.CourseId, HostUserId = hostUserId, Title = request.Title.Trim(), Description = request.Description?.Trim(), StartAtUtc = request.StartAtUtc, EndAtUtc = request.EndAtUtc, Status = LiveSessionStatus.Scheduled, RequireApproval = request.RequireApproval, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow };
        var (provider, settings) = await resolver.ResolveAsync(db, cancellationToken);
        LiveMeetingProvisioningResult meeting;
        try { meeting = await provider.CreateMeetingAsync(new LiveMeetingRequest(session.Id, session.Title, session.StartAtUtc, session.EndAtUtc, request.MeetingUrl, settings?.JitsiBaseUrl), cancellationToken); }
        catch (LiveClassProviderException exception) { return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.MeetingUrl)] = [exception.Message] }); }
        session.Provider = meeting.Provider; session.ProviderMeetingId = meeting.MeetingId; session.JoinUrl = meeting.JoinUrl; session.HostUrl = meeting.HostUrl;
        if (request.AutoRecord)
        {
            // Scheduling the class this way is the host's agreement to recording it.
            if (!session.Provider.Equals("livekit", StringComparison.OrdinalIgnoreCase)) return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.AutoRecord)] = ["Only classes held in LiveKit can record themselves."] });
            if (request.CourseId is null) return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.AutoRecord)] = ["Link the class to a course so its recording can be saved in the video library."] });
            if (!liveKitRecordings.Enabled) return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.AutoRecord)] = ["Recording LiveKit classes is not set up on this server."] });
            session.AutoRecord = true;
            db.SessionConsents.Add(new SessionConsent { Id = Guid.NewGuid(), TenantId = tenantId, SessionId = session.Id, UserId = hostUserId, ConsentType = "recording", Granted = true, RecordedAtUtc = DateTimeOffset.UtcNow, IpAddress = httpContext.Connection.RemoteIpAddress?.ToString() });
        }
        db.LiveClassSessions.Add(session); await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/live-classes/sessions/{session.Id:D}", ToResponse(session, null));
    }

    private static async Task<IResult> JoinSessionAsync(HttpContext httpContext, LmsDbContext db, LiveKitCredentialStore liveKitCredentials, Guid sessionId, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var session = await db.LiveClassSessions.SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        if (session is null) return Results.NotFound();
        if (!await CanAccessSessionAsync(httpContext, db, session, userId, cancellationToken)) return Results.Forbid();
        if (session.Status == LiveSessionStatus.Cancelled || session.Status == LiveSessionStatus.Completed) return Results.Conflict(new { message = "This live session is no longer joinable." });
        // A class with a waiting room: everyone but the host and staff asks to come in and waits for a decision.
        if (NeedsApproval(httpContext, session, userId))
        {
            var request = await db.SessionJoinRequests.SingleOrDefaultAsync(item => item.SessionId == sessionId && item.UserId == userId, cancellationToken);
            if (request is { Status: JoinRequestStatus.Declined }) return Results.Json(new { status = "Declined", message = "The host did not let you into this class." }, statusCode: StatusCodes.Status403Forbidden);
            if (request is not { Status: JoinRequestStatus.Admitted })
            {
                if (request is null) { request = new SessionJoinRequest { Id = Guid.NewGuid(), TenantId = session.TenantId, SessionId = sessionId, UserId = userId, Status = JoinRequestStatus.Waiting, RequestedAtUtc = DateTimeOffset.UtcNow }; db.SessionJoinRequests.Add(request); await db.SaveChangesAsync(cancellationToken); }
                return Results.Json(new { status = "Waiting", message = "Waiting for the host to let you in." }, statusCode: StatusCodes.Status202Accepted);
            }
        }
        LiveKitJoin? liveKit = null;
        if (string.Equals(session.Provider, "livekit", StringComparison.OrdinalIgnoreCase))
        {
            // The room is joined with a token made for this person now, so it is checked before anything is recorded.
            var credentials = liveKitCredentials.Resolve(await db.LiveClassSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken));
            if (credentials is null) return Results.Conflict(new { message = "The organization's LiveKit settings are incomplete. Ask an administrator to check Integrations." });
            var isHost = userId == session.HostUserId || HasPermission(httpContext, LmsPermissions.LiveClassManage);
            var name = await db.Users.AsNoTracking().Where(item => item.Id == userId).Select(item => item.DisplayName).SingleOrDefaultAsync(cancellationToken) ?? "Guest";
            var lifetime = TimeSpan.FromHours(Math.Clamp((session.EndAtUtc - DateTimeOffset.UtcNow).TotalHours + 1, 1, 12));
            liveKit = new LiveKitJoin(credentials.Url, LiveKitTokens.Create(credentials, session.ProviderMeetingId, userId.ToString("D"), name, canPublish: true, roomAdmin: isHost, lifetime), session.ProviderMeetingId, true, isHost);
        }
        if (session.Status == LiveSessionStatus.Scheduled && session.StartAtUtc <= DateTimeOffset.UtcNow) session.Status = LiveSessionStatus.Live;
        var attendance = await db.SessionAttendances.SingleOrDefaultAsync(item => item.SessionId == sessionId && item.UserId == userId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (attendance is null) { attendance = new SessionAttendance { Id = Guid.NewGuid(), TenantId = session.TenantId, SessionId = sessionId, UserId = userId, JoinedAtUtc = now }; db.SessionAttendances.Add(attendance); }
        attendance.Status = AttendanceStatus.Present; attendance.LeftAtUtc = null; attendance.UpdatedAtUtc = now;
        session.UpdatedAtUtc = now; await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new { session = ToResponse(session, null), attendance = new AttendanceResponse(attendance.Id, attendance.UserId, "", attendance.Status.ToString(), attendance.JoinedAtUtc, attendance.LeftAtUtc, attendance.DurationSeconds), meetingUrl = userId == session.HostUserId ? session.HostUrl : session.JoinUrl, liveKit });
    }

    private static bool NeedsApproval(HttpContext httpContext, LiveClassSession session, Guid userId)
        => session.RequireApproval && userId != session.HostUserId && !HasPermission(httpContext, LmsPermissions.LiveClassManage);

    /// <summary>Where the person stands with the waiting room, so their screen can keep asking until they are let in.</summary>
    private static async Task<IResult> JoinStatusAsync(HttpContext httpContext, LmsDbContext db, Guid sessionId, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var session = await db.LiveClassSessions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        if (session is null || !await CanAccessSessionAsync(httpContext, db, session, userId, cancellationToken)) return Results.NotFound();
        if (!NeedsApproval(httpContext, session, userId)) return Results.Ok(new { status = "NotRequired", closed = session.Status is LiveSessionStatus.Completed or LiveSessionStatus.Cancelled });
        var request = await db.SessionJoinRequests.AsNoTracking().SingleOrDefaultAsync(item => item.SessionId == sessionId && item.UserId == userId, cancellationToken);
        return Results.Ok(new { status = request?.Status.ToString() ?? "None", closed = session.Status is LiveSessionStatus.Completed or LiveSessionStatus.Cancelled });
    }

    private static async Task<IResult> ListJoinRequestsAsync(HttpContext httpContext, LmsDbContext db, Guid sessionId, CancellationToken cancellationToken)
    {
        if (!await CanAccessBySessionIdAsync(httpContext, db, sessionId, cancellationToken)) return Results.Forbid();
        var waiting = await db.SessionJoinRequests.AsNoTracking().Where(item => item.SessionId == sessionId && item.Status == JoinRequestStatus.Waiting).OrderBy(item => item.RequestedAtUtc).ToListAsync(cancellationToken);
        var ids = waiting.Select(item => item.UserId).ToArray();
        var names = await db.Users.AsNoTracking().Where(item => ids.Contains(item.Id)).ToDictionaryAsync(item => item.Id, item => item.DisplayName, cancellationToken);
        return Results.Ok(waiting.Select(item => new JoinRequestResponse(item.UserId, names.GetValueOrDefault(item.UserId, "User"), item.Status.ToString(), item.RequestedAtUtc)).ToList());
    }

    private static Task<IResult> AdmitAsync(HttpContext httpContext, LmsDbContext db, Guid sessionId, Guid userId, CancellationToken cancellationToken) => DecideAsync(httpContext, db, sessionId, userId, JoinRequestStatus.Admitted, cancellationToken);
    private static Task<IResult> DeclineAsync(HttpContext httpContext, LmsDbContext db, Guid sessionId, Guid userId, CancellationToken cancellationToken) => DecideAsync(httpContext, db, sessionId, userId, JoinRequestStatus.Declined, cancellationToken);

    private static async Task<IResult> DecideAsync(HttpContext httpContext, LmsDbContext db, Guid sessionId, Guid userId, JoinRequestStatus decision, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid hostId || !await CanAccessBySessionIdAsync(httpContext, db, sessionId, cancellationToken)) return Results.Forbid();
        var request = await db.SessionJoinRequests.SingleOrDefaultAsync(item => item.SessionId == sessionId && item.UserId == userId, cancellationToken);
        if (request is null) return Results.NotFound();
        request.Status = decision; request.DecidedAtUtc = DateTimeOffset.UtcNow; request.DecidedByUserId = hostId;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> AdmitAllAsync(HttpContext httpContext, LmsDbContext db, Guid sessionId, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid hostId || !await CanAccessBySessionIdAsync(httpContext, db, sessionId, cancellationToken)) return Results.Forbid();
        var waiting = await db.SessionJoinRequests.Where(item => item.SessionId == sessionId && item.Status == JoinRequestStatus.Waiting).ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        foreach (var request in waiting) { request.Status = JoinRequestStatus.Admitted; request.DecidedAtUtc = now; request.DecidedByUserId = hostId; }
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new { admitted = waiting.Count });
    }

    private static async Task<IResult> LeaveSessionAsync(HttpContext httpContext, LmsDbContext db, Guid sessionId, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        if (!await CanAccessBySessionIdAsync(httpContext, db, sessionId, cancellationToken)) return Results.Forbid();
        var attendance = await db.SessionAttendances.SingleOrDefaultAsync(item => item.SessionId == sessionId && item.UserId == userId, cancellationToken);
        if (attendance is null) return Results.NotFound();
        if (attendance.Status == AttendanceStatus.Left) return Results.Ok(new AttendanceResponse(attendance.Id, attendance.UserId, "", attendance.Status.ToString(), attendance.JoinedAtUtc, attendance.LeftAtUtc, attendance.DurationSeconds));   // LiveKit has already said so
        var now = DateTimeOffset.UtcNow; attendance.Status = AttendanceStatus.Left; attendance.LeftAtUtc = now; attendance.DurationSeconds = Math.Max(0, (int)(now - attendance.JoinedAtUtc).TotalSeconds); attendance.UpdatedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken); return Results.Ok(new AttendanceResponse(attendance.Id, attendance.UserId, "", attendance.Status.ToString(), attendance.JoinedAtUtc, attendance.LeftAtUtc, attendance.DurationSeconds));
    }

    private static async Task<IResult> CloseSessionAsync(HttpContext httpContext, LmsDbContext db, LiveKitRecordings liveKitRecordings, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await db.LiveClassSessions.SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken); if (session is null) return Results.NotFound();
        if (GetUserId(httpContext) is not Guid userId || !await CanAccessSessionAsync(httpContext, db, session, userId, cancellationToken)) return Results.Forbid();
        session.Status = LiveSessionStatus.Completed; session.UpdatedAtUtc = DateTimeOffset.UtcNow; await db.SaveChangesAsync(cancellationToken);
        await liveKitRecordings.StopIfRecordingAsync(session, cancellationToken);   // a class that ends does not keep recording
        return Results.Ok(ToResponse(session, null));
    }

    private static async Task<IResult> ListAttendanceAsync(HttpContext httpContext, LmsDbContext db, Guid sessionId, CancellationToken cancellationToken)
    {
        if (!await CanAccessBySessionIdAsync(httpContext, db, sessionId, cancellationToken)) return Results.Forbid();
        if (!await db.LiveClassSessions.AnyAsync(item => item.Id == sessionId, cancellationToken)) return Results.NotFound();
        var attendance = await db.SessionAttendances.AsNoTracking().Where(item => item.SessionId == sessionId).OrderBy(item => item.JoinedAtUtc).ToListAsync(cancellationToken);
        var userIds = attendance.Select(item => item.UserId).ToArray(); var users = await db.Users.AsNoTracking().Where(item => userIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, item => item.DisplayName, cancellationToken);
        return Results.Ok(attendance.Select(item => new AttendanceResponse(item.Id, item.UserId, users.TryGetValue(item.UserId, out var name) ? name : "User", item.Status.ToString(), item.JoinedAtUtc, item.LeftAtUtc, item.DurationSeconds)).ToArray());
    }

    private static async Task<IResult> ListAnnouncementsAsync(HttpContext httpContext, LmsDbContext db, Guid sessionId, CancellationToken cancellationToken)
    {
        if (!await CanAccessBySessionIdAsync(httpContext, db, sessionId, cancellationToken)) return Results.Forbid();
        var items = await db.SessionAnnouncements.AsNoTracking().Where(item => item.SessionId == sessionId).OrderByDescending(item => item.IsPinned).ThenByDescending(item => item.CreatedAtUtc).Take(100).ToListAsync(cancellationToken);
        return Results.Ok(await WithAuthorNamesAsync(db, items, cancellationToken));
    }

    private static async Task<IResult> CreateAnnouncementAsync(HttpContext httpContext, LmsDbContext db, Guid sessionId, CreateAnnouncementRequest request, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId || string.IsNullOrWhiteSpace(request.Body) || request.Body.Length > 10000) return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Body)] = ["Announcement text is required and must be at most 10,000 characters."] });
        if (!await CanAccessBySessionIdAsync(httpContext, db, sessionId, cancellationToken)) return Results.Forbid();
        var announcement = new SessionAnnouncement { Id = Guid.NewGuid(), TenantId = await db.LiveClassSessions.Where(item => item.Id == sessionId).Select(item => item.TenantId).SingleAsync(cancellationToken), SessionId = sessionId, AuthorUserId = userId, Body = request.Body.Trim(), IsPinned = request.IsPinned, CreatedAtUtc = DateTimeOffset.UtcNow };
        db.SessionAnnouncements.Add(announcement); await db.SaveChangesAsync(cancellationToken); return Results.Created($"/api/v1/tenant/live-classes/sessions/{sessionId:D}/announcements/{announcement.Id:D}", new AnnouncementResponse(announcement.Id, announcement.AuthorUserId, "", announcement.Body, announcement.IsPinned, announcement.CreatedAtUtc));
    }

    private static async Task<IResult> ListChatAsync(HttpContext httpContext, LmsDbContext db, Guid sessionId, CancellationToken cancellationToken)
    {
        if (!await CanAccessBySessionIdAsync(httpContext, db, sessionId, cancellationToken)) return Results.Forbid();
        var items = await db.SessionChatMessages.AsNoTracking().Where(item => item.SessionId == sessionId).OrderByDescending(item => item.CreatedAtUtc).Take(200).ToListAsync(cancellationToken); return Results.Ok(await WithChatNamesAsync(db, items, cancellationToken));
    }

    private static async Task<IResult> CreateChatAsync(HttpContext httpContext, LmsDbContext db, Guid sessionId, CreateChatRequest request, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId || string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > 4000) return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Message)] = ["Message is required and must be at most 4,000 characters."] });
        if (!await CanAccessBySessionIdAsync(httpContext, db, sessionId, cancellationToken)) return Results.Forbid();
        var message = new SessionChatMessage { Id = Guid.NewGuid(), TenantId = await db.LiveClassSessions.Where(item => item.Id == sessionId).Select(item => item.TenantId).SingleAsync(cancellationToken), SessionId = sessionId, UserId = userId, Message = request.Message.Trim(), CreatedAtUtc = DateTimeOffset.UtcNow }; db.SessionChatMessages.Add(message); await db.SaveChangesAsync(cancellationToken); return Results.Created($"/api/v1/tenant/live-classes/sessions/{sessionId:D}/chat/{message.Id:D}", new ChatMessageResponse(message.Id, userId, "", message.Message, message.CreatedAtUtc));
    }

    private static async Task<IResult> SetRecordingConsentAsync(HttpContext httpContext, LmsDbContext db, Guid sessionId, RecordingConsentRequest request, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        if (!await CanAccessBySessionIdAsync(httpContext, db, sessionId, cancellationToken)) return Results.Forbid();
        var consent = await db.SessionConsents.SingleOrDefaultAsync(item => item.SessionId == sessionId && item.UserId == userId && item.ConsentType == "recording", cancellationToken);
        if (consent is null)
        {
            consent = new SessionConsent { Id = Guid.NewGuid(), TenantId = await db.LiveClassSessions.Where(item => item.Id == sessionId).Select(item => item.TenantId).SingleAsync(cancellationToken), SessionId = sessionId, UserId = userId, ConsentType = "recording" };
            db.SessionConsents.Add(consent);
        }
        consent.Granted = request.Granted;
        consent.RecordedAtUtc = DateTimeOffset.UtcNow;
        consent.IpAddress = httpContext.Connection.RemoteIpAddress?.ToString();
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new RecordingConsentResponse(consent.SessionId, consent.UserId, consent.ConsentType, consent.Granted, consent.RecordedAtUtc));
    }

    private static async Task<IResult> GetRecordingAsync(HttpContext httpContext, LmsDbContext db, Guid sessionId, CancellationToken cancellationToken)
    {
        if (!await CanAccessBySessionIdAsync(httpContext, db, sessionId, cancellationToken)) return Results.Forbid();
        var recording = await db.SessionRecordings.AsNoTracking().SingleOrDefaultAsync(item => item.SessionId == sessionId, cancellationToken);
        return recording is null ? Results.NotFound(new { message = "Recording has not been requested for this session." }) : Results.Ok(ToRecordingResponse(recording));
    }

    private static async Task<IResult> RequestRecordingAsync(HttpContext httpContext, LmsDbContext db, LiveClassProviderResolver resolver, Guid sessionId, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var session = await db.LiveClassSessions.SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        if (session is null) return Results.NotFound();
        if (!await CanAccessSessionAsync(httpContext, db, session, userId, cancellationToken)) return Results.Forbid();
        if (session.Provider.Equals("livekit", StringComparison.OrdinalIgnoreCase))
            return Results.Conflict(new { message = "Classes held in LiveKit are recorded with Start recording while the class is on." });
        if (!resolver.ForSession(session.Provider).CanRecord)
            return Results.Conflict(new { message = "This class is held in another tool, so it cannot be recorded from here. Record it there, then add the recording link." });
        var hostConsent = await db.SessionConsents.AsNoTracking().AnyAsync(item => item.SessionId == sessionId && item.UserId == session.HostUserId && item.ConsentType == "recording" && item.Granted, cancellationToken);
        if (!hostConsent) return Results.Conflict(new { message = "The session host must grant recording consent before recording can be requested." });
        var recording = await db.SessionRecordings.SingleOrDefaultAsync(item => item.SessionId == sessionId, cancellationToken);
        if (recording is not null)
        {
            if (recording.Status == RecordingStatus.Failed) return Results.Conflict(new { message = "Recording provisioning failed. Use retry after reviewing the failure." });
            return recording.Status == RecordingStatus.Available ? Results.Ok(ToRecordingResponse(recording)) : Results.Accepted($"/api/v1/tenant/live-classes/sessions/{sessionId:D}/recording", ToRecordingResponse(recording));
        }
        recording = new SessionRecording { Id = Guid.NewGuid(), TenantId = session.TenantId, SessionId = sessionId, Provider = session.Provider, Status = RecordingStatus.Requested, RequestedAtUtc = DateTimeOffset.UtcNow, NextAttemptAtUtc = DateTimeOffset.UtcNow };
        db.SessionRecordings.Add(recording);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Accepted($"/api/v1/tenant/live-classes/sessions/{sessionId:D}/recording", ToRecordingResponse(recording));
    }

    /// <summary>Starts recording a class held in LiveKit. The host must have agreed to recording first, as for any recording.</summary>
    private static async Task<IResult> StartRecordingAsync(HttpContext httpContext, LmsDbContext db, LiveKitRecordings liveKitRecordings, Guid sessionId, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var session = await db.LiveClassSessions.SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        if (session is null) return Results.NotFound();
        if (!await CanAccessSessionAsync(httpContext, db, session, userId, cancellationToken)) return Results.Forbid();
        var hostConsent = await db.SessionConsents.AsNoTracking().AnyAsync(item => item.SessionId == sessionId && item.UserId == session.HostUserId && item.ConsentType == "recording" && item.Granted, cancellationToken);
        if (!hostConsent) return Results.Conflict(new { message = "The session host must grant recording consent before recording can start." });
        var result = await liveKitRecordings.StartAsync(session, cancellationToken);
        if (!result.Started) return Results.Json(new { message = result.Message }, statusCode: result.StatusCode);
        return Results.Accepted($"/api/v1/tenant/live-classes/sessions/{sessionId:D}/recording", ToRecordingResponse(await db.SessionRecordings.AsNoTracking().SingleAsync(item => item.SessionId == sessionId, cancellationToken)));
    }

    private static async Task<IResult> StopRecordingAsync(HttpContext httpContext, LmsDbContext db, LiveKitRecordings liveKitRecordings, Guid sessionId, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var session = await db.LiveClassSessions.SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        if (session is null) return Results.NotFound();
        if (!await CanAccessSessionAsync(httpContext, db, session, userId, cancellationToken)) return Results.Forbid();
        var result = await liveKitRecordings.StopAsync(session, cancellationToken);
        if (!result.Started) return Results.Json(new { message = result.Message }, statusCode: result.StatusCode);
        return Results.Accepted($"/api/v1/tenant/live-classes/sessions/{sessionId:D}/recording", ToRecordingResponse(await db.SessionRecordings.AsNoTracking().SingleAsync(item => item.SessionId == sessionId, cancellationToken)));
    }

    /// <summary>What the organization's live classes run on, so the schedule form knows whether to ask for a meeting link.</summary>
    private static async Task<IResult> GetProviderAsync(LmsDbContext db, LiveClassProviderResolver resolver, CancellationToken cancellationToken)
    {
        var (provider, _) = await resolver.ResolveAsync(db, cancellationToken);
        var name = provider.Name switch { "manual" => LiveClassProviders.Manual, "jitsi" => LiveClassProviders.Jitsi, "livekit" => LiveClassProviders.LiveKit, _ => LiveClassProviders.Local };
        return Results.Ok(new LiveProviderResponse(name, RequiresMeetingLink: name == LiveClassProviders.Manual, CanRecord: provider.CanRecord));
    }

    /// <summary>A recording made in another tool (Zoom, Meet, Teams, Jitsi...) is attached by its link. The host must have agreed to recording, as for any recording.</summary>
    private static async Task<IResult> LinkRecordingAsync(HttpContext httpContext, LmsDbContext db, IConfiguration configuration, Guid sessionId, LinkRecordingRequest request, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var session = await db.LiveClassSessions.SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        if (session is null) return Results.NotFound();
        if (!await CanAccessSessionAsync(httpContext, db, session, userId, cancellationToken)) return Results.Forbid();
        var url = LiveClassUrls.NormalizeHttps(request.Url, configuration.GetValue("Integrations:AllowInsecureLiveClassHosts", false));
        if (url is null) return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Url)] = ["Paste the recording's link (an https address)."] });
        var hostConsent = await db.SessionConsents.AsNoTracking().AnyAsync(item => item.SessionId == sessionId && item.UserId == session.HostUserId && item.ConsentType == "recording" && item.Granted, cancellationToken);
        if (!hostConsent) return Results.Conflict(new { message = "The session host must grant recording consent before a recording can be attached." });
        var now = DateTimeOffset.UtcNow;
        var recording = await db.SessionRecordings.SingleOrDefaultAsync(item => item.SessionId == sessionId, cancellationToken);
        if (recording is null)
        {
            recording = new SessionRecording { Id = Guid.NewGuid(), TenantId = session.TenantId, SessionId = sessionId, RequestedAtUtc = now };
            db.SessionRecordings.Add(recording);
        }
        recording.Provider = string.IsNullOrWhiteSpace(session.Provider) ? "external" : session.Provider;
        recording.ProviderRecordingId = "external-link";
        recording.RecordingUrl = url;
        recording.Status = RecordingStatus.Available;
        recording.AvailableAtUtc = now;
        recording.RetainUntilUtc = now.AddDays(Math.Max(1, configuration.GetValue("LiveClasses:RecordingRetentionDays", 365)));
        recording.LastError = null;
        recording.NextAttemptAtUtc = null;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToRecordingResponse(recording));
    }

    private static async Task<IResult> RetryRecordingAsync(HttpContext httpContext, LmsDbContext db, Guid sessionId, CancellationToken cancellationToken)
    {
        if (!await CanAccessBySessionIdAsync(httpContext, db, sessionId, cancellationToken)) return Results.Forbid();
        var recording = await db.SessionRecordings.SingleOrDefaultAsync(item => item.SessionId == sessionId, cancellationToken);
        if (recording is null) return Results.NotFound();
        if (recording.Status != RecordingStatus.Failed) return Results.Conflict(new { message = "Only failed recordings can be retried." });
        if (recording.Provider == "livekit") return Results.Conflict(new { message = "Start the recording again while the class is on." });
        recording.Status = RecordingStatus.Requested;
        recording.AttemptCount = 0;
        recording.LastError = null;
        recording.NextAttemptAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Accepted($"/api/v1/tenant/live-classes/sessions/{sessionId:D}/recording", ToRecordingResponse(recording));
    }

    private static async Task StreamEventsAsync(HttpContext httpContext, LmsDbContext db, Guid sessionId, CancellationToken cancellationToken)
    {
        if (!await CanAccessBySessionIdAsync(httpContext, db, sessionId, cancellationToken))
        {
            httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        httpContext.Response.Headers.CacheControl = "no-cache, no-store";
        httpContext.Response.Headers["X-Accel-Buffering"] = "no";
        httpContext.Response.ContentType = "text/event-stream";
        await httpContext.Response.StartAsync(cancellationToken);
        var lastActivity = DateTimeOffset.UtcNow;
        while (!cancellationToken.IsCancellationRequested)
        {
            var latestChat = await db.SessionChatMessages.AsNoTracking().Where(item => item.SessionId == sessionId).OrderByDescending(item => item.CreatedAtUtc).Select(item => (DateTimeOffset?)item.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken);
            var latestAnnouncement = await db.SessionAnnouncements.AsNoTracking().Where(item => item.SessionId == sessionId).OrderByDescending(item => item.CreatedAtUtc).Select(item => (DateTimeOffset?)item.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken);
            var latestPoll = await db.LivePolls.AsNoTracking().Where(item => item.SessionId == sessionId).OrderByDescending(item => item.CreatedAtUtc).Select(item => (DateTimeOffset?)item.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken);
            // A hand going up or down counts as activity, so every screen in the class updates.
            var hands = await db.SessionHandRaises.AsNoTracking().Where(item => item.SessionId == sessionId).Select(item => new { item.RaisedAtUtc, item.LoweredAtUtc }).ToListAsync(cancellationToken);
            DateTimeOffset? latestHand = hands.Count == 0 ? null : hands.Max(item => item.LoweredAtUtc > item.RaisedAtUtc ? item.LoweredAtUtc.Value : item.RaisedAtUtc);
            var latestRequest = await db.SessionJoinRequests.AsNoTracking().Where(item => item.SessionId == sessionId).Select(item => (DateTimeOffset?)(item.DecidedAtUtc ?? item.RequestedAtUtc)).OrderByDescending(item => item).FirstOrDefaultAsync(cancellationToken);
            var latest = new[] { latestChat, latestAnnouncement, latestPoll, latestHand, latestRequest }.Where(item => item.HasValue).Select(item => item!.Value).DefaultIfEmpty(lastActivity).Max();
            var eventName = latest > lastActivity ? "refresh" : "heartbeat";
            lastActivity = latest > lastActivity ? latest : lastActivity;
            await httpContext.Response.WriteAsync($"event: {eventName}\ndata: {DateTimeOffset.UtcNow:O}\n\n", cancellationToken);
            await httpContext.Response.Body.FlushAsync(cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private static async Task<IResult> ListHandRaisesAsync(HttpContext httpContext, LmsDbContext db, Guid sessionId, CancellationToken cancellationToken)
    {
        if (!await CanAccessBySessionIdAsync(httpContext, db, sessionId, cancellationToken)) return Results.Forbid();
        var items = await db.SessionHandRaises.AsNoTracking().Where(item => item.SessionId == sessionId && item.Status == HandRaiseStatus.Raised).OrderBy(item => item.RaisedAtUtc).ToListAsync(cancellationToken); var ids = items.Select(item => item.UserId).ToArray(); var users = await db.Users.AsNoTracking().Where(item => ids.Contains(item.Id)).ToDictionaryAsync(item => item.Id, item => item.DisplayName, cancellationToken); return Results.Ok(items.Select(item => new HandRaiseResponse(item.Id, item.UserId, users.TryGetValue(item.UserId, out var name) ? name : "User", item.Status.ToString(), item.RaisedAtUtc, item.LoweredAtUtc)).ToArray());
    }

    /// <summary>The host or staff put a learner's hand down (after answering them).</summary>
    private static async Task<IResult> LowerHandRaiseAsync(HttpContext httpContext, LmsDbContext db, Guid sessionId, Guid userId, CancellationToken cancellationToken)
    {
        if (!await CanAccessBySessionIdAsync(httpContext, db, sessionId, cancellationToken)) return Results.Forbid();
        var item = await db.SessionHandRaises.SingleOrDefaultAsync(value => value.SessionId == sessionId && value.UserId == userId, cancellationToken);
        if (item is null || item.Status != HandRaiseStatus.Raised) return Results.NotFound();
        item.Status = HandRaiseStatus.Lowered;
        item.LoweredAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> SetHandRaiseAsync(HttpContext httpContext, LmsDbContext db, ITenantContext tenantContext, Guid sessionId, SetHandRaiseRequest request, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId || tenantContext.TenantId is not Guid tenantId) return Results.Unauthorized();
        if (!await CanAccessBySessionIdAsync(httpContext, db, sessionId, cancellationToken)) return Results.Forbid();
        var item = await db.SessionHandRaises.SingleOrDefaultAsync(value => value.SessionId == sessionId && value.UserId == userId, cancellationToken); var now = DateTimeOffset.UtcNow;
        if (item is null) { item = new SessionHandRaise { Id = Guid.NewGuid(), TenantId = tenantId, SessionId = sessionId, UserId = userId }; db.SessionHandRaises.Add(item); }
        item.Status = request.Raised ? HandRaiseStatus.Raised : HandRaiseStatus.Lowered; if (request.Raised) item.RaisedAtUtc = now; else item.LoweredAtUtc = now; await db.SaveChangesAsync(cancellationToken); return Results.Ok(new HandRaiseResponse(item.Id, item.UserId, "", item.Status.ToString(), item.RaisedAtUtc, item.LoweredAtUtc));
    }

    private static async Task<IResult> ListPollsAsync(HttpContext httpContext, LmsDbContext db, Guid sessionId, CancellationToken cancellationToken)
    {
        if (!await CanAccessBySessionIdAsync(httpContext, db, sessionId, cancellationToken)) return Results.Forbid();
        var polls = await db.LivePolls.AsNoTracking().Where(item => item.SessionId == sessionId).OrderByDescending(item => item.CreatedAtUtc).ToListAsync(cancellationToken); return Results.Ok(await BuildPollResponsesAsync(db, polls, cancellationToken));
    }

    private static async Task<IResult> CreatePollAsync(HttpContext httpContext, LmsDbContext db, ITenantContext tenantContext, Guid sessionId, CreatePollRequest request, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId || tenantContext.TenantId is not Guid tenantId) return Results.Unauthorized(); var options = (request.Options ?? []).Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (string.IsNullOrWhiteSpace(request.Question) || options.Length < 2 || options.Length > 10) return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Question)] = ["Question is required."], [nameof(request.Options)] = ["Provide between 2 and 10 options."] });
        if (!await CanAccessBySessionIdAsync(httpContext, db, sessionId, cancellationToken)) return Results.Forbid(); var poll = new LivePoll { Id = Guid.NewGuid(), TenantId = tenantId, SessionId = sessionId, CreatedByUserId = userId, Question = request.Question.Trim(), OptionsJson = JsonSerializer.Serialize(options), IsOpen = true, CreatedAtUtc = DateTimeOffset.UtcNow }; db.LivePolls.Add(poll); await db.SaveChangesAsync(cancellationToken); return Results.Created($"/api/v1/tenant/live-classes/sessions/{sessionId:D}/polls/{poll.Id:D}", await BuildPollResponseAsync(db, poll, cancellationToken));
    }

    private static async Task<IResult> VotePollAsync(HttpContext httpContext, LmsDbContext db, Guid sessionId, Guid pollId, VotePollRequest request, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId || !await CanAccessBySessionIdAsync(httpContext, db, sessionId, cancellationToken)) return Results.Forbid(); var poll = await db.LivePolls.SingleOrDefaultAsync(item => item.Id == pollId && item.SessionId == sessionId, cancellationToken); if (poll is null) return Results.NotFound(); if (!poll.IsOpen) return Results.Conflict(new { message = "This poll is closed." }); var options = JsonSerializer.Deserialize<string[]>(poll.OptionsJson) ?? []; if (request.OptionIndex < 0 || request.OptionIndex >= options.Length) return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.OptionIndex)] = ["Choose a valid poll option."] }); var response = await db.LivePollResponses.SingleOrDefaultAsync(item => item.PollId == pollId && item.UserId == userId, cancellationToken); if (response is null) { response = new LivePollResponse { Id = Guid.NewGuid(), TenantId = poll.TenantId, PollId = pollId, SessionId = sessionId, UserId = userId }; db.LivePollResponses.Add(response); } response.OptionIndex = request.OptionIndex; response.CreatedAtUtc = DateTimeOffset.UtcNow; await db.SaveChangesAsync(cancellationToken); return Results.Ok(await BuildPollResponseAsync(db, poll, cancellationToken));
    }

    private static async Task<IResult> ClosePollAsync(HttpContext httpContext, LmsDbContext db, Guid sessionId, Guid pollId, CancellationToken cancellationToken)
    { if (!await CanAccessBySessionIdAsync(httpContext, db, sessionId, cancellationToken)) return Results.Forbid(); var poll = await db.LivePolls.SingleOrDefaultAsync(item => item.Id == pollId && item.SessionId == sessionId, cancellationToken); if (poll is null) return Results.NotFound(); poll.IsOpen = false; poll.ClosedAtUtc = DateTimeOffset.UtcNow; await db.SaveChangesAsync(cancellationToken); return Results.Ok(await BuildPollResponseAsync(db, poll, cancellationToken)); }

    private static async Task<bool> CanAccessBySessionIdAsync(HttpContext httpContext, LmsDbContext db, Guid sessionId, CancellationToken cancellationToken)
    { if (GetUserId(httpContext) is not Guid userId) return false; var session = await db.LiveClassSessions.SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken); return session is not null && await CanAccessSessionAsync(httpContext, db, session, userId, cancellationToken); }

    private static async Task<bool> CanAccessSessionAsync(HttpContext httpContext, LmsDbContext db, LiveClassSession session, Guid userId, CancellationToken cancellationToken)
    { if (HasPermission(httpContext, LmsPermissions.LiveClassManage) || session.HostUserId == userId) return true; return session.CourseId is Guid courseId && await db.Enrollments.AnyAsync(item => item.CourseId == courseId && item.LearnerUserId == userId && (item.Status == Lms.Api.Domain.Learning.EnrollmentStatus.Active || item.Status == Lms.Api.Domain.Learning.EnrollmentStatus.Completed), cancellationToken); }

    private static LiveSessionResponse ToResponse(LiveClassSession item, string? courseTitle) => new(item.Id, item.CourseId, courseTitle, item.HostUserId, item.Title, item.Description, item.Provider, item.ProviderMeetingId, item.JoinUrl, item.HostUrl, item.StartAtUtc, item.EndAtUtc, item.Status.ToString(), item.RequireApproval, item.AutoRecord);
    private static RecordingResponse ToRecordingResponse(SessionRecording item) => new(item.Id, item.SessionId, item.Provider, item.ProviderRecordingId, item.RecordingUrl, item.Status.ToString(), item.AttemptCount, item.MaxAttempts, item.LastError, item.RequestedAtUtc, item.AvailableAtUtc, item.RetainUntilUtc, item.VideoId);
    private static async Task<AnnouncementResponse[]> WithAuthorNamesAsync(LmsDbContext db, List<SessionAnnouncement> items, CancellationToken cancellationToken) { var ids = items.Select(item => item.AuthorUserId).ToArray(); var users = await db.Users.AsNoTracking().Where(item => ids.Contains(item.Id)).ToDictionaryAsync(item => item.Id, item => item.DisplayName, cancellationToken); return items.Select(item => new AnnouncementResponse(item.Id, item.AuthorUserId, users.TryGetValue(item.AuthorUserId, out var name) ? name : "User", item.Body, item.IsPinned, item.CreatedAtUtc)).ToArray(); }
    private static async Task<ChatMessageResponse[]> WithChatNamesAsync(LmsDbContext db, List<SessionChatMessage> items, CancellationToken cancellationToken) { var ids = items.Select(item => item.UserId).ToArray(); var users = await db.Users.AsNoTracking().Where(item => ids.Contains(item.Id)).ToDictionaryAsync(item => item.Id, item => item.DisplayName, cancellationToken); return items.Select(item => new ChatMessageResponse(item.Id, item.UserId, users.TryGetValue(item.UserId, out var name) ? name : "User", item.Message, item.CreatedAtUtc)).ToArray(); }
    private static async Task<PollResponse[]> BuildPollResponsesAsync(LmsDbContext db, List<LivePoll> polls, CancellationToken cancellationToken) { var result = new List<PollResponse>(); foreach (var poll in polls) result.Add(await BuildPollResponseAsync(db, poll, cancellationToken)); return result.ToArray(); }
    private static async Task<PollResponse> BuildPollResponseAsync(LmsDbContext db, LivePoll poll, CancellationToken cancellationToken) { var options = JsonSerializer.Deserialize<string[]>(poll.OptionsJson) ?? []; var votes = await db.LivePollResponses.AsNoTracking().Where(item => item.PollId == poll.Id).GroupBy(item => item.OptionIndex).Select(group => new { Index = group.Key, Count = group.Count() }).ToDictionaryAsync(group => group.Index, group => group.Count, cancellationToken); /* project before materializing: a bare GroupBy cannot be translated to SQL */ return new PollResponse(poll.Id, poll.SessionId, poll.Question, options, poll.IsOpen, poll.CreatedAtUtc, poll.ClosedAtUtc, options.Select((option, index) => new PollOptionResponse(index, option, votes.TryGetValue(index, out var count) ? count : 0)).ToArray()); }
    private static Guid? GetUserId(HttpContext context) => Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub"), out var id) ? id : null;
    private static bool HasPermission(HttpContext context, string permission) => context.User.Claims.Any(item => item.Type == "permission" && item.Value.Equals(permission, StringComparison.OrdinalIgnoreCase));
}

public sealed record CreateLiveSessionRequest(Guid? CourseId, string Title, string? Description, DateTimeOffset StartAtUtc, DateTimeOffset EndAtUtc, string? MeetingUrl = null, bool RequireApproval = false, bool AutoRecord = false);
public sealed record TrackRecordingResponse(Guid Id, Guid UserId, string DisplayName, string Status, DateTimeOffset StartedAtUtc, DateTimeOffset? FinishedAtUtc, int? DurationSeconds, long SizeBytes, string? LastError, string? DownloadUrl);
public sealed record LinkRecordingRequest(string? Url);
public sealed record LiveProviderResponse(string Provider, bool RequiresMeetingLink, bool CanRecord);
public sealed record CreateAnnouncementRequest(string Body, bool IsPinned = false);
public sealed record CreateChatRequest(string Message);
public sealed record SetHandRaiseRequest(bool Raised);
public sealed record CreatePollRequest(string Question, string[]? Options);
public sealed record VotePollRequest(int OptionIndex);
public sealed record RecordingConsentRequest(bool Granted);
public sealed record LiveSessionResponse(Guid Id, Guid? CourseId, string? CourseTitle, Guid HostUserId, string Title, string? Description, string Provider, string ProviderMeetingId, string JoinUrl, string HostUrl, DateTimeOffset StartAtUtc, DateTimeOffset EndAtUtc, string Status, bool RequireApproval = false, bool AutoRecord = false);
public sealed record JoinRequestResponse(Guid UserId, string UserName, string Status, DateTimeOffset RequestedAtUtc);
public sealed record AttendanceResponse(Guid Id, Guid UserId, string UserName, string Status, DateTimeOffset JoinedAtUtc, DateTimeOffset? LeftAtUtc, int DurationSeconds);
public sealed record AnnouncementResponse(Guid Id, Guid AuthorUserId, string AuthorName, string Body, bool IsPinned, DateTimeOffset CreatedAtUtc);
public sealed record ChatMessageResponse(Guid Id, Guid UserId, string UserName, string Message, DateTimeOffset CreatedAtUtc);
public sealed record HandRaiseResponse(Guid Id, Guid UserId, string UserName, string Status, DateTimeOffset RaisedAtUtc, DateTimeOffset? LoweredAtUtc);
public sealed record PollResponse(Guid Id, Guid SessionId, string Question, string[] Options, bool IsOpen, DateTimeOffset CreatedAtUtc, DateTimeOffset? ClosedAtUtc, PollOptionResponse[] Results);
public sealed record PollOptionResponse(int Index, string Option, int Votes);
public sealed record RecordingConsentResponse(Guid SessionId, Guid UserId, string ConsentType, bool Granted, DateTimeOffset RecordedAtUtc);
public sealed record RecordingResponse(Guid Id, Guid SessionId, string Provider, string ProviderRecordingId, string? RecordingUrl, string Status, int AttemptCount, int MaxAttempts, string? LastError, DateTimeOffset RequestedAtUtc, DateTimeOffset? AvailableAtUtc, DateTimeOffset? RetainUntilUtc, Guid? VideoId = null);
