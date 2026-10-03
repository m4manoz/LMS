using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

/// <summary>A class with a waiting room: learners ask to come in and the host decides.</summary>
public sealed class LiveClassWaitingRoomTests : IClassFixture<LmsApiFactory>
{
    private const string Sessions = "/api/v1/tenant/live-classes/sessions";
    private readonly TestWorld _world;
    public LiveClassWaitingRoomTests(LmsApiFactory factory) { _world = new TestWorld(factory); }

    private sealed record Setup(Tenant Tenant, Person Teacher, Person Ada, Person Ben, Person Outsider, CourseInfo Course);

    private async Task<Setup> NewSetupAsync()
    {
        var t = await _world.NewTenantAsync();
        var teacher = await _world.AddPersonAsync(t, "Tara", "TEACHER");
        var ada = await _world.AddLearnerAsync(t, "Ada");
        var ben = await _world.AddLearnerAsync(t, "Ben");
        var outsider = await _world.AddLearnerAsync(t, "Olga");
        var course = await _world.NewCourseAsync(t, "WAIT-1");
        Assert.True((await EnrollAsync(ada, course)).IsSuccessStatusCode);
        Assert.True((await EnrollAsync(ben, course)).IsSuccessStatusCode);
        return new Setup(t, teacher, ada, ben, outsider, course);
    }

    private static async Task<Guid> ScheduleAsync(Setup s, bool requireApproval, string title = "Algebra")
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        var response = await s.Teacher.Client.PostAsJsonAsync(Sessions, new { courseId = s.Course.Id, title, startAtUtc = start, endAtUtc = start.AddHours(1), requireApproval });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal(requireApproval, body.GetProperty("requireApproval").GetBoolean());
        return body.GetProperty("id").GetGuid();
    }

    private static string Url(Guid id, string tail) => $"{Sessions}/{id}/{tail}";
    private static Task<HttpResponseMessage> Join(Person who, Guid id) => who.Client.PostAsync(Url(id, "join"), null);
    private static async Task<string> StatusAsync(Person who, Guid id) => (await ReadAsync(await who.Client.GetAsync(Url(id, "join-status")))).GetProperty("status").GetString()!;
    private static async Task<List<JsonElement>> WaitingAsync(Person staff, Guid id) => (await ReadAsync(await staff.Client.GetAsync(Url(id, "join-requests")))).EnumerateArray().ToList();
    private static async Task<int> AttendanceCountAsync(Person staff, Guid id) => (await ReadAsync(await staff.Client.GetAsync(Url(id, "attendance")))).GetArrayLength();

    [Fact]
    public async Task A_class_without_a_waiting_room_lets_enrolled_learners_straight_in()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s, requireApproval: false);
        Assert.Equal("NotRequired", await StatusAsync(s.Ada, id));
        Assert.Equal(HttpStatusCode.OK, (await Join(s.Ada, id)).StatusCode);
        Assert.Empty(await WaitingAsync(s.Teacher, id));
    }

    [Fact]
    public async Task With_a_waiting_room_a_learner_waits_and_is_not_counted_present_while_the_host_and_staff_go_straight_in()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s, requireApproval: true);

        var waiting = await Join(s.Ada, id);
        Assert.Equal(HttpStatusCode.Accepted, waiting.StatusCode);
        var body = await ReadAsync(waiting);
        Assert.Equal("Waiting", body.GetProperty("status").GetString());
        Assert.False(body.TryGetProperty("attendance", out _));
        Assert.Equal("Waiting", await StatusAsync(s.Ada, id));
        Assert.Equal(HttpStatusCode.Accepted, (await Join(s.Ada, id)).StatusCode);                     // asking again changes nothing
        Assert.Single(await WaitingAsync(s.Teacher, id));
        Assert.Equal(0, await AttendanceCountAsync(s.Teacher, id));                                      // waiting is not attending

        Assert.Equal(HttpStatusCode.OK, (await Join(s.Teacher, id)).StatusCode);                         // the host does not wait
        Assert.Equal(HttpStatusCode.OK, (await s.Tenant.Admin.PostAsync(Url(id, "join"), null)).StatusCode);   // nor does staff who manage classes
        Assert.Equal("NotRequired", await StatusAsync(s.Teacher, id));
    }

    [Fact]
    public async Task The_host_admits_a_learner_who_is_then_let_in_and_stays_admitted()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s, requireApproval: true);
        await Join(s.Ada, id);
        var entry = Assert.Single(await WaitingAsync(s.Teacher, id));
        Assert.Equal("Ada", entry.GetProperty("userName").GetString());
        Assert.Equal(s.Ada.Id, entry.GetProperty("userId").GetGuid());

        Assert.Equal(HttpStatusCode.NoContent, (await s.Teacher.Client.PostAsync(Url(id, $"join-requests/{s.Ada.Id}/admit"), null)).StatusCode);
        Assert.Equal("Admitted", await StatusAsync(s.Ada, id));
        Assert.Empty(await WaitingAsync(s.Teacher, id));
        var joined = await Join(s.Ada, id);
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        Assert.Equal("Present", (await ReadAsync(joined)).GetProperty("attendance").GetProperty("status").GetString());

        await s.Ada.Client.PostAsync(Url(id, "leave"), null);
        Assert.Equal(HttpStatusCode.OK, (await Join(s.Ada, id)).StatusCode);                             // coming back later needs no new approval
    }

    [Fact]
    public async Task A_declined_learner_is_told_so_until_the_host_changes_their_mind()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s, requireApproval: true);
        await Join(s.Ada, id);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Teacher.Client.PostAsync(Url(id, $"join-requests/{s.Ada.Id}/decline"), null)).StatusCode);
        var refused = await Join(s.Ada, id);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("did not let you", await refused.Content.ReadAsStringAsync());
        Assert.Equal("Declined", await StatusAsync(s.Ada, id));
        Assert.Empty(await WaitingAsync(s.Teacher, id));
        Assert.Equal(0, await AttendanceCountAsync(s.Teacher, id));

        await s.Teacher.Client.PostAsync(Url(id, $"join-requests/{s.Ada.Id}/admit"), null);
        Assert.Equal(HttpStatusCode.OK, (await Join(s.Ada, id)).StatusCode);
    }

    [Fact]
    public async Task Admit_all_lets_everyone_waiting_in_at_once()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s, requireApproval: true);
        await Join(s.Ada, id); await Join(s.Ben, id);
        Assert.Equal(2, (await WaitingAsync(s.Teacher, id)).Count);
        Assert.Equal(2, (await ReadAsync(await s.Teacher.Client.PostAsync(Url(id, "join-requests/admit-all"), null))).GetProperty("admitted").GetInt32());
        Assert.Empty(await WaitingAsync(s.Teacher, id));
        Assert.Equal(HttpStatusCode.OK, (await Join(s.Ada, id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Join(s.Ben, id)).StatusCode);
        Assert.Equal(0, (await ReadAsync(await s.Teacher.Client.PostAsync(Url(id, "join-requests/admit-all"), null))).GetProperty("admitted").GetInt32());
    }

    [Fact]
    public async Task Only_the_host_and_staff_decide_and_outsiders_and_other_organizations_have_no_part()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s, requireApproval: true);
        await Join(s.Ada, id);
        foreach (var tail in new[] { "admit-all", $"{s.Ada.Id}/admit", $"{s.Ada.Id}/decline" })
            Assert.Equal(HttpStatusCode.Forbidden, (await s.Ben.Client.PostAsync(Url(id, $"join-requests/{tail}"), null)).StatusCode);     // a classmate cannot let people in
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Ben.Client.GetAsync(Url(id, "join-requests"))).StatusCode);
        Assert.Equal("Waiting", await StatusAsync(s.Ada, id));

        Assert.Equal(HttpStatusCode.Forbidden, (await Join(s.Outsider, id)).StatusCode);                     // not in the course
        Assert.Equal(HttpStatusCode.NotFound, (await s.Outsider.Client.GetAsync(Url(id, "join-status"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Teacher.Client.PostAsync(Url(id, $"join-requests/{Guid.NewGuid()}/admit"), null)).StatusCode);

        var other = await _world.NewTenantAsync();
        var stranger = await _world.AddPersonAsync(other, "Olivia", "TEACHER");
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.Client.PostAsync(Url(id, $"join-requests/{s.Ada.Id}/admit"), null)).StatusCode);
        Assert.Equal("Waiting", await StatusAsync(s.Ada, id));
    }

    [Fact]
    public async Task A_closed_class_cannot_be_joined_by_anyone_and_says_so_while_waiting()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s, requireApproval: true);
        await Join(s.Ada, id);
        await s.Teacher.Client.PostAsync(Url(id, "close"), null);
        Assert.Equal(HttpStatusCode.Conflict, (await Join(s.Ada, id)).StatusCode);
        Assert.True((await ReadAsync(await s.Ada.Client.GetAsync(Url(id, "join-status")))).GetProperty("closed").GetBoolean());
    }

    [Fact]
    public async Task In_a_LiveKit_class_no_room_token_is_handed_out_until_the_learner_is_admitted()
    {
        var s = await NewSetupAsync();
        Assert.Equal(HttpStatusCode.OK, (await s.Tenant.Admin.PutAsJsonAsync("/api/v1/tenant/integrations/live-classes", new { provider = "LiveKit", liveKitUrl = "wss://classes.example.org", liveKitApiKey = "APIkey123", liveKitApiSecret = "a-long-livekit-api-secret-for-tests-0123456789" })).StatusCode);
        var id = await ScheduleAsync(s, requireApproval: true);
        var waiting = await Join(s.Ada, id);
        Assert.Equal(HttpStatusCode.Accepted, waiting.StatusCode);
        Assert.DoesNotContain("token", await waiting.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        await s.Teacher.Client.PostAsync(Url(id, $"join-requests/{s.Ada.Id}/admit"), null);
        var admitted = await ReadAsync(await Join(s.Ada, id));
        Assert.False(admitted.GetProperty("liveKit").GetProperty("isHost").GetBoolean());
        Assert.False(string.IsNullOrEmpty(admitted.GetProperty("liveKit").GetProperty("token").GetString()));
    }

    [Fact]
    public async Task The_live_update_stream_treats_a_request_to_join_as_news_so_the_hosts_screen_shows_it()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s, requireApproval: true);
        using var stream = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var request = new HttpRequestMessage(HttpMethod.Get, Url(id, "events"));
        using var response = await s.Teacher.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stream.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(stream.Token));
        for (var i = 0; i < 4; i++) await reader.ReadLineAsync(stream.Token);                            // the first heartbeat
        await Join(s.Ada, id);
        var seen = new List<string>();
        while (!seen.Contains("event: refresh") && !stream.IsCancellationRequested) seen.Add((await reader.ReadLineAsync(stream.Token)) ?? "");
        Assert.Contains("event: refresh", seen);
    }
}
