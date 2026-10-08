using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lms.Api.Domain.LiveClasses;
using Lms.Api.Infrastructure.LiveClasses;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

/// <summary>Stands in for LiveKit's room service: who is in the room, whose microphone was switched off, which rooms were closed.</summary>
public sealed class FakeRoomClient : ILiveKitRoomClient
{
    public List<RoomParticipant> People { get; } = [];
    public List<string> Muted { get; } = [];
    public List<string> Closed { get; } = [];
    public bool Unreachable { get; set; }

    public Task<IReadOnlyList<RoomParticipant>> ListParticipantsAsync(LiveKitCredentials credentials, string room, CancellationToken cancellationToken)
        => Unreachable ? throw new LiveKitApiException("LiveKit could not be reached.") : Task.FromResult<IReadOnlyList<RoomParticipant>>(People.ToList());

    public Task<int> MuteAsync(LiveKitCredentials credentials, string room, string identity, CancellationToken cancellationToken)
    {
        if (Unreachable) throw new LiveKitApiException("LiveKit could not be reached.");
        var person = People.FirstOrDefault(item => item.Identity == identity);
        var count = person?.Tracks.Count(track => track.IsAudio && !track.Muted) ?? 0;
        if (count > 0) Muted.Add(identity);
        return Task.FromResult(count);
    }

    public Task CloseAsync(LiveKitCredentials credentials, string room, CancellationToken cancellationToken) { Closed.Add(room); return Task.CompletedTask; }

    public static RoomParticipant Speaker(Guid id, bool muted = false) => new(id.ToString("D"), null, [new RoomTrack("TR_" + id.ToString("N")[..6], true, muted)]);
}

public sealed class LiveKitWebhookVerifierTests
{
    private static readonly LiveKitCredentials Credentials = new("wss://classes.example.org", "APIkey123", "a-long-livekit-api-secret-for-tests-0123456789");

    [Fact]
    public void A_call_signed_with_the_secret_for_exactly_this_body_is_accepted()
    {
        const string body = """{"event":"participant_joined"}""";
        Assert.True(LiveKitWebhookVerifier.IsValid(Credentials, LiveKitWebhookVerifier.Sign(Credentials, body), body));
        Assert.True(LiveKitWebhookVerifier.IsValid(Credentials, "Bearer " + LiveKitWebhookVerifier.Sign(Credentials, body), body));
    }

    [Fact]
    public void Another_body_another_secret_another_key_an_old_token_or_no_token_is_refused()
    {
        const string body = """{"event":"participant_joined"}""";
        var signed = LiveKitWebhookVerifier.Sign(Credentials, body);
        Assert.False(LiveKitWebhookVerifier.IsValid(Credentials, signed, """{"event":"room_finished"}"""));
        Assert.False(LiveKitWebhookVerifier.IsValid(Credentials with { ApiSecret = "another-long-secret-of-another-organization-0123456789" }, signed, body));
        Assert.False(LiveKitWebhookVerifier.IsValid(Credentials with { ApiKey = "someoneelse" }, signed, body));
        Assert.False(LiveKitWebhookVerifier.IsValid(Credentials, LiveKitWebhookVerifier.Sign(Credentials, body, DateTimeOffset.UtcNow.AddHours(-2)), body));
        Assert.False(LiveKitWebhookVerifier.IsValid(Credentials, null, body));
        Assert.False(LiveKitWebhookVerifier.IsValid(Credentials, "", body));
        Assert.False(LiveKitWebhookVerifier.IsValid(Credentials, "not.a.token", body));
        Assert.False(LiveKitWebhookVerifier.IsValid(Credentials, "garbage", body));
    }
}

public sealed class LiveKitRoomClientTests
{
    private sealed class Handler(Func<string, string> answer) : HttpMessageHandler
    {
        public List<(string Url, string Body)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request.RequestUri!.ToString(), body));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer(request.RequestUri.AbsolutePath), Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(handler, disposeHandler: false); }
    private static readonly LiveKitCredentials Credentials = new("wss://classes.example.org", "APIkey123", "a-long-livekit-api-secret-for-tests-0123456789");

    [Fact]
    public async Task Muting_switches_off_only_the_microphones_that_are_on_whether_the_kind_is_a_name_or_a_number()
    {
        var handler = new Handler(path => path.EndsWith("ListParticipants")
            ? """{"participants":[{"identity":"u1","tracks":[{"sid":"TR_a","type":"AUDIO","muted":false},{"sid":"TR_v","type":"VIDEO"},{"sid":"TR_b","type":0,"muted":true}]}]}"""
            : "{}");
        var muted = await new LiveKitRoomClient(new Factory(handler)).MuteAsync(Credentials, "lms-room", "u1", CancellationToken.None);
        Assert.Equal(1, muted);
        var call = handler.Calls.Single(item => item.Url.EndsWith("MutePublishedTrack"));
        Assert.Equal("https://classes.example.org/twirp/livekit.RoomService/MutePublishedTrack", call.Url);
        var body = JsonDocument.Parse(call.Body).RootElement;
        Assert.Equal(("lms-room", "u1", "TR_a", true), (body.GetProperty("room").GetString(), body.GetProperty("identity").GetString(), body.GetProperty("track_sid").GetString(), body.GetProperty("muted").GetBoolean()));
    }

    [Fact]
    public async Task Someone_who_is_not_in_the_room_is_left_alone()
    {
        var handler = new Handler(_ => """{"participants":[]}""");
        Assert.Equal(0, await new LiveKitRoomClient(new Factory(handler)).MuteAsync(Credentials, "lms-room", "u9", CancellationToken.None));
        Assert.DoesNotContain(handler.Calls, item => item.Url.EndsWith("MutePublishedTrack"));
    }
}

/// <summary>Classes held in LiveKit that run themselves: attendance from LiveKit's webhook, the host's mute controls, a class that goes live, records and closes with its schedule, and each person recorded on their own.</summary>
public sealed class LiveKitAutomationTests : IDisposable
{
    private const string Settings = "/api/v1/tenant/integrations/live-classes";
    private const string Sessions = "/api/v1/tenant/live-classes/sessions";
    private const string Webhook = "/api/v1/integrations/livekit/webhook";
    private const string Key = "APIkey123", Secret = "a-long-livekit-api-secret-for-tests-0123456789";
    private static readonly byte[] Mp4 = [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'m', (byte)'p', (byte)'4', (byte)'2', 1, 2, 3, 4, 5, 6, 7, 8];
    private readonly string shared = Path.Combine(Path.GetTempPath(), "lms-automation-" + Guid.NewGuid().ToString("N"));

    public LiveKitAutomationTests() => Directory.CreateDirectory(shared);
    public void Dispose() { try { Directory.Delete(shared, true); } catch (IOException) { } }

    private sealed record Setup(TestWorld World, LmsApiFactory Factory, FakeEgressClient Egress, FakeRoomClient Rooms, Tenant Tenant, Person Teacher, Person Ada, Person Ben, CourseInfo Course);

    private async Task<Setup> NewSetupAsync(bool egress = true, bool tracks = false)
    {
        var fakeEgress = new FakeEgressClient { LocalDirectory = shared };
        var rooms = new FakeRoomClient();
        var settings = new Dictionary<string, string?> { ["LiveKit:Egress:Enabled"] = egress ? "true" : "false", ["LiveKit:Egress:Destination"] = "Local", ["LiveKit:Egress:LocalDirectory"] = shared, ["LiveKit:Egress:ContainerPath"] = "/out", ["LiveKit:Egress:SeparateTracks"] = tracks ? "true" : "false" };
        var factory = new LmsApiFactory { Egress = fakeEgress, Rooms = rooms, Transcoder = new FakeVideoTranscoder(), ExtraSettings = settings };
        var world = new TestWorld(factory);
        var t = await world.NewTenantAsync();
        var teacher = await world.AddPersonAsync(t, "Tara", "TEACHER");
        var ada = await world.AddLearnerAsync(t, "Ada");
        var ben = await world.AddLearnerAsync(t, "Ben");
        var course = await world.NewCourseAsync(t, "AUTO-1");
        Assert.True((await EnrollAsync(ada, course)).IsSuccessStatusCode);
        Assert.True((await EnrollAsync(ben, course)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.OK, (await t.Admin.PutAsJsonAsync(Settings, new { provider = "LiveKit", liveKitUrl = "wss://classes.example.org", liveKitApiKey = Key, liveKitApiSecret = Secret })).StatusCode);
        return new Setup(world, factory, fakeEgress, rooms, t, teacher, ada, ben, course);
    }

    private static async Task<(Guid Id, string Room)> ScheduleAsync(Setup s, bool autoRecord = false, string title = "Algebra", int startMinutes = -5)
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(startMinutes);
        var response = await s.Teacher.Client.PostAsJsonAsync(Sessions, new { courseId = s.Course.Id, title, startAtUtc = start, endAtUtc = start.AddHours(1), autoRecord });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadAsync(response);
        return (body.GetProperty("id").GetGuid(), body.GetProperty("providerMeetingId").GetString()!);
    }

    private static string Url(Guid id, string tail) => $"{Sessions}/{id}/{tail}";
    private static Task<HttpResponseMessage> SendAsync(Setup s, object payload, string secret = Secret, bool sign = true)
    {
        var body = JsonSerializer.Serialize(payload);
        var request = new HttpRequestMessage(HttpMethod.Post, Webhook) { Content = new StringContent(body, Encoding.UTF8, "application/webhook+json") };
        if (sign) request.Headers.TryAddWithoutValidation("Authorization", LiveKitWebhookVerifier.Sign(new LiveKitCredentials("wss://classes.example.org", Key, secret), body));
        return s.Factory.CreateClient().SendAsync(request);
    }
    private static Task<HttpResponseMessage> JoinedAsync(Setup s, string room, string identity) => SendAsync(s, new { @event = "participant_joined", room = new { name = room }, participant = new { identity } });
    private static Task<HttpResponseMessage> LeftAsync(Setup s, string room, string identity) => SendAsync(s, new { @event = "participant_left", room = new { name = room }, participant = new { identity } });
    private static async Task<JsonElement> AttendanceAsync(Setup s, Guid id) => await ReadAsync(await s.Teacher.Client.GetAsync(Url(id, "attendance")));
    private static async Task<string> StatusOfAsync(Setup s, Guid id) => (await ReadAsync(await s.Teacher.Client.GetAsync(Sessions))).EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == id).GetProperty("status").GetString()!;
    private static async Task<JsonElement> RecordingAsync(Setup s, Guid id) => await ReadAsync(await s.Teacher.Client.GetAsync(Url(id, "recording")));
    private static Task<int> SyncAsync(Setup s) => s.Factory.Services.GetRequiredService<LiveKitRecordingSync>().RunOnceAsync(CancellationToken.None);
    private static Task<int> AutomateAsync(Setup s) => s.Factory.Services.GetRequiredService<LiveKitClassAutomation>().RunOnceAsync(CancellationToken.None);

    // ---------- webhook ----------
    [Fact]
    public async Task A_call_that_is_not_signed_by_the_organizations_LiveKit_is_refused_and_one_for_an_unknown_room_changes_nothing()
    {
        var s = await NewSetupAsync();
        var (id, room) = await ScheduleAsync(s);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(s, new { @event = "participant_joined", room = new { name = room }, participant = new { identity = s.Ada.Id } }, sign: false)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(s, new { @event = "participant_joined", room = new { name = room }, participant = new { identity = s.Ben.Id } }, secret: "another-long-secret-of-another-organization-0123456789")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(s, new { @event = "participant_joined", room = new { name = "lms-nowhere" }, participant = new { identity = s.Ben.Id } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(s, new { })).StatusCode);
        var rows = (await AttendanceAsync(s, id)).EnumerateArray().ToList();
        Assert.DoesNotContain(rows, row => row.GetProperty("userId").GetGuid() == s.Ben.Id);
    }

    [Fact]
    public async Task Joining_and_leaving_the_room_records_attendance_and_puts_the_class_on_air_without_the_browser_reporting()
    {
        var s = await NewSetupAsync();
        var (id, room) = await ScheduleAsync(s);
        Assert.Equal("Scheduled", await StatusOfAsync(s, id));
        Assert.Equal(HttpStatusCode.OK, (await JoinedAsync(s, room, s.Ada.Id.ToString("D"))).StatusCode);
        Assert.Equal("Live", await StatusOfAsync(s, id));
        var row = Assert.Single((await AttendanceAsync(s, id)).EnumerateArray());
        Assert.Equal((s.Ada.Id, "Present"), (row.GetProperty("userId").GetGuid(), row.GetProperty("status").GetString()));

        // The recording service joins the room too, as a guest with no person behind it; it is not counted.
        Assert.Equal(HttpStatusCode.OK, (await JoinedAsync(s, room, "EG_abc123")).StatusCode);
        Assert.Single((await AttendanceAsync(s, id)).EnumerateArray());

        await s.World.WithDbAsync(s.Tenant.Slug, async db => { var a = await db.SessionAttendances.SingleAsync(); a.JoinedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-20); await db.SaveChangesAsync(); });
        await LeftAsync(s, room, s.Ada.Id.ToString("D"));
        var left = Assert.Single((await AttendanceAsync(s, id)).EnumerateArray());
        Assert.Equal("Left", left.GetProperty("status").GetString());
        Assert.InRange(left.GetProperty("durationSeconds").GetInt32(), 19 * 60, 21 * 60);

        // Coming back counts as present again; leaving through the page afterwards changes nothing already said by LiveKit.
        await JoinedAsync(s, room, s.Ada.Id.ToString("D"));
        Assert.Equal("Present", (await AttendanceAsync(s, id)).EnumerateArray().Single().GetProperty("status").GetString());
        await LeftAsync(s, room, s.Ada.Id.ToString("D"));
        Assert.Equal(HttpStatusCode.OK, (await s.Ada.Client.PostAsync(Url(id, "leave"), null)).StatusCode);
        Assert.Equal("Left", (await AttendanceAsync(s, id)).EnumerateArray().Single().GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_person_of_another_organization_in_the_room_is_not_counted()
    {
        var s = await NewSetupAsync();
        var (id, room) = await ScheduleAsync(s);
        await JoinedAsync(s, room, Guid.NewGuid().ToString("D"));
        Assert.Empty((await AttendanceAsync(s, id)).EnumerateArray());
    }

    [Fact]
    public async Task The_end_of_the_room_stops_a_recording_and_ends_a_class_that_is_over_but_not_one_still_on()
    {
        var s = await NewSetupAsync();
        var (id, room) = await ScheduleAsync(s, autoRecord: true);
        await JoinedAsync(s, room, s.Teacher.Id.ToString("D"));
        Assert.Equal("Recording", (await RecordingAsync(s, id)).GetProperty("status").GetString());
        await SendAsync(s, new { @event = "room_finished", room = new { name = room } });
        Assert.Equal(["EG_1"], s.Egress.Stops);
        Assert.Equal("Live", await StatusOfAsync(s, id));                                                       // the hour is not over: people may come back

        await s.World.WithDbAsync(s.Tenant.Slug, async db => { var session = await db.LiveClassSessions.SingleAsync(); session.EndAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1); await db.SaveChangesAsync(); });
        await SendAsync(s, new { @event = "room_finished", room = new { name = room } });
        Assert.Equal("Completed", await StatusOfAsync(s, id));
    }

    [Fact]
    public async Task When_LiveKit_says_a_recording_ended_the_file_is_brought_in_without_waiting_for_the_next_check()
    {
        var s = await NewSetupAsync();
        var (id, room) = await ScheduleAsync(s);
        await s.Teacher.Client.PostAsJsonAsync(Url(id, "consent"), new { granted = true });
        Assert.Equal(HttpStatusCode.Accepted, (await s.Teacher.Client.PostAsync(Url(id, "recording/start"), null)).StatusCode);
        await s.Teacher.Client.PostAsync(Url(id, "recording/stop"), null);
        await s.Egress.CompleteAsync("EG_1", Mp4);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(s, new { @event = "egress_ended", egressInfo = new { egressId = "EG_1", roomName = room } })).StatusCode);
        Assert.Equal("Available", (await RecordingAsync(s, id)).GetProperty("status").GetString());
    }

    // ---------- muting ----------
    [Fact]
    public async Task The_host_can_switch_off_one_microphone_or_everyones_but_their_own_and_learners_cannot()
    {
        var s = await NewSetupAsync();
        var (id, _) = await ScheduleAsync(s);
        s.Rooms.People.AddRange([FakeRoomClient.Speaker(s.Teacher.Id), FakeRoomClient.Speaker(s.Ada.Id), FakeRoomClient.Speaker(s.Ben.Id, muted: true)]);

        Assert.Equal(HttpStatusCode.Forbidden, (await s.Ada.Client.PostAsync(Url(id, $"mute/{s.Ben.Id}"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Ada.Client.PostAsync(Url(id, "mute-all"), null)).StatusCode);
        Assert.Empty(s.Rooms.Muted);

        var one = await s.Teacher.Client.PostAsync(Url(id, $"mute/{s.Ada.Id}"), null);
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        Assert.Equal(1, (await ReadAsync(one)).GetProperty("muted").GetInt32());
        Assert.Equal([s.Ada.Id.ToString("D")], s.Rooms.Muted);

        s.Rooms.Muted.Clear();
        var all = await ReadAsync(await s.Teacher.Client.PostAsync(Url(id, "mute-all"), null));
        Assert.Equal(1, all.GetProperty("muted").GetInt32());                                                   // Ada; Ben's was off already and the host is the host
        Assert.Equal([s.Ada.Id.ToString("D")], s.Rooms.Muted);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Teacher.Client.PostAsync(Url(Guid.NewGuid(), "mute-all"), null)).StatusCode);
    }

    [Fact]
    public async Task Muting_a_class_that_has_ended_or_when_LiveKit_cannot_be_reached_says_so()
    {
        var s = await NewSetupAsync();
        var (id, _) = await ScheduleAsync(s);
        s.Rooms.Unreachable = true;
        var down = await s.Teacher.Client.PostAsync(Url(id, "mute-all"), null);
        Assert.Equal(HttpStatusCode.BadGateway, down.StatusCode);
        Assert.Contains("could not be reached", await down.Content.ReadAsStringAsync());
        s.Rooms.Unreachable = false;
        await s.Teacher.Client.PostAsync(Url(id, "close"), null);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Teacher.Client.PostAsync(Url(id, "mute-all"), null)).StatusCode);
    }

    // ---------- starting and stopping with the class ----------
    [Fact]
    public async Task A_class_can_only_be_set_to_record_itself_when_it_can_be_recorded()
    {
        var off = await NewSetupAsync(egress: false);
        var start = DateTimeOffset.UtcNow.AddHours(1);
        var refused = await off.Teacher.Client.PostAsJsonAsync(Sessions, new { courseId = off.Course.Id, title = "A", startAtUtc = start, endAtUtc = start.AddHours(1), autoRecord = true });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("not set up on this server", await refused.Content.ReadAsStringAsync());

        var s = await NewSetupAsync();
        var noCourse = await s.Teacher.Client.PostAsJsonAsync(Sessions, new { title = "B", startAtUtc = start, endAtUtc = start.AddHours(1), autoRecord = true });
        Assert.Equal(HttpStatusCode.BadRequest, noCourse.StatusCode);
        Assert.Contains("Link the class to a course", await noCourse.Content.ReadAsStringAsync());

        await s.Tenant.Admin.PutAsJsonAsync(Settings, new { provider = "Jitsi" });
        var jitsi = await s.Teacher.Client.PostAsJsonAsync(Sessions, new { courseId = s.Course.Id, title = "C", startAtUtc = start, endAtUtc = start.AddHours(1), autoRecord = true });
        Assert.Equal(HttpStatusCode.BadRequest, jitsi.StatusCode);
        Assert.Contains("LiveKit", await jitsi.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_class_set_to_record_itself_starts_recording_when_the_first_person_is_in_the_room_and_only_once()
    {
        var s = await NewSetupAsync();
        var (id, room) = await ScheduleAsync(s, autoRecord: true);
        Assert.True((await ReadAsync(await s.Teacher.Client.GetAsync(Sessions))).EnumerateArray().Single().GetProperty("autoRecord").GetBoolean());
        Assert.Empty(s.Egress.Starts);

        await JoinedAsync(s, room, s.Teacher.Id.ToString("D"));
        var call = Assert.Single(s.Egress.Starts);
        Assert.Equal(room, call.Room);
        Assert.Equal("Recording", (await RecordingAsync(s, id)).GetProperty("status").GetString());

        await JoinedAsync(s, room, s.Ada.Id.ToString("D"));
        Assert.Single(s.Egress.Starts);

        // Stopped on purpose, it does not start itself again.
        await s.Teacher.Client.PostAsync(Url(id, "recording/stop"), null);
        await JoinedAsync(s, room, s.Ben.Id.ToString("D"));
        Assert.Single(s.Egress.Starts);
    }

    [Fact]
    public async Task A_class_not_set_to_record_itself_never_does()
    {
        var s = await NewSetupAsync();
        var (id, room) = await ScheduleAsync(s);
        await JoinedAsync(s, room, s.Teacher.Id.ToString("D"));
        Assert.Empty(s.Egress.Starts);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Teacher.Client.GetAsync(Url(id, "recording"))).StatusCode);
        Assert.False((await ReadAsync(await s.Teacher.Client.GetAsync(Sessions))).EnumerateArray().Single().GetProperty("autoRecord").GetBoolean());
    }

    [Fact]
    public async Task The_next_check_starts_recording_when_nobody_was_in_the_room_the_first_time()
    {
        var s = await NewSetupAsync();
        var (id, _) = await ScheduleAsync(s, autoRecord: true);
        s.Egress.StartError = "LiveKit says: requested room does not exist";
        await AutomateAsync(s);
        Assert.Empty(s.Egress.Starts);
        s.Egress.StartError = null;
        await AutomateAsync(s);
        Assert.Single(s.Egress.Starts);
        Assert.Equal("Recording", (await RecordingAsync(s, id)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_class_goes_live_at_its_start_time_and_one_not_yet_started_is_left_alone()
    {
        var s = await NewSetupAsync();
        var (due, _) = await ScheduleAsync(s, title: "Now");
        var (later, _) = await ScheduleAsync(s, title: "Later", startMinutes: 60);
        Assert.Equal(1, await AutomateAsync(s));                                                                 // only the one whose time has come is looked at
        Assert.Equal("Live", await StatusOfAsync(s, due));
        Assert.Equal("Scheduled", await StatusOfAsync(s, later));
    }

    [Fact]
    public async Task A_class_that_ran_past_its_end_is_closed_with_its_recording_and_its_room_but_one_just_over_is_given_time()
    {
        var s = await NewSetupAsync();
        var (id, room) = await ScheduleAsync(s, autoRecord: true);
        await JoinedAsync(s, room, s.Ada.Id.ToString("D"));
        await s.World.WithDbAsync(s.Tenant.Slug, async db => { var session = await db.LiveClassSessions.SingleAsync(); session.EndAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5); await db.SaveChangesAsync(); });
        await AutomateAsync(s);
        Assert.Equal("Live", await StatusOfAsync(s, id));                                                        // five minutes over is within the grace period
        Assert.Empty(s.Rooms.Closed);

        await s.World.WithDbAsync(s.Tenant.Slug, async db => { var session = await db.LiveClassSessions.SingleAsync(); session.EndAtUtc = DateTimeOffset.UtcNow.AddMinutes(-30); await db.SaveChangesAsync(); });
        await AutomateAsync(s);
        Assert.Equal("Completed", await StatusOfAsync(s, id));
        Assert.Equal([room], s.Rooms.Closed);
        Assert.Equal(["EG_1"], s.Egress.Stops);
        Assert.Equal("Processing", (await RecordingAsync(s, id)).GetProperty("status").GetString());
        Assert.Equal("Left", (await AttendanceAsync(s, id)).EnumerateArray().Single().GetProperty("status").GetString());

        Assert.Equal(0, await AutomateAsync(s));                                                                 // done: nothing more to look at
    }

    // ---------- each person on their own ----------
    [Fact]
    public async Task With_separate_tracks_on_each_person_in_the_room_gets_their_own_recording_when_the_class_recording_starts_and_when_they_arrive_later()
    {
        var s = await NewSetupAsync(tracks: true);
        var (id, room) = await ScheduleAsync(s);
        await JoinedAsync(s, room, s.Ada.Id.ToString("D"));                                                      // before recording: nothing yet
        Assert.Empty(s.Egress.TrackStarts);

        s.Rooms.People.AddRange([FakeRoomClient.Speaker(s.Teacher.Id), FakeRoomClient.Speaker(s.Ada.Id)]);
        await s.Teacher.Client.PostAsJsonAsync(Url(id, "consent"), new { granted = true });
        Assert.Equal(HttpStatusCode.Accepted, (await s.Teacher.Client.PostAsync(Url(id, "recording/start"), null)).StatusCode);
        Assert.Equal([s.Teacher.Id.ToString("D"), s.Ada.Id.ToString("D")], s.Egress.TrackStarts.Select(item => item.Identity).ToArray());
        Assert.All(s.Egress.TrackStarts, item => Assert.Equal(room, item.Room));
        Assert.Matches($"^/out/{id:N}-[0-9a-f]{{32}}-\\d+\\.mp4$", s.Egress.TrackStarts[0].Destination.FilePath);

        await JoinedAsync(s, room, s.Ben.Id.ToString("D"));                                                      // arrives later
        await JoinedAsync(s, room, s.Ben.Id.ToString("D"));                                                      // a second notice changes nothing
        Assert.Equal(3, s.Egress.TrackStarts.Count);

        Assert.Single(s.Egress.Starts);                                                                          // and the whole class is recorded once
    }

    [Fact]
    public async Task Without_separate_tracks_only_the_whole_class_is_recorded()
    {
        var s = await NewSetupAsync(tracks: false);
        var (id, room) = await ScheduleAsync(s);
        s.Rooms.People.Add(FakeRoomClient.Speaker(s.Ada.Id));
        await s.Teacher.Client.PostAsJsonAsync(Url(id, "consent"), new { granted = true });
        await s.Teacher.Client.PostAsync(Url(id, "recording/start"), null);
        await JoinedAsync(s, room, s.Ada.Id.ToString("D"));
        Assert.Empty(s.Egress.TrackStarts);
    }

    [Fact]
    public async Task Each_finished_track_becomes_a_course_file_the_teacher_can_open_and_stopping_the_class_recording_stops_them()
    {
        var s = await NewSetupAsync(tracks: true);
        var (id, room) = await ScheduleAsync(s);
        s.Rooms.People.AddRange([FakeRoomClient.Speaker(s.Teacher.Id), FakeRoomClient.Speaker(s.Ada.Id)]);
        await s.Teacher.Client.PostAsJsonAsync(Url(id, "consent"), new { granted = true });
        await s.Teacher.Client.PostAsync(Url(id, "recording/start"), null);

        var running = (await ReadAsync(await s.Teacher.Client.GetAsync(Url(id, "recording/tracks")))).EnumerateArray().ToList();
        Assert.Equal(2, running.Count);
        Assert.All(running, item => { Assert.Equal("Recording", item.GetProperty("status").GetString()); Assert.Equal(JsonValueKind.Null, item.GetProperty("downloadUrl").ValueKind); });
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Ada.Client.GetAsync(Url(id, "recording/tracks"))).StatusCode);

        await s.Teacher.Client.PostAsync(Url(id, "recording/stop"), null);
        Assert.Equal(["EG_1", "EGP_1", "EGP_2"], s.Egress.Stops);

        await s.Egress.CompleteTrackAsync("EGP_2", Mp4, seconds: 61.5);                                          // Ada's track is done; the other is still being finished
        await s.Egress.CompleteAsync("EG_1", Mp4);
        await SyncAsync(s);
        var tracks = (await ReadAsync(await s.Teacher.Client.GetAsync(Url(id, "recording/tracks")))).EnumerateArray().ToList();
        var ada = tracks.Single(item => item.GetProperty("userId").GetGuid() == s.Ada.Id);
        Assert.Equal(("Available", "Ada", 62, (long)Mp4.Length), (ada.GetProperty("status").GetString(), ada.GetProperty("displayName").GetString(), ada.GetProperty("durationSeconds").GetInt32(), ada.GetProperty("sizeBytes").GetInt64()));
        Assert.Equal("Processing", tracks.Single(item => item.GetProperty("userId").GetGuid() == s.Teacher.Id).GetProperty("status").GetString());

        var download = await s.Teacher.Client.GetAsync(ada.GetProperty("downloadUrl").GetString());
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(Mp4, await download.Content.ReadAsByteArrayAsync());
        Assert.DoesNotContain(Directory.GetFiles(shared), file => file.EndsWith(s.Egress.TrackStarts[1].Destination.FilePath.Split('/').Last()));   // the shared folder is cleaned up
    }

    [Fact]
    public async Task A_track_that_fails_says_why_and_does_not_disturb_the_class_recording()
    {
        var s = await NewSetupAsync(tracks: true);
        var (id, _) = await ScheduleAsync(s);
        s.Rooms.People.Add(FakeRoomClient.Speaker(s.Ada.Id));
        await s.Teacher.Client.PostAsJsonAsync(Url(id, "consent"), new { granted = true });
        await s.Teacher.Client.PostAsync(Url(id, "recording/start"), null);
        s.Egress.Set("EGP_1", "EGRESS_FAILED", "out of disk space");
        await SyncAsync(s);
        var track = Assert.Single((await ReadAsync(await s.Teacher.Client.GetAsync(Url(id, "recording/tracks")))).EnumerateArray());
        Assert.Equal("Failed", track.GetProperty("status").GetString());
        Assert.Equal("LiveKit could not finish the recording: out of disk space", track.GetProperty("lastError").GetString());
        Assert.Equal("Recording", (await RecordingAsync(s, id)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_refusal_when_starting_one_persons_track_does_not_stop_the_class_from_recording()
    {
        var s = await NewSetupAsync(tracks: true);
        var (id, room) = await ScheduleAsync(s);
        await s.Teacher.Client.PostAsJsonAsync(Url(id, "consent"), new { granted = true });
        await s.Teacher.Client.PostAsync(Url(id, "recording/start"), null);
        s.Egress.StartError = "LiveKit could not be reached.";
        Assert.Equal(HttpStatusCode.OK, (await JoinedAsync(s, room, s.Ada.Id.ToString("D"))).StatusCode);
        Assert.Empty((await ReadAsync(await s.Teacher.Client.GetAsync(Url(id, "recording/tracks")))).EnumerateArray());
        Assert.Equal("Recording", (await RecordingAsync(s, id)).GetProperty("status").GetString());
    }
}
