using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lms.Api.Infrastructure.LiveClasses;
using Lms.Api.Infrastructure.Storage;
using Lms.Api.Infrastructure.Videos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

/// <summary>Stands in for LiveKit's recording service. It remembers what it was asked and lets a test decide how each recording goes.</summary>
public sealed class FakeEgressClient : ILiveKitEgressClient
{
    public sealed record Start(LiveKitCredentials Credentials, string Room, EgressDestination Destination, string Id);
    private readonly Dictionary<string, EgressInfo> infos = [];
    public List<Start> Starts { get; } = [];
    public List<string> Stops { get; } = [];
    public string? StartError { get; set; }
    public bool Unreachable { get; set; }
    /// <summary>Where a finished file is put: a folder shared with the API, or (for S3) a function that stores it under the given key.</summary>
    public string? LocalDirectory { get; set; }
    public Func<string, byte[], Task>? PutObject { get; set; }

    public Task<string> StartRoomRecordingAsync(LiveKitCredentials credentials, string room, EgressDestination destination, CancellationToken cancellationToken)
    {
        if (StartError is not null) throw new LiveKitEgressException(StartError);
        var id = $"EG_{Starts.Count + 1}";
        Starts.Add(new Start(credentials, room, destination, id));
        infos[id] = new EgressInfo(id, "EGRESS_ACTIVE", null, []);
        return Task.FromResult(id);
    }

    public sealed record TrackStart(string Room, string Identity, EgressDestination Destination, string Id);
    public List<TrackStart> TrackStarts { get; } = [];

    public Task<string> StartParticipantRecordingAsync(LiveKitCredentials credentials, string room, string identity, EgressDestination destination, CancellationToken cancellationToken)
    {
        if (StartError is not null) throw new LiveKitEgressException(StartError);
        var id = $"EGP_{TrackStarts.Count + 1}";
        TrackStarts.Add(new TrackStart(room, identity, destination, id));
        infos[id] = new EgressInfo(id, "EGRESS_ACTIVE", null, []);
        return Task.FromResult(id);
    }

    /// <summary>One person's recording finishes, like <see cref="CompleteAsync"/>.</summary>
    public async Task CompleteTrackAsync(string id, byte[] content, double seconds = 60)
    {
        var start = TrackStarts.Single(item => item.Id == id);
        var name = Path.GetFileName(start.Destination.FilePath);
        if (start.Destination.S3 is null) await File.WriteAllBytesAsync(Path.Combine(LocalDirectory!, name), content);
        else await PutObject!(start.Destination.FilePath, content);
        infos[id] = new EgressInfo(id, "EGRESS_COMPLETE", null, [new EgressFile(name, start.Destination.FilePath, content.Length, (long)(seconds * 1e9))]);
    }

    public Task StopAsync(LiveKitCredentials credentials, string egressId, CancellationToken cancellationToken)
    {
        Stops.Add(egressId);
        if (infos.TryGetValue(egressId, out var info) && info.Status == "EGRESS_ACTIVE") infos[egressId] = info with { Status = "EGRESS_ENDING" };
        return Task.CompletedTask;
    }

    public Task<EgressInfo?> GetAsync(LiveKitCredentials credentials, string egressId, CancellationToken cancellationToken)
    {
        if (Unreachable) throw new LiveKitEgressException("LiveKit could not be reached.");
        return Task.FromResult(infos.TryGetValue(egressId, out var info) ? info : null);
    }

    public void Forget(string id) => infos.Remove(id);
    public void Set(string id, string status, string? error = null) => infos[id] = new EgressInfo(id, status, error, []);

    /// <summary>The recording finishes: its file appears where it was asked to be put and LiveKit reports it.</summary>
    public async Task CompleteAsync(string id, byte[] content, double seconds = 95)
    {
        var start = Starts.Single(item => item.Id == id);
        var name = Path.GetFileName(start.Destination.FilePath);
        if (start.Destination.S3 is null) await File.WriteAllBytesAsync(Path.Combine(LocalDirectory!, name), content);
        else await PutObject!(start.Destination.FilePath, content);
        infos[id] = new EgressInfo(id, "EGRESS_COMPLETE", null, [new EgressFile(name, start.Destination.FilePath, content.Length, (long)(seconds * 1e9))]);
    }
}

public sealed class LiveKitEgressClientTests
{
    private sealed class Handler(Func<HttpRequestMessage, string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(string Url, string? Authorization, string Body)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));
            return answer(request, body);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(handler, disposeHandler: false); }

    private static readonly LiveKitCredentials Credentials = new("wss://classes.example.org", "APIkey123", "a-long-livekit-api-secret-for-tests-0123456789");
    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Starting_a_recording_asks_for_the_whole_room_as_one_mp4_with_a_token_that_may_only_record()
    {
        var handler = new Handler((_, _) => Json(HttpStatusCode.OK, """{"egress_id":"EG_abc","status":"EGRESS_STARTING"}"""));
        var id = await new LiveKitEgressClient(new Factory(handler)).StartRoomRecordingAsync(Credentials, "lms-room1", new EgressDestination("/out/class.mp4", null), CancellationToken.None);
        Assert.Equal("EG_abc", id);
        var call = Assert.Single(handler.Calls);
        Assert.Equal("https://classes.example.org/twirp/livekit.Egress/StartRoomCompositeEgress", call.Url);   // wss becomes https
        var request = JsonDocument.Parse(call.Body).RootElement;
        Assert.Equal("lms-room1", request.GetProperty("room_name").GetString());
        var output = request.GetProperty("file_outputs")[0];
        Assert.Equal("MP4", output.GetProperty("file_type").GetString());
        Assert.Equal("/out/class.mp4", output.GetProperty("filepath").GetString());
        Assert.False(output.TryGetProperty("s3", out _));

        var token = call.Authorization!["Bearer ".Length..].Split('.');
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(Credentials.ApiSecret), Encoding.ASCII.GetBytes($"{token[0]}.{token[1]}"));
        Assert.Equal(Convert.ToBase64String(expected).TrimEnd('=').Replace('+', '-').Replace('/', '_'), token[2]);   // signed with the organization's secret
        var claims = JsonDocument.Parse(Convert.FromBase64String(token[1].Replace('-', '+').Replace('_', '/').PadRight((token[1].Length + 3) / 4 * 4, '='))).RootElement;
        Assert.Equal("APIkey123", claims.GetProperty("iss").GetString());
        Assert.True(claims.GetProperty("video").GetProperty("roomRecord").GetBoolean());
        Assert.False(claims.GetProperty("video").TryGetProperty("roomJoin", out _));                              // it cannot join the room
    }

    [Fact]
    public async Task A_bucket_destination_carries_its_address_and_keys_to_the_recording_service()
    {
        var handler = new Handler((_, _) => Json(HttpStatusCode.OK, """{"egress_id":"EG_s3"}"""));
        await new LiveKitEgressClient(new Factory(handler)).StartRoomRecordingAsync(Credentials with { Url = "ws://localhost:7880" }, "r", new EgressDestination("lms/recordings/a.mp4", new EgressS3("AK", "SK", "us-east-1", "http://minio:9000", "bucket", true)), CancellationToken.None);
        Assert.StartsWith("http://localhost:7880/twirp/", handler.Calls[0].Url);
        var s3 = JsonDocument.Parse(handler.Calls[0].Body).RootElement.GetProperty("file_outputs")[0].GetProperty("s3");
        Assert.Equal(("AK", "SK", "http://minio:9000", "bucket", true), (s3.GetProperty("access_key").GetString(), s3.GetProperty("secret").GetString(), s3.GetProperty("endpoint").GetString(), s3.GetProperty("bucket").GetString(), s3.GetProperty("force_path_style").GetBoolean()));
    }

    [Fact]
    public async Task A_refusal_is_reported_with_LiveKits_own_words_and_unreachable_servers_are_named()
    {
        var refused = new LiveKitEgressClient(new Factory(new Handler((_, _) => Json(HttpStatusCode.NotFound, """{"code":"not_found","msg":"requested room does not exist"}"""))));
        var exception = await Assert.ThrowsAsync<LiveKitEgressException>(() => refused.StartRoomRecordingAsync(Credentials, "r", new EgressDestination("/out/a.mp4", null), CancellationToken.None));
        Assert.Equal("LiveKit says: requested room does not exist", exception.Message);

        var odd = new LiveKitEgressClient(new Factory(new Handler((_, _) => Json(HttpStatusCode.BadGateway, "<html>"))));
        Assert.Equal("LiveKit answered 502.", (await Assert.ThrowsAsync<LiveKitEgressException>(() => odd.StopAsync(Credentials, "EG", CancellationToken.None))).Message);

        var down = new LiveKitEgressClient(new Factory(new Handler((_, _) => throw new HttpRequestException("refused"))));
        Assert.Equal("LiveKit could not be reached.", (await Assert.ThrowsAsync<LiveKitEgressException>(() => down.GetAsync(Credentials, "EG", CancellationToken.None))).Message);
    }

    [Fact]
    public async Task A_recordings_state_is_read_with_its_file_whether_statuses_come_as_names_or_numbers()
    {
        var named = new Handler((_, _) => Json(HttpStatusCode.OK, """{"items":[{"egress_id":"EG_1","status":"EGRESS_COMPLETE","file_results":[{"filename":"a.mp4","location":"s3://b/a.mp4","size":"1234567","duration":"95000000000"}]}]}"""));
        var info = (await new LiveKitEgressClient(new Factory(named)).GetAsync(Credentials, "EG_1", CancellationToken.None))!;
        Assert.Equal("EGRESS_COMPLETE", info.Status);
        Assert.Equal(new EgressFile("a.mp4", "s3://b/a.mp4", 1234567, 95_000_000_000), Assert.Single(info.Files));
        Assert.Equal("""{"egress_id":"EG_1"}""", named.Calls[0].Body);

        var numbered = new Handler((_, _) => Json(HttpStatusCode.OK, """{"items":[{"egress_id":"EG_2","status":4,"error":"no space"}]}"""));
        var failed = (await new LiveKitEgressClient(new Factory(numbered)).GetAsync(Credentials, "EG_2", CancellationToken.None))!;
        Assert.Equal(("EGRESS_FAILED", "no space"), (failed.Status, failed.Error));

        var none = new LiveKitEgressClient(new Factory(new Handler((_, _) => Json(HttpStatusCode.OK, """{}"""))));
        Assert.Null(await none.GetAsync(Credentials, "EG_3", CancellationToken.None));
    }
}

/// <summary>Recording classes held in LiveKit: start and stop, and the finished file arriving in the video library.</summary>
public sealed class LiveKitRecordingTests : IDisposable
{
    private const string Settings = "/api/v1/tenant/integrations/live-classes";
    private const string Sessions = "/api/v1/tenant/live-classes/sessions";
    private static readonly byte[] Mp4 = [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'m', (byte)'p', (byte)'4', (byte)'2', 1, 2, 3, 4, 5, 6, 7, 8];
    private readonly string shared = Path.Combine(Path.GetTempPath(), "lms-egress-" + Guid.NewGuid().ToString("N"));

    public LiveKitRecordingTests() => Directory.CreateDirectory(shared);
    public void Dispose() { try { Directory.Delete(shared, true); } catch (IOException) { } }

    private sealed record Setup(TestWorld World, LmsApiFactory Factory, FakeEgressClient Egress, FakeVideoTranscoder Transcoder, Tenant Tenant, Person Teacher, Person Ada, Person Ben, CourseInfo Course);

    private async Task<Setup> NewSetupAsync(bool enabled = true, Dictionary<string, string?>? more = null, bool fakeStorage = false)
    {
        var egress = new FakeEgressClient { LocalDirectory = shared };
        var transcoder = new FakeVideoTranscoder();
        var settings = new Dictionary<string, string?> { ["LiveKit:Egress:Enabled"] = enabled ? "true" : "false", ["LiveKit:Egress:Destination"] = "Local", ["LiveKit:Egress:LocalDirectory"] = shared, ["LiveKit:Egress:ContainerPath"] = "/out" };
        foreach (var (key, value) in more ?? []) settings[key] = value;
        var factory = new LmsApiFactory { Egress = egress, Transcoder = transcoder, ExtraSettings = settings, UseFakeObjectStorage = fakeStorage };
        var world = new TestWorld(factory);
        var t = await world.NewTenantAsync();
        var teacher = await world.AddPersonAsync(t, "Tara", "TEACHER");
        var ada = await world.AddLearnerAsync(t, "Ada");
        var ben = await world.AddLearnerAsync(t, "Ben");
        var course = await world.NewCourseAsync(t, "REC-1");
        Assert.True((await EnrollAsync(ada, course)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.OK, (await t.Admin.PutAsJsonAsync(Settings, new { provider = "LiveKit", liveKitUrl = "wss://classes.example.org", liveKitApiKey = "APIkey123", liveKitApiSecret = "a-long-livekit-api-secret-for-tests-0123456789" })).StatusCode);
        return new Setup(world, factory, egress, transcoder, t, teacher, ada, ben, course);
    }

    private static async Task<Guid> ScheduleAsync(Setup s, string title = "Algebra", bool withCourse = true)
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        var response = await s.Teacher.Client.PostAsJsonAsync(Sessions, new { courseId = withCourse ? s.Course.Id : (Guid?)null, title, startAtUtc = start, endAtUtc = start.AddHours(1) });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    private static string Url(Guid id, string tail) => $"{Sessions}/{id}/{tail}";
    private static Task<HttpResponseMessage> ConsentAsync(Setup s, Guid id) => s.Teacher.Client.PostAsJsonAsync(Url(id, "consent"), new { granted = true });
    private static Task<HttpResponseMessage> StartAsync(Setup s, Guid id) => s.Teacher.Client.PostAsync(Url(id, "recording/start"), null);
    private static Task<HttpResponseMessage> StopAsync(Setup s, Guid id) => s.Teacher.Client.PostAsync(Url(id, "recording/stop"), null);
    private static Task<JsonElement> RecordingAsync(Setup s, Guid id) => s.Teacher.Client.GetAsync(Url(id, "recording")).ContinueWith(task => ReadAsync(task.Result)).Unwrap();
    private static Task<int> SyncAsync(Setup s) => s.Factory.Services.GetRequiredService<LiveKitRecordingSync>().RunOnceAsync(CancellationToken.None);
    private static Task<int> ConvertAsync(Setup s) => s.Factory.Services.GetRequiredService<VideoProcessingService>().RunOnceAsync(CancellationToken.None);
    private static async Task<string> StatusAsync(Setup s, Guid id) => (await RecordingAsync(s, id)).GetProperty("status").GetString()!;

    // ---------- starting ----------
    [Fact]
    public async Task Recording_needs_the_hosts_consent_a_livekit_class_and_the_server_to_be_set_up()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s);
        var response = await StartAsync(s, id);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("must grant recording consent", await response.Content.ReadAsStringAsync());
        Assert.Empty(s.Egress.Starts);

        var off = await NewSetupAsync(enabled: false);
        var offId = await ScheduleAsync(off);
        await ConsentAsync(off, offId);
        var notSetUp = await StartAsync(off, offId);
        Assert.Equal(HttpStatusCode.Conflict, notSetUp.StatusCode);
        Assert.Contains("not set up on this server", await notSetUp.Content.ReadAsStringAsync());

        await s.Tenant.Admin.PutAsJsonAsync(Settings, new { provider = "Jitsi" });
        var jitsi = (await ReadAsync(await s.Teacher.Client.PostAsJsonAsync(Sessions, new { courseId = s.Course.Id, title = "Jitsi", startAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5), endAtUtc = DateTimeOffset.UtcNow.AddHours(1) }))).GetProperty("id").GetGuid();
        await ConsentAsync(s, jitsi);
        Assert.Equal(HttpStatusCode.Conflict, (await StartAsync(s, jitsi)).StatusCode);
    }

    [Fact]
    public async Task Only_staff_who_manage_classes_can_start_or_stop_and_a_recording_needs_a_course_and_an_open_class()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s);
        await ConsentAsync(s, id);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Ada.Client.PostAsync(Url(id, "recording/start"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Ada.Client.PostAsync(Url(id, "recording/stop"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Teacher.Client.PostAsync(Url(Guid.NewGuid(), "recording/start"), null)).StatusCode);

        var noCourse = await ScheduleAsync(s, "No course", withCourse: false);
        await ConsentAsync(s, noCourse);
        var refused = await StartAsync(s, noCourse);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("Link this class to a course", await refused.Content.ReadAsStringAsync());

        await s.Teacher.Client.PostAsync(Url(id, "close"), null);
        Assert.Equal(HttpStatusCode.Conflict, (await StartAsync(s, id)).StatusCode);
        Assert.Empty(s.Egress.Starts);
    }

    [Fact]
    public async Task Starting_asks_LiveKit_to_record_the_classs_room_with_the_organizations_key_and_a_place_for_the_file()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s);
        var room = (await ReadAsync(await s.Teacher.Client.GetAsync(Sessions))).EnumerateArray().Single().GetProperty("providerMeetingId").GetString();
        await ConsentAsync(s, id);
        var started = await StartAsync(s, id);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var body = await ReadAsync(started);
        Assert.Equal("Recording", body.GetProperty("status").GetString());
        Assert.Equal("livekit", body.GetProperty("provider").GetString());

        var call = Assert.Single(s.Egress.Starts);
        Assert.Equal(room, call.Room);
        Assert.Equal("APIkey123", call.Credentials.ApiKey);
        Assert.Equal("wss://classes.example.org", call.Credentials.Url);
        Assert.Matches($"^/out/{id:N}-\\d+\\.mp4$", call.Destination.FilePath);
        Assert.Null(call.Destination.S3);

        var again = await StartAsync(s, id);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("already being recorded", await again.Content.ReadAsStringAsync());
        Assert.Single(s.Egress.Starts);
    }

    [Fact]
    public async Task When_nobody_is_in_the_room_yet_it_says_so_and_other_refusals_are_passed_on()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s);
        await ConsentAsync(s, id);
        s.Egress.StartError = "LiveKit says: requested room does not exist";
        var empty = await StartAsync(s, id);
        Assert.Equal(HttpStatusCode.Conflict, empty.StatusCode);
        Assert.Contains("Nobody is in the class room yet", await empty.Content.ReadAsStringAsync());
        s.Egress.StartError = "LiveKit could not be reached.";
        var down = await StartAsync(s, id);
        Assert.Equal(HttpStatusCode.BadGateway, down.StatusCode);
        Assert.Contains("could not be reached", await down.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await s.Teacher.Client.GetAsync(Url(id, "recording"))).StatusCode);     // nothing was recorded or saved
    }

    // ---------- the file arriving ----------
    [Fact]
    public async Task A_finished_recording_becomes_a_library_video_that_is_converted_and_learners_can_watch()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s, "Algebra");
        await ConsentAsync(s, id);
        await StartAsync(s, id);

        Assert.Equal(1, await SyncAsync(s));
        Assert.Equal("Recording", await StatusAsync(s, id));                                                    // still going
        var stopped = await StopAsync(s, id);
        Assert.Equal(HttpStatusCode.Accepted, stopped.StatusCode);
        Assert.Equal("Processing", (await ReadAsync(stopped)).GetProperty("status").GetString());
        Assert.Equal(["EG_1"], s.Egress.Stops);
        Assert.Equal(HttpStatusCode.Conflict, (await StopAsync(s, id)).StatusCode);                             // no longer recording

        await SyncAsync(s);                                                                                    // LiveKit is still finishing the file
        Assert.Equal("Processing", await StatusAsync(s, id));
        await s.Egress.CompleteAsync("EG_1", Mp4, seconds: 94.2);
        Assert.Equal(1, await SyncAsync(s));

        var recording = await RecordingAsync(s, id);
        Assert.Equal("Available", recording.GetProperty("status").GetString());
        var videoId = recording.GetProperty("videoId").GetGuid();
        Assert.Equal(0, await SyncAsync(s));                                                                    // nothing left to check
        Assert.Empty(Directory.GetFiles(shared));                                                               // the shared folder is cleaned up

        var video = await ReadAsync(await s.Teacher.Client.GetAsync($"/api/v1/tenant/videos/{videoId}"));
        Assert.Equal("LiveRecording", video.GetProperty("type").GetString());
        Assert.StartsWith("Algebra (recording ", video.GetProperty("title").GetString());
        Assert.Equal(s.Course.Title, video.GetProperty("courseTitle").GetString());
        Assert.Equal(Mp4.Length, video.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(95, video.GetProperty("durationSeconds").GetInt32());
        Assert.Equal("Processing", video.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await s.Ada.Client.GetAsync($"/api/v1/tenant/videos/{videoId}")).StatusCode);   // not ready yet

        Assert.Equal(1, await ConvertAsync(s));                                                                 // streaming pieces and a poster, like any upload
        var ready = await ReadAsync(await s.Ada.Client.GetAsync($"/api/v1/tenant/videos/{videoId}"));
        Assert.Equal("Ready", ready.GetProperty("status").GetString());
        Assert.True(ready.GetProperty("hasStreaming").GetBoolean());
        Assert.Equal("hls", (await ReadAsync(await s.Ada.Client.GetAsync($"/api/v1/tenant/videos/{videoId}/link"))).GetProperty("kind").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await s.Ben.Client.GetAsync($"/api/v1/tenant/videos/{videoId}")).StatusCode);     // not enrolled
    }

    [Fact]
    public async Task Without_conversion_set_up_the_recording_is_ready_straight_away_and_can_be_transcribed_like_any_video()
    {
        var s = await NewSetupAsync();
        s.Transcoder.Enabled = false;
        var id = await ScheduleAsync(s);
        await ConsentAsync(s, id);
        await StartAsync(s, id);
        await StopAsync(s, id);
        await s.Egress.CompleteAsync("EG_1", Mp4);
        await SyncAsync(s);
        var videoId = (await RecordingAsync(s, id)).GetProperty("videoId").GetGuid();
        Assert.Equal("Ready", (await ReadAsync(await s.Ada.Client.GetAsync($"/api/v1/tenant/videos/{videoId}"))).GetProperty("status").GetString());

        // A class recording takes a pasted transcript, summaries and the rest, like an upload.
        var pasted = await s.Teacher.Client.PostAsJsonAsync($"/api/v1/tenant/videos/{videoId}/transcript", new { text = "WEBVTT\n\n00:00:00.000 --> 00:00:04.000\nWelcome to the class." });
        Assert.Equal(HttpStatusCode.OK, pasted.StatusCode);
    }

    [Fact]
    public async Task A_recording_class_closed_while_recording_is_stopped_and_one_that_ran_over_is_stopped_by_the_next_check()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s);
        await ConsentAsync(s, id);
        await StartAsync(s, id);
        Assert.Equal(HttpStatusCode.OK, (await s.Teacher.Client.PostAsync(Url(id, "close"), null)).StatusCode);
        Assert.Equal(["EG_1"], s.Egress.Stops);
        Assert.Equal("Processing", await StatusAsync(s, id));

        var other = await ScheduleAsync(s, "Late class");
        await ConsentAsync(s, other);
        await StartAsync(s, other);
        await s.World.WithDbAsync(s.Tenant.Slug, async db => { var session = await db.LiveClassSessions.SingleAsync(item => item.Id == other); session.Status = Lms.Api.Domain.LiveClasses.LiveSessionStatus.Completed; await db.SaveChangesAsync(); });   // ended some other way
        await SyncAsync(s);
        Assert.Equal(["EG_1", "EG_2"], s.Egress.Stops);
    }

    [Fact]
    public async Task A_failed_recording_says_why_and_can_be_started_again_without_the_built_in_worker_touching_it()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s);
        await ConsentAsync(s, id);
        await StartAsync(s, id);
        s.Egress.Set("EG_1", "EGRESS_FAILED", "out of disk space");
        await SyncAsync(s);
        var failed = await RecordingAsync(s, id);
        Assert.Equal("Failed", failed.GetProperty("status").GetString());
        Assert.Equal("LiveKit could not finish the recording: out of disk space", failed.GetProperty("lastError").GetString());
        Assert.Equal(0, failed.GetProperty("maxAttempts").GetInt32());                                         // the worker that makes built-in recordings skips it
        Assert.Equal(HttpStatusCode.Conflict, (await s.Teacher.Client.PostAsync(Url(id, "recording/retry"), null)).StatusCode);

        Assert.Equal(HttpStatusCode.Accepted, (await StartAsync(s, id)).StatusCode);                            // start again
        Assert.Equal(2, s.Egress.Starts.Count);
        Assert.Equal("Recording", await StatusAsync(s, id));
        Assert.Null((await RecordingAsync(s, id)).GetProperty("lastError").GetString());
    }

    [Fact]
    public async Task A_missing_or_empty_file_fails_the_recording_and_a_service_that_cannot_be_reached_is_tried_again_later()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s);
        await ConsentAsync(s, id);
        await StartAsync(s, id);
        s.Egress.Unreachable = true;
        await SyncAsync(s);
        Assert.Equal("Recording", await StatusAsync(s, id));                                                    // nothing lost: checked again next round
        s.Egress.Unreachable = false;

        s.Egress.Set("EG_1", "EGRESS_COMPLETE");                                                                // finished, but no file reported
        await SyncAsync(s);
        var none = await RecordingAsync(s, id);
        Assert.Equal("Failed", none.GetProperty("status").GetString());
        Assert.Contains("reported no file", none.GetProperty("lastError").GetString());

        await StartAsync(s, id);
        await s.Egress.CompleteAsync("EG_2", []);
        await SyncAsync(s);
        Assert.Contains("empty", (await RecordingAsync(s, id)).GetProperty("lastError").GetString());

        await StartAsync(s, id);
        await s.Egress.CompleteAsync("EG_3", Mp4);
        foreach (var file in Directory.GetFiles(shared)) File.Delete(file);                                      // the file never arrived
        await SyncAsync(s);
        Assert.Contains("not found", (await RecordingAsync(s, id)).GetProperty("lastError").GetString());
        await s.World.WithDbAsync(s.Tenant.Slug, async db => Assert.Empty(await db.Videos.ToListAsync()));       // nothing half-made in the library
    }

    [Fact]
    public async Task A_recording_LiveKit_has_no_record_of_fails()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s);
        await ConsentAsync(s, id);
        await StartAsync(s, id);
        s.Egress.Forget("EG_1");
        await SyncAsync(s);
        Assert.Contains("no record", (await RecordingAsync(s, id)).GetProperty("lastError").GetString());
    }

    [Fact]
    public async Task Classes_held_elsewhere_and_the_built_in_request_flow_are_unaffected()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s);
        await ConsentAsync(s, id);
        var request = await s.Teacher.Client.PostAsync(Url(id, "recording/request"), null);
        Assert.Equal(HttpStatusCode.Conflict, request.StatusCode);
        Assert.Contains("Start recording", await request.Content.ReadAsStringAsync());
        // Attaching a link still works for a class that could not be recorded here.
        Assert.Equal(HttpStatusCode.OK, (await s.Teacher.Client.PostAsJsonAsync(Url(id, "recording/link"), new { url = "https://storage.example.org/recordings/1.mp4" })).StatusCode);
    }

    // ---------- recording into the storage bucket ----------
    [Fact]
    public async Task With_bucket_storage_the_recording_service_writes_straight_into_the_bucket_and_the_library_uses_that_object()
    {
        var more = new Dictionary<string, string?>
        {
            ["LiveKit:Egress:Destination"] = "S3", ["LiveKit:Egress:S3ServiceUrl"] = "http://minio:9000", ["Storage:S3:KeyPrefix"] = "lms/",
            ["Storage:S3:AccessKeyId"] = "AKIATEST", ["Storage:S3:SecretAccessKey"] = "secret-test-key", ["Storage:S3:Region"] = "eu-west-1", ["Storage:S3:ForcePathStyle"] = "true"
        };
        var s = await NewSetupAsync(more: more, fakeStorage: true);
        s.Egress.PutObject = (key, bytes) => s.Factory.Store.PutAsync(key["lms/".Length..], new MemoryStream(bytes), bytes.Length, "video/mp4", CancellationToken.None);   // the bucket adds its prefix itself
        var id = await ScheduleAsync(s);
        await ConsentAsync(s, id);
        await StartAsync(s, id);

        var s3 = Assert.Single(s.Egress.Starts).Destination;
        Assert.Matches($"^lms/recordings/[0-9a-f-]{{36}}/{id:D}/{id:N}-\\d+\\.mp4$", s3.FilePath);
        Assert.Equal(("AKIATEST", "secret-test-key", "eu-west-1", "http://minio:9000", "test-bucket", true), (s3.S3!.AccessKey, s3.S3.Secret, s3.S3.Region, s3.S3.Endpoint, s3.S3.Bucket, s3.S3.ForcePathStyle));

        await StopAsync(s, id);
        await s.Egress.CompleteAsync("EG_1", Mp4);
        await SyncAsync(s);
        var videoId = (await RecordingAsync(s, id)).GetProperty("videoId").GetGuid();
        Assert.Equal("Processing", (await ReadAsync(await s.Teacher.Client.GetAsync($"/api/v1/tenant/videos/{videoId}"))).GetProperty("status").GetString());
        var storage = s.Factory.Services.GetRequiredService<IContentAssetStorage>();
        Assert.True(await storage.ExistsAsync(s3.FilePath["lms/".Length..], CancellationToken.None));

        Assert.Equal(HttpStatusCode.NoContent, (await s.Teacher.Client.DeleteAsync($"/api/v1/tenant/videos/{videoId}")).StatusCode);   // deleting the video removes the file
        Assert.False(await storage.ExistsAsync(s3.FilePath["lms/".Length..], CancellationToken.None));
    }

    [Fact]
    public async Task Bucket_recording_needs_bucket_storage_and_keys()
    {
        var s = await NewSetupAsync(more: new() { ["LiveKit:Egress:Destination"] = "S3" });                     // local disk storage
        var id = await ScheduleAsync(s);
        await ConsentAsync(s, id);
        var response = await StartAsync(s, id);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Storage:Provider to be S3", await response.Content.ReadAsStringAsync());

        var noKeys = await NewSetupAsync(more: new() { ["LiveKit:Egress:Destination"] = "S3" }, fakeStorage: true);
        var noKeysId = await ScheduleAsync(noKeys);
        await ConsentAsync(noKeys, noKeysId);
        Assert.Contains("access key", await (await StartAsync(noKeys, noKeysId)).Content.ReadAsStringAsync());

        var local = await NewSetupAsync(more: new() { ["LiveKit:Egress:LocalDirectory"] = "" });
        var localId = await ScheduleAsync(local);
        await ConsentAsync(local, localId);
        Assert.Contains("LocalDirectory is missing", await (await StartAsync(local, localId)).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Another_organization_cannot_start_stop_or_see_a_recording()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s);
        await ConsentAsync(s, id);
        await StartAsync(s, id);
        var other = await s.World.NewTenantAsync();
        var outsider = await s.World.AddPersonAsync(other, "Olga", "TEACHER");
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.Client.PostAsync(Url(id, "recording/start"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.Client.PostAsync(Url(id, "recording/stop"), null)).StatusCode);
        Assert.Single(s.Egress.Starts);
    }
}
