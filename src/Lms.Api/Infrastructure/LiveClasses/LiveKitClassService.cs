using System.Text.Json;
using Lms.Api.Domain.LiveClasses;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Infrastructure.LiveClasses;

public enum WebhookOutcome
{
    /// <summary>The call was understood and acted on (or needs no action).</summary>
    Handled,
    /// <summary>Nothing here concerns this system (an unknown room, a kind of event it does not use).</summary>
    Ignored,
    /// <summary>The call is not signed by the organization's LiveKit server.</summary>
    Rejected
}

public sealed record MuteOutcome(bool Ok, int StatusCode, string? Message, int Muted = 0);

/// <summary>
/// What happens around a class held in LiveKit without anyone pressing a button: LiveKit tells this system who joined and left (the webhook),
/// the class goes live and closes with its schedule, recording starts and stops with it, and the host can switch other people's microphones off.
/// </summary>
public sealed class LiveKitClassService(LmsDbContext db, ITenantContext tenantContext, LiveKitCredentialStore credentialStore, LiveKitRecordings recordings, ILiveKitRoomClient rooms,
    IConfiguration configuration, ILogger<LiveKitClassService> logger)
{
    // ---------- the webhook ----------

    /// <summary>
    /// Acts on one call from LiveKit. The organization is found from the room named in the call and the call is checked against that organization's own secret,
    /// so one organization's server cannot speak for another's class.
    /// </summary>
    public async Task<WebhookOutcome> HandleWebhookAsync(string body, string? authorization, CancellationToken cancellationToken)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(body).RootElement; }
        catch (JsonException) { return WebhookOutcome.Ignored; }
        if (root.ValueKind != JsonValueKind.Object) return WebhookOutcome.Ignored;
        var kind = Text(root, "event");
        var room = Text(Child(root, "room"), "name");
        var egress = Child(root, "egressInfo").ValueKind == JsonValueKind.Object ? Child(root, "egressInfo") : Child(root, "egress_info");
        var egressId = Text(egress, "egressId") ?? Text(egress, "egress_id");
        if (string.IsNullOrEmpty(kind)) return WebhookOutcome.Ignored;

        var session = await FindSessionAsync(room, egressId, cancellationToken);
        if (session is null) return WebhookOutcome.Ignored;
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(item => item.Id == session.TenantId, cancellationToken);
        if (tenant is null) return WebhookOutcome.Ignored;
        tenantContext.Set(tenant.Id, tenant.Slug);
        session = await db.LiveClassSessions.SingleOrDefaultAsync(item => item.Id == session.Id, cancellationToken);
        if (session is null) return WebhookOutcome.Ignored;
        if (credentialStore.Resolve(await db.LiveClassSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken)) is not { } credentials
            || !LiveKitWebhookVerifier.IsValid(credentials, authorization, body)) return WebhookOutcome.Rejected;

        var identity = Text(Child(root, "participant"), "identity");
        var person = Guid.TryParse(identity, out var userId) ? userId : (Guid?)null;   // the recorder joins the room as a hidden guest; only our own people have an id here
        switch (kind)
        {
            case "room_started":
                await GoLiveAsync(session, cancellationToken);
                break;
            case "participant_joined" when person is Guid joined:
                await GoLiveAsync(session, cancellationToken);
                await MarkPresentAsync(session, joined, cancellationToken);
                await recordings.StartTrackAsync(session, joined, cancellationToken);
                await StartAutomaticRecordingAsync(session, cancellationToken);
                break;
            case "participant_left" when person is Guid left:
                await MarkLeftAsync(session, left, cancellationToken);
                break;
            case "room_finished":
                await recordings.StopIfRecordingAsync(session, cancellationToken);
                if (DateTimeOffset.UtcNow >= session.EndAtUtc && session.Status == LiveSessionStatus.Live) await CompleteAsync(session, closeRoom: false, cancellationToken);
                break;
            case "egress_ended" when !string.IsNullOrEmpty(egressId):
                var whole = await db.SessionRecordings.AsNoTracking().FirstOrDefaultAsync(item => item.ProviderRecordingId == egressId, cancellationToken);
                if (whole is not null) await recordings.SyncAsync(whole.Id, cancellationToken);
                else if (await db.SessionTrackRecordings.AsNoTracking().FirstOrDefaultAsync(item => item.ProviderRecordingId == egressId, cancellationToken) is { } track) await recordings.SyncTrackAsync(track.Id, cancellationToken);
                break;
            default:
                return WebhookOutcome.Ignored;
        }
        return WebhookOutcome.Handled;
    }

    private async Task<LiveClassSession?> FindSessionAsync(string? room, string? egressId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(room))
            return await db.LiveClassSessions.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(item => item.Provider == "livekit" && item.ProviderMeetingId == room, cancellationToken);
        if (string.IsNullOrEmpty(egressId)) return null;
        var sessionId = await db.SessionRecordings.IgnoreQueryFilters().AsNoTracking().Where(item => item.ProviderRecordingId == egressId).Select(item => (Guid?)item.SessionId).FirstOrDefaultAsync(cancellationToken)
            ?? await db.SessionTrackRecordings.IgnoreQueryFilters().AsNoTracking().Where(item => item.ProviderRecordingId == egressId).Select(item => (Guid?)item.SessionId).FirstOrDefaultAsync(cancellationToken);
        return sessionId is Guid id ? await db.LiveClassSessions.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, cancellationToken) : null;
    }

    private async Task GoLiveAsync(LiveClassSession session, CancellationToken cancellationToken)
    {
        if (session.Status != LiveSessionStatus.Scheduled) return;
        session.Status = LiveSessionStatus.Live;
        session.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The person is in the room: their attendance is recorded from LiveKit itself, so it is right even if their browser never reports in.</summary>
    private async Task MarkPresentAsync(LiveClassSession session, Guid userId, CancellationToken cancellationToken)
    {
        if (!await db.Users.AnyAsync(item => item.Id == userId, cancellationToken)) return;   // not a person of this organization
        var now = DateTimeOffset.UtcNow;
        var attendance = await db.SessionAttendances.SingleOrDefaultAsync(item => item.SessionId == session.Id && item.UserId == userId, cancellationToken);
        if (attendance is null) { attendance = new SessionAttendance { Id = Guid.NewGuid(), TenantId = session.TenantId, SessionId = session.Id, UserId = userId, JoinedAtUtc = now }; db.SessionAttendances.Add(attendance); }
        else if (attendance.Status == AttendanceStatus.Present) return;
        attendance.Status = AttendanceStatus.Present; attendance.LeftAtUtc = null; attendance.UpdatedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkLeftAsync(LiveClassSession session, Guid userId, CancellationToken cancellationToken)
    {
        var attendance = await db.SessionAttendances.SingleOrDefaultAsync(item => item.SessionId == session.Id && item.UserId == userId, cancellationToken);
        if (attendance is not { Status: AttendanceStatus.Present }) return;
        var now = DateTimeOffset.UtcNow;
        attendance.Status = AttendanceStatus.Left; attendance.LeftAtUtc = now; attendance.DurationSeconds = Math.Max(0, (int)(now - attendance.JoinedAtUtc).TotalSeconds); attendance.UpdatedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---------- the class's own schedule ----------

    private int GraceMinutes => Math.Clamp(configuration.GetValue("LiveKit:Automation:CloseGraceMinutes", 15), 0, 240);

    private async Task<bool> HostAgreedToRecordingAsync(LiveClassSession session, CancellationToken cancellationToken)
        => await db.SessionConsents.AsNoTracking().AnyAsync(item => item.SessionId == session.Id && item.UserId == session.HostUserId && item.ConsentType == "recording" && item.Granted, cancellationToken);

    /// <summary>Starts the recording of a class set to record itself, once there is someone in the room. Does nothing when it is not set up, already started or the host has not agreed.</summary>
    public async Task StartAutomaticRecordingAsync(LiveClassSession session, CancellationToken cancellationToken)
    {
        if (!session.AutoRecord || session.Status != LiveSessionStatus.Live || !recordings.Enabled) return;
        var now = DateTimeOffset.UtcNow;
        if (now < session.StartAtUtc.AddMinutes(-GraceMinutes) || now > session.EndAtUtc.AddMinutes(GraceMinutes)) return;
        if (await db.SessionRecordings.AnyAsync(item => item.SessionId == session.Id, cancellationToken)) return;   // started once already (and perhaps stopped on purpose)
        if (!await HostAgreedToRecordingAsync(session, cancellationToken)) return;
        var result = await recordings.StartAsync(session, cancellationToken);
        if (!result.Started) logger.LogInformation("Automatic recording of session {SessionId} did not start: {Message}", session.Id, result.Message);
    }

    /// <summary>One look at a class: it goes live when its time comes, starts recording if set to, and closes after its end (with a grace period for overruns).</summary>
    public async Task RunAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await db.LiveClassSessions.SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        if (session is null || !session.Provider.Equals("livekit", StringComparison.OrdinalIgnoreCase) || session.Status is LiveSessionStatus.Completed or LiveSessionStatus.Cancelled) return;
        var now = DateTimeOffset.UtcNow;
        if (now >= session.EndAtUtc.AddMinutes(GraceMinutes))
        {
            if (session.Status == LiveSessionStatus.Live || await db.SessionAttendances.AnyAsync(item => item.SessionId == session.Id, cancellationToken)) await CompleteAsync(session, closeRoom: true, cancellationToken);
            return;
        }
        if (now >= session.StartAtUtc) await GoLiveAsync(session, cancellationToken);
        await StartAutomaticRecordingAsync(session, cancellationToken);
    }

    /// <summary>Closes the class: it is marked completed, whoever is still marked present is marked as having left, the recording stops and the room is closed.</summary>
    public async Task CompleteAsync(LiveClassSession session, bool closeRoom, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        session.Status = LiveSessionStatus.Completed; session.UpdatedAtUtc = now;
        foreach (var attendance in await db.SessionAttendances.Where(item => item.SessionId == session.Id && item.Status == AttendanceStatus.Present).ToListAsync(cancellationToken))
        {
            attendance.Status = AttendanceStatus.Left; attendance.LeftAtUtc = now; attendance.DurationSeconds = Math.Max(0, (int)(now - attendance.JoinedAtUtc).TotalSeconds); attendance.UpdatedAtUtc = now;
        }
        await db.SaveChangesAsync(cancellationToken);
        await recordings.StopIfRecordingAsync(session, cancellationToken);
        if (!closeRoom) return;
        try
        {
            if (credentialStore.Resolve(await db.LiveClassSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken)) is { } credentials) await rooms.CloseAsync(credentials, session.ProviderMeetingId, cancellationToken);
        }
        catch (LiveKitApiException exception) { logger.LogWarning(exception, "Could not close the room of session {SessionId}.", session.Id); }
    }

    // ---------- the host's controls ----------

    /// <summary>Switches off the microphone of one person (or, with no person, of everyone but the host and the person asking). Anyone can switch theirs on again.</summary>
    public async Task<MuteOutcome> MuteAsync(LiveClassSession session, Guid? userId, Guid askedBy, CancellationToken cancellationToken)
    {
        if (!session.Provider.Equals("livekit", StringComparison.OrdinalIgnoreCase)) return new(false, 409, "Only classes held in LiveKit have this control.");
        if (session.Status is LiveSessionStatus.Completed or LiveSessionStatus.Cancelled) return new(false, 409, "This class has ended.");
        if (credentialStore.Resolve(await db.LiveClassSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken)) is not { } credentials) return new(false, 409, "The LiveKit settings are incomplete.");
        try
        {
            var muted = 0;
            if (userId is Guid one) muted = await rooms.MuteAsync(credentials, session.ProviderMeetingId, one.ToString("D"), cancellationToken);
            else
                foreach (var person in await rooms.ListParticipantsAsync(credentials, session.ProviderMeetingId, cancellationToken))
                {
                    if (!Guid.TryParse(person.Identity, out var other) || other == askedBy || other == session.HostUserId) continue;
                    if (person.Tracks.Any(track => track.IsAudio && !track.Muted)) { await rooms.MuteAsync(credentials, session.ProviderMeetingId, person.Identity, cancellationToken); muted++; }
                }
            return new(true, 200, null, muted);
        }
        catch (LiveKitApiException exception) { return new(false, 502, exception.Message); }
    }

    private static JsonElement Child(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;
    private static string? Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>Looks at the classes that are on or just over, across organizations.</summary>
public sealed class LiveKitClassAutomation(IServiceScopeFactory scopes, ILogger<LiveKitClassAutomation> logger)
{
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        List<(Guid Id, Guid TenantId)> open;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LmsDbContext>();
            var now = DateTimeOffset.UtcNow;
            var since = now.AddDays(-2);
            open = (await db.LiveClassSessions.IgnoreQueryFilters().AsNoTracking()
                .Where(item => item.Provider == "livekit" && (item.Status == LiveSessionStatus.Scheduled || item.Status == LiveSessionStatus.Live) && item.StartAtUtc <= now && item.EndAtUtc >= since)
                .OrderBy(item => item.EndAtUtc).Take(50).Select(item => new { item.Id, item.TenantId }).ToListAsync(cancellationToken)).Select(item => (item.Id, item.TenantId)).ToList();
        }
        foreach (var (id, tenantId) in open)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<LmsDbContext>();
                var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(item => item.Id == tenantId, cancellationToken);
                if (tenant is null) continue;
                scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenant.Id, tenant.Slug);
                await scope.ServiceProvider.GetRequiredService<LiveKitClassService>().RunAsync(id, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException) { logger.LogWarning(exception, "Class {Id} could not be checked.", id); }
        }
        return open.Count;
    }
}

public sealed class LiveKitClassAutomationWorker(LiveKitClassAutomation automation, IConfiguration configuration, ILogger<LiveKitClassAutomationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("LiveKit:Automation:WorkerEnabled", true)) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await automation.RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Class automation failed."); }
        }
    }
}
