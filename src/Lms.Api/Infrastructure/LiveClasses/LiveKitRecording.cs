using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Lms.Api.Domain.Courses;
using Lms.Api.Domain.LiveClasses;
using Lms.Api.Domain.Videos;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Security;
using Lms.Api.Infrastructure.Storage;
using Lms.Api.Infrastructure.Tenancy;
using Lms.Api.Infrastructure.Videos;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Infrastructure.LiveClasses;

/// <summary>Raised when LiveKit's recording service refuses a request; the message is safe to show to staff.</summary>
public sealed class LiveKitEgressException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record EgressFile(string? Filename, string? Location, long? SizeBytes, long? DurationNanoseconds);
public sealed record EgressInfo(string EgressId, string Status, string? Error, IReadOnlyList<EgressFile> Files);

/// <summary>Where the recording service puts its file: a folder it can write to (shared with this server), or an S3-compatible bucket.</summary>
public sealed record EgressDestination(string FilePath, EgressS3? S3);
public sealed record EgressS3(string AccessKey, string Secret, string Region, string? Endpoint, string Bucket, bool ForcePathStyle);

/// <summary>The part of LiveKit's recording service (Egress) this system uses.</summary>
public interface ILiveKitEgressClient
{
    /// <summary>Starts recording the whole room (everyone's video and sound) to one MP4 file. Returns the recording's id.</summary>
    Task<string> StartRoomRecordingAsync(LiveKitCredentials credentials, string room, EgressDestination destination, CancellationToken cancellationToken);
    /// <summary>Starts recording one person (their camera and voice, apart from everyone else) to its own MP4 file. Returns the recording's id.</summary>
    Task<string> StartParticipantRecordingAsync(LiveKitCredentials credentials, string room, string identity, EgressDestination destination, CancellationToken cancellationToken);
    Task StopAsync(LiveKitCredentials credentials, string egressId, CancellationToken cancellationToken);
    /// <summary>Null when LiveKit does not know that recording.</summary>
    Task<EgressInfo?> GetAsync(LiveKitCredentials credentials, string egressId, CancellationToken cancellationToken);
}

/// <summary>Talks to the recording service over LiveKit's HTTP (Twirp) API, signed with the organization's key.</summary>
public sealed class LiveKitEgressClient(IHttpClientFactory httpFactory) : ILiveKitEgressClient
{
    private static readonly string[] Statuses = ["EGRESS_STARTING", "EGRESS_ACTIVE", "EGRESS_ENDING", "EGRESS_COMPLETE", "EGRESS_FAILED", "EGRESS_ABORTED", "EGRESS_LIMIT_REACHED"];

    private static Dictionary<string, object?> FileOutput(EgressDestination destination)
    {
        var output = new Dictionary<string, object?> { ["file_type"] = "MP4", ["filepath"] = destination.FilePath };
        if (destination.S3 is { } s3)
            output["s3"] = new Dictionary<string, object?> { ["access_key"] = s3.AccessKey, ["secret"] = s3.Secret, ["region"] = s3.Region, ["endpoint"] = s3.Endpoint, ["bucket"] = s3.Bucket, ["force_path_style"] = s3.ForcePathStyle };
        return output;
    }

    public async Task<string> StartRoomRecordingAsync(LiveKitCredentials credentials, string room, EgressDestination destination, CancellationToken cancellationToken)
    {
        var body = await CallAsync(credentials, "StartRoomCompositeEgress", new { room_name = room, file_outputs = new[] { FileOutput(destination) } }, cancellationToken);
        return body.TryGetProperty("egress_id", out var id) && id.GetString() is { Length: > 0 } value ? value : throw new LiveKitEgressException("LiveKit did not start the recording.");
    }

    public async Task<string> StartParticipantRecordingAsync(LiveKitCredentials credentials, string room, string identity, EgressDestination destination, CancellationToken cancellationToken)
    {
        var body = await CallAsync(credentials, "StartParticipantEgress", new { room_name = room, identity, screen_share = false, file_outputs = new[] { FileOutput(destination) } }, cancellationToken);
        return body.TryGetProperty("egress_id", out var id) && id.GetString() is { Length: > 0 } value ? value : throw new LiveKitEgressException("LiveKit did not start the recording.");
    }

    public async Task StopAsync(LiveKitCredentials credentials, string egressId, CancellationToken cancellationToken)
        => await CallAsync(credentials, "StopEgress", new { egress_id = egressId }, cancellationToken);

    public async Task<EgressInfo?> GetAsync(LiveKitCredentials credentials, string egressId, CancellationToken cancellationToken)
    {
        var body = await CallAsync(credentials, "ListEgress", new { egress_id = egressId }, cancellationToken);
        if (!body.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0) return null;
        return ReadInfo(items[0]);
    }

    public static EgressInfo ReadInfo(JsonElement item)
    {
        var status = item.TryGetProperty("status", out var s)
            ? s.ValueKind == JsonValueKind.Number && s.GetInt32() is >= 0 and var number && number < Statuses.Length ? Statuses[number] : s.GetString() ?? string.Empty
            : "EGRESS_STARTING";
        var files = new List<EgressFile>();
        if (item.TryGetProperty("file_results", out var results) && results.ValueKind == JsonValueKind.Array)
            foreach (var file in results.EnumerateArray()) files.Add(new EgressFile(Text(file, "filename"), Text(file, "location"), Number(file, "size"), Number(file, "duration")));
        return new EgressInfo(Text(item, "egress_id") ?? string.Empty, status, Text(item, "error") is { Length: > 0 } error ? error : null, files);
    }

    private static string? Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>64-bit numbers arrive as text in this API ("123"), but accept plain numbers too.</summary>
    private static long? Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }

    private async Task<JsonElement> CallAsync(LiveKitCredentials credentials, string method, object request, CancellationToken cancellationToken)
    {
        try { return await LiveKitApi.CallAsync(httpFactory, credentials, "livekit.Egress", method, request, LiveKitTokens.CreateRecordingToken(credentials, TimeSpan.FromMinutes(5)), cancellationToken); }
        catch (LiveKitApiException exception) { throw new LiveKitEgressException(exception.Message, exception.InnerException); }
    }
}

public sealed record StartRecordingResult(bool Started, int StatusCode, string? Message);

/// <summary>
/// Records LiveKit classes: starts and stops the recording service for a class, and once the file is ready brings it into the video library
/// (where it is converted for streaming and can be transcribed like any video).
/// Settings: <c>LiveKit:Egress:Enabled</c>, <c>LiveKit:Egress:Destination</c> = Local (a folder shared with the recording service: <c>ContainerPath</c> as it sees it,
/// <c>LocalDirectory</c> as this server sees it) or S3 (the storage bucket from <c>Storage:S3</c>, with <c>LiveKit:Egress:S3ServiceUrl</c> if the recording service reaches it by another address).
/// </summary>
public sealed class LiveKitRecordings(LmsDbContext db, ITenantContext tenantContext, LiveKitCredentialStore credentialStore, ILiveKitEgressClient egress, IConfiguration configuration,
    IManagedSecretStore secrets, IContentAssetStorage storage, IVideoTranscoder transcoder, ILiveKitRoomClient rooms, ILogger<LiveKitRecordings> logger)
{
    public bool Enabled => configuration.GetValue("LiveKit:Egress:Enabled", false);
    private string DestinationKind => configuration["LiveKit:Egress:Destination"]?.Trim() is { Length: > 0 } kind ? kind : "Local";

    public async Task<StartRecordingResult> StartAsync(LiveClassSession session, CancellationToken cancellationToken)
    {
        if (!session.Provider.Equals("livekit", StringComparison.OrdinalIgnoreCase)) return new(false, 409, "Only classes held in LiveKit can be recorded here.");
        if (session.Status is LiveSessionStatus.Completed or LiveSessionStatus.Cancelled) return new(false, 409, "This class has ended.");
        if (!Enabled) return new(false, 409, "Recording LiveKit classes is not set up on this server. Add the recording link instead, or ask your administrator.");
        if (session.CourseId is null) return new(false, 409, "Link this class to a course so its recording can be saved in the video library.");
        var settings = await db.LiveClassSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (credentialStore.Resolve(settings) is not { } credentials) return new(false, 409, "The LiveKit settings are incomplete.");
        var destination = BuildDestination(session);
        if (destination.Error is not null) return new(false, 409, destination.Error);

        var recording = await db.SessionRecordings.SingleOrDefaultAsync(item => item.SessionId == session.Id, cancellationToken);
        if (recording is { Status: RecordingStatus.Recording or RecordingStatus.Processing }) return new(false, 409, "This class is already being recorded.");
        if (recording is not null && recording.Status != RecordingStatus.Failed) return new(false, 409, "A recording already exists for this class.");

        string egressId;
        try { egressId = await egress.StartRoomRecordingAsync(credentials, session.ProviderMeetingId, destination.Value!, cancellationToken); }
        catch (LiveKitEgressException exception) { return new(false, exception.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ? 409 : 502, exception.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ? "Nobody is in the class room yet. Join the class, then start recording." : exception.Message); }

        var now = DateTimeOffset.UtcNow;
        if (recording is null)
        {
            recording = new SessionRecording { Id = Guid.NewGuid(), TenantId = session.TenantId, SessionId = session.Id };
            db.SessionRecordings.Add(recording);
        }
        recording.Provider = "livekit";
        recording.ProviderRecordingId = egressId;
        recording.OutputKey = destination.Key;
        recording.Status = RecordingStatus.Recording;
        recording.MaxAttempts = 0;                       // the built-in recording worker must leave these alone
        recording.AttemptCount = 0;
        recording.LastError = null;
        recording.RecordingUrl = null;
        recording.VideoId = null;
        recording.RequestedAtUtc = now;
        recording.NextAttemptAtUtc = null;
        await db.SaveChangesAsync(cancellationToken);
        await StartTracksForRoomAsync(session, cancellationToken);   // when each person is also recorded on their own, those already in the room start now
        return new(true, 202, null);
    }

    /// <summary>Stops a recording in progress. The file is brought in by <see cref="SyncAsync"/> once LiveKit has finished it.</summary>
    public async Task<StartRecordingResult> StopAsync(LiveClassSession session, CancellationToken cancellationToken)
    {
        var recording = await db.SessionRecordings.SingleOrDefaultAsync(item => item.SessionId == session.Id, cancellationToken);
        if (recording is not { Status: RecordingStatus.Recording }) return new(false, 409, "This class is not being recorded.");
        var settings = await db.LiveClassSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (credentialStore.Resolve(settings) is not { } credentials) return new(false, 409, "The LiveKit settings are incomplete.");
        try { await egress.StopAsync(credentials, recording.ProviderRecordingId, cancellationToken); }
        catch (LiveKitEgressException exception) when (exception.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) || exception.Message.Contains("already", StringComparison.OrdinalIgnoreCase)) { /* it had already ended; the next check picks up its result */ }
        catch (LiveKitEgressException exception) { return new(false, 502, exception.Message); }
        recording.Status = RecordingStatus.Processing;
        await db.SaveChangesAsync(cancellationToken);
        await StopTracksAsync(session, cancellationToken);
        return new(true, 202, null);
    }

    /// <summary>Called when a class is closed: a recording still running is stopped so it does not go on for an empty room.</summary>
    public async Task StopIfRecordingAsync(LiveClassSession session, CancellationToken cancellationToken)
    {
        try
        {
            if (await db.SessionRecordings.AnyAsync(item => item.SessionId == session.Id && item.Status == RecordingStatus.Recording, cancellationToken)) await StopAsync(session, cancellationToken);
            else await StopTracksAsync(session, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { logger.LogWarning(exception, "Could not stop the recording of session {SessionId}.", session.Id); }
    }

    private (EgressDestination? Value, string? Key, string? Error) BuildDestination(LiveClassSession session, Guid? person = null)
    {
        var file = $"{session.Id:N}-{(person is Guid who ? who.ToString("N") + "-" : "")}{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.mp4";
        if (DestinationKind.Equals("Local", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(configuration["LiveKit:Egress:LocalDirectory"])) return (null, null, "Recording is not fully set up: LiveKit:Egress:LocalDirectory is missing.");
            var container = (configuration["LiveKit:Egress:ContainerPath"]?.Trim() is { Length: > 0 } path ? path : "/out").TrimEnd('/');
            return (new EgressDestination($"{container}/{file}", null), file, null);
        }
        if (!DestinationKind.Equals("S3", StringComparison.OrdinalIgnoreCase)) return (null, null, "LiveKit:Egress:Destination must be Local or S3.");
        if (!string.Equals(configuration["Storage:Provider"], "S3", StringComparison.OrdinalIgnoreCase)) return (null, null, "Recording to S3 needs Storage:Provider to be S3.");
        var options = S3StorageOptions.From(configuration);
        var access = !string.IsNullOrWhiteSpace(options.AccessKeyIdReference) ? secrets.Get(options.AccessKeyIdReference) : options.AccessKeyId;
        var secret = !string.IsNullOrWhiteSpace(options.SecretAccessKeyReference) ? secrets.Get(options.SecretAccessKeyReference) : options.SecretAccessKey;
        if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(secret)) return (null, null, "Recording to S3 needs the storage access key and secret.");
        var key = $"recordings/{session.TenantId:D}/{session.Id:D}/{file}";
        var endpoint = configuration["LiveKit:Egress:S3ServiceUrl"]?.Trim() is { Length: > 0 } custom ? custom : options.ServiceUrl;
        return (new EgressDestination(options.KeyPrefix + key, new EgressS3(access, secret, options.Region, endpoint, options.Bucket, options.ForcePathStyle)), key, null);
    }

    /// <summary>Looks at one recording in progress and moves it on: still recording, being finished, done (brought into the library) or failed.</summary>
    public async Task SyncAsync(Guid recordingId, CancellationToken cancellationToken)
    {
        var recording = await db.SessionRecordings.SingleOrDefaultAsync(item => item.Id == recordingId && (item.Status == RecordingStatus.Recording || item.Status == RecordingStatus.Processing) && item.Provider == "livekit", cancellationToken);
        if (recording is null) return;
        var session = await db.LiveClassSessions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == recording.SessionId, cancellationToken);
        var settings = await db.LiveClassSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (session is null || credentialStore.Resolve(settings) is not { } credentials) { await FailAsync(recording, "The class or the LiveKit settings are no longer available.", cancellationToken); return; }

        EgressInfo? info;
        try { info = await egress.GetAsync(credentials, recording.ProviderRecordingId, cancellationToken); }
        catch (LiveKitEgressException exception) { logger.LogWarning(exception, "Could not check recording {Id}.", recording.ProviderRecordingId); return; }   // try again on the next round
        if (info is null) { await FailAsync(recording, "LiveKit has no record of this recording.", cancellationToken); return; }

        switch (info.Status)
        {
            case "EGRESS_STARTING" or "EGRESS_ACTIVE":
                // A class that has been closed (or is long over) must not keep recording.
                if (session.Status == LiveSessionStatus.Completed || DateTimeOffset.UtcNow > session.EndAtUtc.AddHours(1))
                {
                    try { await egress.StopAsync(credentials, recording.ProviderRecordingId, cancellationToken); recording.Status = RecordingStatus.Processing; await db.SaveChangesAsync(cancellationToken); }
                    catch (LiveKitEgressException exception) { logger.LogWarning(exception, "Could not stop recording {Id}.", recording.ProviderRecordingId); }
                }
                break;
            case "EGRESS_ENDING":
                if (recording.Status != RecordingStatus.Processing) { recording.Status = RecordingStatus.Processing; await db.SaveChangesAsync(cancellationToken); }
                break;
            case "EGRESS_COMPLETE":
                await ImportAsync(recording, session, info, cancellationToken);
                break;
            default:   // failed, aborted, limit reached
                await FailAsync(recording, string.IsNullOrWhiteSpace(info.Error) ? "LiveKit could not finish the recording." : $"LiveKit could not finish the recording: {info.Error}", cancellationToken);
                break;
        }
    }

    private async Task ImportAsync(SessionRecording recording, LiveClassSession session, EgressInfo info, CancellationToken cancellationToken)
    {
        try
        {
            if (session.CourseId is not Guid courseId) throw new InvalidOperationException("The class is not linked to a course.");
            var file = info.Files.FirstOrDefault() ?? throw new InvalidOperationException("LiveKit reported no file.");
            var now = DateTimeOffset.UtcNow;
            var assetId = Guid.NewGuid();
            var (key, size) = await StoreAsync(recording.OutputKey, file, session.TenantId, courseId, assetId, cancellationToken);

            var asset = new ContentAsset { Id = assetId, TenantId = session.TenantId, CourseId = courseId, OriginalFileName = $"{session.Title}.mp4", StorageKey = key, ContentType = "video/mp4", SizeBytes = size, Sha256 = string.Empty, CreatedByUserId = session.HostUserId, CreatedAtUtc = now };
            var seconds = file.DurationNanoseconds is > 0 ? (int)Math.Min(24 * 3600, Math.Ceiling(file.DurationNanoseconds.Value / 1e9)) : (int?)null;
            var video = new Video
            {
                Id = Guid.NewGuid(), TenantId = session.TenantId, CourseId = courseId, Title = $"{session.Title} (recording {session.StartAtUtc:yyyy-MM-dd})", Description = $"Recording of the live class “{session.Title}”.",
                Type = VideoType.LiveRecording, Status = transcoder.Enabled ? VideoStatus.Processing : VideoStatus.Ready, ContentAssetId = assetId, ContentType = "video/mp4", SizeBytes = size,
                DurationSeconds = seconds, CreatedByUserId = session.HostUserId, CreatedAtUtc = now, UpdatedAtUtc = now
            };
            db.ContentAssets.Add(asset);
            db.Videos.Add(video);
            recording.VideoId = video.Id;
            recording.Status = RecordingStatus.Available;
            recording.AvailableAtUtc = now;
            recording.RetainUntilUtc = now.AddDays(Math.Max(1, configuration.GetValue("LiveClasses:RecordingRetentionDays", 365)));
            recording.LastError = null;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await FailAsync(recording, exception.Message, cancellationToken);
        }
    }

    /// <summary>Brings a finished recording file into this system's storage (or confirms it is already in the bucket) and returns where it is and how big.</summary>
    private async Task<(string Key, long Size)> StoreAsync(string? outputKey, EgressFile file, Guid tenantId, Guid courseId, Guid assetId, CancellationToken cancellationToken)
    {
        string key; long size;
        if (DestinationKind.Equals("S3", StringComparison.OrdinalIgnoreCase))
        {
            key = outputKey ?? throw new InvalidOperationException("The recording's location is not known.");
            if (!await storage.ExistsAsync(key, cancellationToken)) throw new InvalidOperationException("The recording file was not found in storage.");
            size = file.SizeBytes ?? 0;
        }
        else
        {
            var directory = configuration["LiveKit:Egress:LocalDirectory"] ?? throw new InvalidOperationException("LiveKit:Egress:LocalDirectory is missing.");
            var path = Path.Combine(directory, Path.GetFileName(outputKey ?? file.Filename ?? string.Empty));
            if (!File.Exists(path)) throw new InvalidOperationException("The recording file was not found where LiveKit was asked to put it.");
            key = $"{tenantId:D}/{courseId:D}/{assetId:D}.mp4";
            await using (var source = File.OpenRead(path))
            {
                size = source.Length;
                if (size <= 0) throw new InvalidOperationException("The recording file is empty.");
                await storage.PutAsync(key, source, "video/mp4", cancellationToken);
            }
            try { File.Delete(path); } catch (IOException) { /* a leftover in the shared folder is harmless */ }
        }
        if (size <= 0) throw new InvalidOperationException("The recording file is empty.");
        return (key, size);
    }

    private async Task FailAsync(SessionRecording recording, string message, CancellationToken cancellationToken)
    {
        recording.Status = RecordingStatus.Failed;
        recording.LastError = message.Length > 4000 ? message[..4000] : message;
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---- Each person on their own track ----

    /// <summary>Setting <c>LiveKit:Egress:SeparateTracks</c>: besides the whole-class file, every person is also recorded on their own.</summary>
    public bool SeparateTracks => Enabled && configuration.GetValue("LiveKit:Egress:SeparateTracks", false);

    /// <summary>Starts recording one person on their own, when the class is being recorded and this is switched on. Never throws: the class's own recording matters more.</summary>
    public async Task StartTrackAsync(LiveClassSession session, Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            if (!SeparateTracks || session.CourseId is null) return;
            if (!await db.SessionRecordings.AnyAsync(item => item.SessionId == session.Id && item.Status == RecordingStatus.Recording, cancellationToken)) return;
            if (await db.SessionTrackRecordings.AnyAsync(item => item.SessionId == session.Id && item.UserId == userId && (item.Status == RecordingStatus.Recording || item.Status == RecordingStatus.Processing), cancellationToken)) return;
            if (credentialStore.Resolve(await db.LiveClassSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken)) is not { } credentials) return;
            var destination = BuildDestination(session, userId);
            if (destination.Value is null) return;
            var id = await egress.StartParticipantRecordingAsync(credentials, session.ProviderMeetingId, userId.ToString("D"), destination.Value, cancellationToken);
            db.SessionTrackRecordings.Add(new SessionTrackRecording { Id = Guid.NewGuid(), TenantId = session.TenantId, SessionId = session.Id, UserId = userId, ProviderRecordingId = id, OutputKey = destination.Key, Status = RecordingStatus.Recording, StartedAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is LiveKitEgressException or DbUpdateException) { logger.LogWarning(exception, "Could not record {UserId} on their own in session {SessionId}.", userId, session.Id); }
    }

    /// <summary>Starts a track for everyone who is in the room already (the class recording has just begun).</summary>
    public async Task StartTracksForRoomAsync(LiveClassSession session, CancellationToken cancellationToken)
    {
        if (!SeparateTracks) return;
        try
        {
            if (credentialStore.Resolve(await db.LiveClassSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken)) is not { } credentials) return;
            foreach (var person in await rooms.ListParticipantsAsync(credentials, session.ProviderMeetingId, cancellationToken))
                if (Guid.TryParse(person.Identity, out var userId)) await StartTrackAsync(session, userId, cancellationToken);
        }
        catch (LiveKitApiException exception) { logger.LogWarning(exception, "Could not list who is in the room of session {SessionId}.", session.Id); }
    }

    /// <summary>Stops the tracks still being recorded; the files are brought in by <see cref="SyncTrackAsync"/> once LiveKit has finished them.</summary>
    public async Task StopTracksAsync(LiveClassSession session, CancellationToken cancellationToken)
    {
        var open = await db.SessionTrackRecordings.Where(item => item.SessionId == session.Id && item.Status == RecordingStatus.Recording).ToListAsync(cancellationToken);
        if (open.Count == 0) return;
        if (credentialStore.Resolve(await db.LiveClassSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken)) is not { } credentials) return;
        foreach (var track in open)
        {
            try { await egress.StopAsync(credentials, track.ProviderRecordingId, cancellationToken); }
            catch (LiveKitEgressException exception) { logger.LogWarning(exception, "Could not stop track {Id}.", track.ProviderRecordingId); }   // it may have ended with the person leaving
            track.Status = RecordingStatus.Processing;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SyncTrackAsync(Guid trackId, CancellationToken cancellationToken)
    {
        var track = await db.SessionTrackRecordings.SingleOrDefaultAsync(item => item.Id == trackId && (item.Status == RecordingStatus.Recording || item.Status == RecordingStatus.Processing), cancellationToken);
        if (track is null) return;
        var session = await db.LiveClassSessions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == track.SessionId, cancellationToken);
        var settings = await db.LiveClassSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (session?.CourseId is not Guid courseId || credentialStore.Resolve(settings) is not { } credentials) { await FailTrackAsync(track, "The class or the LiveKit settings are no longer available.", cancellationToken); return; }
        EgressInfo? info;
        try { info = await egress.GetAsync(credentials, track.ProviderRecordingId, cancellationToken); }
        catch (LiveKitEgressException exception) { logger.LogWarning(exception, "Could not check track {Id}.", track.ProviderRecordingId); return; }
        if (info is null) { await FailTrackAsync(track, "LiveKit has no record of this recording.", cancellationToken); return; }
        switch (info.Status)
        {
            case "EGRESS_STARTING" or "EGRESS_ACTIVE":
                if (session.Status == LiveSessionStatus.Completed || DateTimeOffset.UtcNow > session.EndAtUtc.AddHours(1))
                {
                    try { await egress.StopAsync(credentials, track.ProviderRecordingId, cancellationToken); track.Status = RecordingStatus.Processing; await db.SaveChangesAsync(cancellationToken); }
                    catch (LiveKitEgressException exception) { logger.LogWarning(exception, "Could not stop track {Id}.", track.ProviderRecordingId); }
                }
                break;
            case "EGRESS_ENDING":
                if (track.Status != RecordingStatus.Processing) { track.Status = RecordingStatus.Processing; await db.SaveChangesAsync(cancellationToken); }
                break;
            case "EGRESS_COMPLETE":
                try
                {
                    var file = info.Files.FirstOrDefault() ?? throw new InvalidOperationException("LiveKit reported no file.");
                    var assetId = Guid.NewGuid();
                    var (key, size) = await StoreAsync(track.OutputKey, file, session.TenantId, courseId, assetId, cancellationToken);
                    var name = await db.Users.AsNoTracking().Where(item => item.Id == track.UserId).Select(item => item.DisplayName).SingleOrDefaultAsync(cancellationToken) ?? "Participant";
                    var now = DateTimeOffset.UtcNow;
                    db.ContentAssets.Add(new ContentAsset { Id = assetId, TenantId = session.TenantId, CourseId = courseId, OriginalFileName = $"{session.Title} – {name}.mp4", StorageKey = key, ContentType = "video/mp4", SizeBytes = size, Sha256 = string.Empty, CreatedByUserId = session.HostUserId, CreatedAtUtc = now });
                    track.ContentAssetId = assetId; track.SizeBytes = size; track.FinishedAtUtc = now; track.LastError = null; track.Status = RecordingStatus.Available;
                    track.DurationSeconds = file.DurationNanoseconds is > 0 ? (int)Math.Min(24 * 3600, Math.Ceiling(file.DurationNanoseconds.Value / 1e9)) : null;
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException) { await FailTrackAsync(track, exception.Message, cancellationToken); }
                break;
            default:
                await FailTrackAsync(track, string.IsNullOrWhiteSpace(info.Error) ? "LiveKit could not finish the recording." : $"LiveKit could not finish the recording: {info.Error}", cancellationToken);
                break;
        }
    }

    private async Task FailTrackAsync(SessionTrackRecording track, string message, CancellationToken cancellationToken)
    {
        track.Status = RecordingStatus.Failed;
        track.LastError = message.Length > 4000 ? message[..4000] : message;
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Checks every recording in progress, across organizations.</summary>
public sealed class LiveKitRecordingSync(IServiceScopeFactory scopes, ILogger<LiveKitRecordingSync> logger)
{
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        List<(Guid Id, Guid TenantId, bool Track)> open;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LmsDbContext>();
            open = (await db.SessionRecordings.IgnoreQueryFilters().AsNoTracking()
                .Where(item => item.Provider == "livekit" && (item.Status == RecordingStatus.Recording || item.Status == RecordingStatus.Processing))
                .OrderBy(item => item.RequestedAtUtc).Take(20).Select(item => new { item.Id, item.TenantId }).ToListAsync(cancellationToken)).Select(item => (item.Id, item.TenantId, false)).ToList();
            open.AddRange((await db.SessionTrackRecordings.IgnoreQueryFilters().AsNoTracking()
                .Where(item => item.Status == RecordingStatus.Recording || item.Status == RecordingStatus.Processing)
                .OrderBy(item => item.StartedAtUtc).Take(40).Select(item => new { item.Id, item.TenantId }).ToListAsync(cancellationToken)).Select(item => (item.Id, item.TenantId, true)));
        }
        foreach (var (id, tenantId, track) in open)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<LmsDbContext>();
                var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(item => item.Id == tenantId, cancellationToken);
                if (tenant is null) continue;
                scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenant.Id, tenant.Slug);
                var recordings = scope.ServiceProvider.GetRequiredService<LiveKitRecordings>();
                if (track) await recordings.SyncTrackAsync(id, cancellationToken); else await recordings.SyncAsync(id, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException) { logger.LogWarning(exception, "Recording {Id} could not be checked.", id); }
        }
        return open.Count;
    }
}

public sealed class LiveKitRecordingWorker(LiveKitRecordingSync sync, IConfiguration configuration, ILogger<LiveKitRecordingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("LiveKit:Egress:Enabled", false) || !configuration.GetValue("Videos:Processing:WorkerEnabled", true)) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await sync.RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Recording check failed."); }
        }
    }
}
