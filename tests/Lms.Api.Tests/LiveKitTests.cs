using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lms.Api.Infrastructure.LiveClasses;
using Microsoft.EntityFrameworkCore;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

/// <summary>Classes held in a LiveKit room inside the app: the settings, who gets a token, and what the token allows.</summary>
public sealed class LiveKitTests : IClassFixture<LmsApiFactory>
{
    private const string Settings = "/api/v1/tenant/integrations/live-classes";
    private const string Sessions = "/api/v1/tenant/live-classes/sessions";
    private const string Secret = "a-long-livekit-api-secret-for-tests-0123456789";
    private readonly TestWorld _world;

    public LiveKitTests(LmsApiFactory factory) { _world = new TestWorld(factory); }

    private static object LiveKit(string? secret = Secret, string url = "wss://classes.example.org", string key = "APIkey123") => new { provider = "LiveKit", liveKitUrl = url, liveKitApiKey = key, liveKitApiSecret = secret };

    private sealed record Setup(Tenant Tenant, Person Teacher, Person Ada, Person Outsider, CourseInfo Course);

    private async Task<Setup> NewSetupAsync()
    {
        var t = await _world.NewTenantAsync();
        var teacher = await _world.AddPersonAsync(t, "Tara", "TEACHER");
        var ada = await _world.AddLearnerAsync(t, "Ada");
        var outsider = await _world.AddLearnerAsync(t, "Olga");
        var course = await _world.NewCourseAsync(t, "LK-1");
        Assert.True((await EnrollAsync(ada, course)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.OK, (await t.Admin.PutAsJsonAsync(Settings, LiveKit())).StatusCode);
        return new Setup(t, teacher, ada, outsider, course);
    }

    private static async Task<Guid> ScheduleAsync(Setup s, string title = "Algebra")
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        var response = await s.Teacher.Client.PostAsJsonAsync(Sessions, new { courseId = s.Course.Id, title, startAtUtc = start, endAtUtc = start.AddHours(1) });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    private static string Url(Guid id, string tail = "") => $"{Sessions}/{id}{tail}";

    /// <summary>Checks a LiveKit token's signature with the secret and returns its claims.</summary>
    private static JsonElement Verify(string token, string secret)
    {
        var parts = token.Split('.');
        Assert.Equal(3, parts.Length);
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"));
        Assert.Equal(Convert.ToBase64String(expected).TrimEnd('=').Replace('+', '-').Replace('/', '_'), parts[2]);
        return JsonDocument.Parse(Decode(parts[1])).RootElement.Clone();
    }

    private static byte[] Decode(string part)
    {
        var padded = part.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded + new string('=', (4 - padded.Length % 4) % 4));
    }

    // ---------- the token ----------
    [Fact]
    public void A_token_is_signed_with_the_secret_and_names_the_person_the_room_and_what_they_may_do()
    {
        var now = DateTimeOffset.Parse("2026-10-10T10:00:00Z");
        var token = LiveKitTokens.Create(new LiveKitCredentials("wss://x", "APIkey123", Secret), "lms-room1", "user-1", "Ada Learner", canPublish: true, roomAdmin: false, TimeSpan.FromHours(2), now);
        var claims = Verify(token, Secret);
        Assert.Equal("APIkey123", claims.GetProperty("iss").GetString());
        Assert.Equal("user-1", claims.GetProperty("sub").GetString());
        Assert.Equal("Ada Learner", claims.GetProperty("name").GetString());
        Assert.Equal(now.AddHours(2).ToUnixTimeSeconds(), claims.GetProperty("exp").GetInt64());
        Assert.True(claims.GetProperty("nbf").GetInt64() <= now.ToUnixTimeSeconds());
        var video = claims.GetProperty("video");
        Assert.Equal("lms-room1", video.GetProperty("room").GetString());
        Assert.True(video.GetProperty("roomJoin").GetBoolean());
        Assert.True(video.GetProperty("canPublish").GetBoolean());
        Assert.True(video.GetProperty("canSubscribe").GetBoolean());
        Assert.False(video.GetProperty("roomAdmin").GetBoolean());
    }

    [Fact]
    public void A_token_made_with_another_secret_does_not_verify_and_a_host_token_is_an_admin_token()
    {
        var credentials = new LiveKitCredentials("wss://x", "k", Secret);
        var token = LiveKitTokens.Create(credentials, "room", "u", "Tara", true, roomAdmin: true, TimeSpan.FromHours(1));
        Assert.True(Verify(token, Secret).GetProperty("video").GetProperty("roomAdmin").GetBoolean());
        Assert.ThrowsAny<Exception>(() => Verify(token, "some-other-secret"));
    }

    // ---------- the setting ----------
    [Fact]
    public async Task LiveKit_needs_a_server_address_a_key_and_a_secret()
    {
        var t = await _world.NewTenantAsync();
        Task<HttpResponseMessage> Put(object body) => t.Admin.PutAsJsonAsync(Settings, body);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(new { provider = "LiveKit" })).StatusCode);
        foreach (var bad in new[] { "", "not a link", "ws://classes.example.org", "http://classes.example.org", "javascript:alert(1)", "wss://user:pw@classes.example.org", "wss://classes.example.org/?x=1" })
            Assert.Equal(HttpStatusCode.BadRequest, (await Put(LiveKit(url: bad))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(LiveKit(key: " "))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(LiveKit(secret: null))).StatusCode);      // the first save needs a secret
        Assert.Equal(HttpStatusCode.OK, (await Put(LiveKit(url: "wss://classes.example.org/"))).StatusCode);
        Assert.Equal("wss://classes.example.org", (await ReadAsync(await t.Admin.GetAsync(Settings))).GetProperty("liveKitUrl").GetString());   // tidied
    }

    [Fact]
    public async Task A_test_server_on_this_machine_may_use_ws_while_developing_but_no_other_host_may()
    {
        var t = await _world.NewTenantAsync();
        Assert.Equal(HttpStatusCode.OK, (await t.Admin.PutAsJsonAsync(Settings, LiveKit(url: "ws://localhost:7880"))).StatusCode);
        Assert.Equal("ws://localhost:7880", (await ReadAsync(await t.Admin.GetAsync(Settings))).GetProperty("liveKitUrl").GetString());
        Assert.Equal(HttpStatusCode.OK, (await t.Admin.PutAsJsonAsync(Settings, LiveKit(url: "ws://127.0.0.1:7880"))).StatusCode);
        foreach (var other in new[] { "ws://192.168.1.5:7880", "ws://classes.example.org", "http://example.org" })
            Assert.Equal(HttpStatusCode.BadRequest, (await t.Admin.PutAsJsonAsync(Settings, LiveKit(url: other))).StatusCode);
    }

    [Fact]
    public async Task The_secret_is_never_returned_and_is_not_stored_in_plain_text()
    {
        var t = await _world.NewTenantAsync();
        await t.Admin.PutAsJsonAsync(Settings, LiveKit());
        var response = await t.Admin.GetAsync(Settings);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Secret, body);
        var saved = JsonDocument.Parse(body).RootElement;
        Assert.True(saved.GetProperty("liveKitSecretSet").GetBoolean());
        Assert.Equal("APIkey123", saved.GetProperty("liveKitApiKey").GetString());

        await _world.WithDbAsync(t.Slug, async db =>
        {
            var row = await db.LiveClassSettings.SingleAsync();
            Assert.False(string.IsNullOrEmpty(row.LiveKitSecretProtected));
            Assert.DoesNotContain(Secret, row.LiveKitSecretProtected);
        });
    }

    [Fact]
    public async Task Leaving_the_secret_blank_keeps_the_saved_one_and_switching_away_and_back_does_too()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s);
        Assert.Equal(HttpStatusCode.OK, (await s.Tenant.Admin.PutAsJsonAsync(Settings, LiveKit(secret: null, url: "wss://other.example.org"))).StatusCode);   // new address, same secret
        var token = (await ReadAsync(await s.Ada.Client.PostAsync(Url(id, "/join"), null))).GetProperty("liveKit");
        Assert.Equal("wss://other.example.org", token.GetProperty("url").GetString());
        Verify(token.GetProperty("token").GetString()!, Secret);

        await s.Tenant.Admin.PutAsJsonAsync(Settings, new { provider = "Manual" });
        Assert.Equal(HttpStatusCode.OK, (await s.Tenant.Admin.PutAsJsonAsync(Settings, LiveKit(secret: null))).StatusCode);
        Verify((await ReadAsync(await s.Ada.Client.PostAsync(Url(id, "/join"), null))).GetProperty("liveKit").GetProperty("token").GetString()!, Secret);
    }

    [Fact]
    public async Task Only_administrators_can_change_the_livekit_settings()
    {
        var s = await NewSetupAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Teacher.Client.PutAsJsonAsync(Settings, LiveKit())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Teacher.Client.GetAsync(Settings)).StatusCode);
    }

    // ---------- scheduling ----------
    [Fact]
    public async Task A_livekit_class_gets_its_own_room_and_asks_for_no_link()
    {
        var s = await NewSetupAsync();
        var info = await ReadAsync(await s.Teacher.Client.GetAsync("/api/v1/tenant/live-classes/provider"));
        Assert.Equal("LiveKit", info.GetProperty("provider").GetString());
        Assert.False(info.GetProperty("requiresMeetingLink").GetBoolean());

        var first = await ReadAsync(await s.Teacher.Client.PostAsJsonAsync(Sessions, new { courseId = s.Course.Id, title = "One", startAtUtc = DateTimeOffset.UtcNow.AddHours(1), endAtUtc = DateTimeOffset.UtcNow.AddHours(2) }));
        var second = await ReadAsync(await s.Teacher.Client.PostAsJsonAsync(Sessions, new { courseId = s.Course.Id, title = "Two", startAtUtc = DateTimeOffset.UtcNow.AddHours(1), endAtUtc = DateTimeOffset.UtcNow.AddHours(2) }));
        Assert.Equal("livekit", first.GetProperty("provider").GetString());
        Assert.Matches("^lms-[0-9a-f]{20}$", first.GetProperty("providerMeetingId").GetString()!);
        Assert.NotEqual(first.GetProperty("providerMeetingId").GetString(), second.GetProperty("providerMeetingId").GetString());
        Assert.Contains($"liveSession={first.GetProperty("id").GetGuid()}", first.GetProperty("joinUrl").GetString());   // the link opens the class inside the app
    }

    // ---------- joining ----------
    [Fact]
    public async Task A_learner_gets_a_token_for_the_classs_room_and_the_teacher_gets_an_admin_token()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s);
        var room = (await ReadAsync(await s.Teacher.Client.GetAsync(Sessions))).EnumerateArray().Single().GetProperty("providerMeetingId").GetString();

        var learner = await ReadAsync(await s.Ada.Client.PostAsync(Url(id, "/join"), null));
        var join = learner.GetProperty("liveKit");
        Assert.Equal("wss://classes.example.org", join.GetProperty("url").GetString());
        Assert.Equal(room, join.GetProperty("room").GetString());
        Assert.False(join.GetProperty("isHost").GetBoolean());
        var claims = Verify(join.GetProperty("token").GetString()!, Secret);
        Assert.Equal(s.Ada.Id.ToString(), claims.GetProperty("sub").GetString());
        Assert.Equal("Ada", claims.GetProperty("name").GetString());
        Assert.Equal(room, claims.GetProperty("video").GetProperty("room").GetString());
        Assert.False(claims.GetProperty("video").GetProperty("roomAdmin").GetBoolean());
        Assert.Equal("Present", learner.GetProperty("attendance").GetProperty("status").GetString());

        var host = (await ReadAsync(await s.Teacher.Client.PostAsync(Url(id, "/join"), null))).GetProperty("liveKit");
        Assert.True(host.GetProperty("isHost").GetBoolean());
        Assert.True(Verify(host.GetProperty("token").GetString()!, Secret).GetProperty("video").GetProperty("roomAdmin").GetBoolean());
        Assert.NotEqual(join.GetProperty("token").GetString(), host.GetProperty("token").GetString());          // every person has their own
    }

    [Fact]
    public async Task People_outside_the_course_get_no_token()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s);
        var response = await s.Outsider.Client.PostAsync(Url(id, "/join"), null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("token", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_closed_class_gives_no_token_and_incomplete_settings_stop_a_join_before_attendance_is_recorded()
    {
        var s = await NewSetupAsync();
        var closedId = await ScheduleAsync(s, "Closed");
        await s.Teacher.Client.PostAsync(Url(closedId, "/close"), null);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Ada.Client.PostAsync(Url(closedId, "/join"), null)).StatusCode);

        var id = await ScheduleAsync(s, "Open");
        await _world.WithDbAsync(s.Tenant.Slug, async db =>
        {
            var row = await db.LiveClassSettings.SingleAsync();
            row.LiveKitSecretProtected = null;                              // the secret is lost
            await db.SaveChangesAsync();
        });
        var refused = await s.Ada.Client.PostAsync(Url(id, "/join"), null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("LiveKit settings are incomplete", await refused.Content.ReadAsStringAsync());
        Assert.Empty((await ReadAsync(await s.Teacher.Client.GetAsync(Url(id, "/attendance")))).EnumerateArray());   // nobody was recorded as present
    }

    [Fact]
    public async Task Classes_in_other_tools_return_no_livekit_details()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s);                                   // made while LiveKit was chosen
        await s.Tenant.Admin.PutAsJsonAsync(Settings, new { provider = "Jitsi" });
        var jitsi = await s.Teacher.Client.PostAsJsonAsync(Sessions, new { courseId = s.Course.Id, title = "Jitsi class", startAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5), endAtUtc = DateTimeOffset.UtcNow.AddHours(1) });
        var jitsiId = (await ReadAsync(jitsi)).GetProperty("id").GetGuid();
        var joined = await ReadAsync(await s.Ada.Client.PostAsync(Url(jitsiId, "/join"), null));
        Assert.Equal(JsonValueKind.Null, joined.GetProperty("liveKit").ValueKind);
        Assert.StartsWith("https://meet.jit.si/", joined.GetProperty("meetingUrl").GetString());
        // The LiveKit class made earlier still joins its own room.
        Assert.Equal(JsonValueKind.Object, (await ReadAsync(await s.Ada.Client.PostAsync(Url(id, "/join"), null))).GetProperty("liveKit").ValueKind);
    }

    // ---------- recordings ----------
    [Fact]
    public async Task A_livekit_recording_is_attached_by_link_for_now()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s);
        await s.Teacher.Client.PostAsJsonAsync(Url(id, "/consent"), new { granted = true });
        Assert.Equal(HttpStatusCode.Conflict, (await s.Teacher.Client.PostAsync(Url(id, "/recording/request"), null)).StatusCode);
        var attached = await s.Teacher.Client.PostAsJsonAsync(Url(id, "/recording/link"), new { url = "https://storage.example.org/recordings/1.mp4" });
        Assert.Equal(HttpStatusCode.OK, attached.StatusCode);
        Assert.Equal("Available", (await ReadAsync(attached)).GetProperty("status").GetString());
    }
}
