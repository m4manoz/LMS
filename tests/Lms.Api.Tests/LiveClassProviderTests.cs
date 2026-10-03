using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

/// <summary>Which tool an organization holds its live classes in, and what that changes: links, joining and recordings.</summary>
public sealed class LiveClassProviderTests : IClassFixture<LmsApiFactory>
{
    private const string Settings = "/api/v1/tenant/integrations/live-classes";
    private const string Sessions = "/api/v1/tenant/live-classes/sessions";
    private readonly TestWorld _world;

    public LiveClassProviderTests(LmsApiFactory factory) { _world = new TestWorld(factory); }

    private sealed record Setup(Tenant Tenant, Person Teacher, Person Ada, CourseInfo Course);

    private async Task<Setup> NewSetupAsync(string provider, string? jitsiBaseUrl = null)
    {
        var t = await _world.NewTenantAsync();
        var teacher = await _world.AddPersonAsync(t, "Tara", "TEACHER");
        var ada = await _world.AddLearnerAsync(t, "Ada");
        var course = await _world.NewCourseAsync(t, "LP-1");
        Assert.True((await EnrollAsync(ada, course)).IsSuccessStatusCode);
        var saved = await t.Admin.PutAsJsonAsync(Settings, new { provider, jitsiBaseUrl });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        return new Setup(t, teacher, ada, course);
    }

    private static Task<HttpResponseMessage> Schedule(Person teacher, Guid courseId, string? meetingUrl = null, string title = "Revision")
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        return teacher.Client.PostAsJsonAsync(Sessions, new { courseId, title, startAtUtc = start, endAtUtc = start.AddHours(1), meetingUrl });
    }

    private static async Task<JsonElement> ScheduledAsync(Person teacher, Guid courseId, string? meetingUrl = null, string title = "Revision")
    {
        var response = await Schedule(teacher, courseId, meetingUrl, title);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync(response);
    }

    private static string Url(Guid id, string tail = "") => $"{Sessions}/{id}{tail}";

    // ---------- the setting ----------
    [Fact]
    public async Task An_organization_starts_on_the_placeholder_and_only_administrators_can_change_it()
    {
        var t = await _world.NewTenantAsync();
        var teacher = await _world.AddPersonAsync(t, "Tara", "TEACHER");
        var defaults = await ReadAsync(await t.Admin.GetAsync(Settings));
        Assert.Equal("Local", defaults.GetProperty("provider").GetString());
        Assert.Equal("https://meet.jit.si", defaults.GetProperty("jitsiBaseUrl").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await teacher.Client.GetAsync(Settings)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await teacher.Client.PutAsJsonAsync(Settings, new { provider = "Manual" })).StatusCode);
    }

    [Fact]
    public async Task The_setting_checks_the_provider_and_the_jitsi_address()
    {
        var t = await _world.NewTenantAsync();
        Task<HttpResponseMessage> Put(object body) => t.Admin.PutAsJsonAsync(Settings, body);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(new { provider = "Skype" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(new { provider = "Jitsi", jitsiBaseUrl = "http://meet.example.org" })).StatusCode);              // not https
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(new { provider = "Jitsi", jitsiBaseUrl = "javascript:alert(1)" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(new { provider = "Jitsi", jitsiBaseUrl = "https://user:pass@meet.example.org" })).StatusCode);   // credentials in the address
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(new { provider = "Jitsi", jitsiBaseUrl = "https://meet.example.org/?x=1" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Put(new { provider = "jitsi" })).StatusCode);                                                                 // blank address means the public server
        var saved = await ReadAsync(await t.Admin.GetAsync(Settings));
        Assert.Equal("Jitsi", saved.GetProperty("provider").GetString());                                                                                    // name is tidied to its canonical form
        Assert.Equal("https://meet.jit.si/", saved.GetProperty("jitsiBaseUrl").GetString());
    }

    [Fact]
    public async Task Each_organization_has_its_own_setting()
    {
        var a = await _world.NewTenantAsync();
        var b = await _world.NewTenantAsync();
        await a.Admin.PutAsJsonAsync(Settings, new { provider = "Manual" });
        Assert.Equal("Local", (await ReadAsync(await b.Admin.GetAsync(Settings))).GetProperty("provider").GetString());
    }

    [Fact]
    public async Task The_schedule_form_can_ask_what_the_organization_uses()
    {
        foreach (var (provider, requiresLink, canRecord) in new[] { ("Local", false, true), ("Manual", true, false), ("Jitsi", false, false) })
        {
            var s = await NewSetupAsync(provider);
            var info = await ReadAsync(await s.Teacher.Client.GetAsync("/api/v1/tenant/live-classes/provider"));
            Assert.Equal(provider, info.GetProperty("provider").GetString());
            Assert.Equal(requiresLink, info.GetProperty("requiresMeetingLink").GetBoolean());
            Assert.Equal(canRecord, info.GetProperty("canRecord").GetBoolean());
        }
    }

    // ---------- manual links ----------
    [Fact]
    public async Task With_manual_links_the_teacher_pastes_the_meeting_and_everyone_is_sent_there()
    {
        var s = await NewSetupAsync("Manual");
        var link = "https://us02web.zoom.us/j/123456789?pwd=abc";
        var session = await ScheduledAsync(s.Teacher, s.Course.Id, link);
        var id = session.GetProperty("id").GetGuid();
        Assert.Equal("manual", session.GetProperty("provider").GetString());
        Assert.Equal(link, session.GetProperty("joinUrl").GetString());

        var learner = await ReadAsync(await s.Ada.Client.PostAsync(Url(id, "/join"), null));
        Assert.Equal(link, learner.GetProperty("meetingUrl").GetString());
        var host = await ReadAsync(await s.Teacher.Client.PostAsync(Url(id, "/join"), null));
        Assert.Equal(link, host.GetProperty("meetingUrl").GetString());
    }

    [Fact]
    public async Task A_manual_link_must_be_a_clean_https_address()
    {
        var s = await NewSetupAsync("Manual");
        foreach (var bad in new string?[] { null, "", "   ", "not a link", "http://meet.example.org/room", "javascript:alert(1)", "ftp://x.example.org/room", "https://user:pw@meet.example.org/room", "/relative/path", "https://" + new string('a', 2000) + ".org" })
        {
            var response = await Schedule(s.Teacher, s.Course.Id, bad);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Empty((await ReadAsync(await s.Teacher.Client.GetAsync(Sessions))).EnumerateArray());   // nothing was created
    }

    // ---------- jitsi ----------
    [Fact]
    public async Task With_jitsi_each_class_gets_its_own_hard_to_guess_room()
    {
        var s = await NewSetupAsync("Jitsi", "https://meet.school.example.org");
        var first = await ScheduledAsync(s.Teacher, s.Course.Id, title: "One");
        var second = await ScheduledAsync(s.Teacher, s.Course.Id, title: "Two");
        var url = first.GetProperty("joinUrl").GetString()!;
        Assert.Matches(@"^https://meet\.school\.example\.org/lms-[0-9a-f]{20}$", url);
        Assert.NotEqual(url, second.GetProperty("joinUrl").GetString());                                   // a room is never shared between classes
        Assert.DoesNotContain(first.GetProperty("id").GetString()!, url);                                   // and cannot be worked out from the class id
        Assert.Equal("jitsi", first.GetProperty("provider").GetString());
        Assert.Equal(url, first.GetProperty("hostUrl").GetString());

        var joined = await ReadAsync(await s.Ada.Client.PostAsync(Url(first.GetProperty("id").GetGuid(), "/join"), null));
        Assert.Equal(url, joined.GetProperty("meetingUrl").GetString());
        Assert.Equal("Present", joined.GetProperty("attendance").GetProperty("status").GetString());       // attendance is still kept here
    }

    [Fact]
    public async Task Jitsi_uses_the_public_server_when_no_address_is_set_and_ignores_a_pasted_link()
    {
        var s = await NewSetupAsync("Jitsi");
        var session = await ScheduledAsync(s.Teacher, s.Course.Id, meetingUrl: "https://elsewhere.example.org/ignored");
        Assert.StartsWith("https://meet.jit.si/lms-", session.GetProperty("joinUrl").GetString());
    }

    [Fact]
    public async Task Changing_the_provider_does_not_move_classes_that_already_exist()
    {
        var s = await NewSetupAsync("Jitsi");
        var session = await ScheduledAsync(s.Teacher, s.Course.Id);
        var url = session.GetProperty("joinUrl").GetString();
        await s.Tenant.Admin.PutAsJsonAsync(Settings, new { provider = "Manual" });
        var joined = await ReadAsync(await s.Ada.Client.PostAsync(Url(session.GetProperty("id").GetGuid(), "/join"), null));
        Assert.Equal(url, joined.GetProperty("meetingUrl").GetString());
    }

    // ---------- the placeholder ----------
    [Fact]
    public async Task The_placeholder_sends_the_host_to_the_host_link_and_learners_to_the_join_link()
    {
        var s = await NewSetupAsync("Local");
        var id = (await ScheduledAsync(s.Teacher, s.Course.Id)).GetProperty("id").GetGuid();
        var learner = await ReadAsync(await s.Ada.Client.PostAsync(Url(id, "/join"), null));
        var host = await ReadAsync(await s.Teacher.Client.PostAsync(Url(id, "/join"), null));
        Assert.DoesNotContain("host=1", learner.GetProperty("meetingUrl").GetString());
        Assert.Contains("host=1", host.GetProperty("meetingUrl").GetString());
    }

    // ---------- recordings made elsewhere ----------
    [Fact]
    public async Task Classes_held_in_another_tool_cannot_be_recorded_from_here()
    {
        foreach (var provider in new[] { "Manual", "Jitsi" })
        {
            var s = await NewSetupAsync(provider);
            var id = (await ScheduledAsync(s.Teacher, s.Course.Id, "https://meet.example.org/room")).GetProperty("id").GetGuid();
            await s.Teacher.Client.PostAsJsonAsync(Url(id, "/consent"), new { granted = true });
            var response = await s.Teacher.Client.PostAsync(Url(id, "/recording/request"), null);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("Record it there", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task A_recording_made_elsewhere_is_attached_by_link_once_the_host_has_agreed()
    {
        var s = await NewSetupAsync("Manual");
        var id = (await ScheduledAsync(s.Teacher, s.Course.Id, "https://meet.example.org/room")).GetProperty("id").GetGuid();
        var link = "https://us02web.zoom.us/rec/share/abc123";

        Assert.Equal(HttpStatusCode.Conflict, (await s.Teacher.Client.PostAsJsonAsync(Url(id, "/recording/link"), new { url = link })).StatusCode);   // no consent yet
        await s.Teacher.Client.PostAsJsonAsync(Url(id, "/consent"), new { granted = true });
        foreach (var bad in new[] { "", "http://x.example.org/rec", "javascript:alert(1)", "not a link" })
            Assert.Equal(HttpStatusCode.BadRequest, (await s.Teacher.Client.PostAsJsonAsync(Url(id, "/recording/link"), new { url = bad })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Ada.Client.PostAsJsonAsync(Url(id, "/recording/link"), new { url = link })).StatusCode);          // learners cannot attach one

        var attached = await ReadAsync(await s.Teacher.Client.PostAsJsonAsync(Url(id, "/recording/link"), new { url = link }));
        Assert.Equal("Available", attached.GetProperty("status").GetString());
        Assert.Equal(link, attached.GetProperty("recordingUrl").GetString());
        Assert.NotEqual(JsonValueKind.Null, attached.GetProperty("retainUntilUtc").ValueKind);

        var seen = await ReadAsync(await s.Ada.Client.GetAsync(Url(id, "/recording")));      // enrolled learners can watch it
        Assert.Equal(link, seen.GetProperty("recordingUrl").GetString());

        var replaced = await ReadAsync(await s.Teacher.Client.PostAsJsonAsync(Url(id, "/recording/link"), new { url = "https://us02web.zoom.us/rec/share/new" }));   // a better link replaces it
        Assert.Equal("https://us02web.zoom.us/rec/share/new", replaced.GetProperty("recordingUrl").GetString());
    }

    [Fact]
    public async Task People_outside_the_course_cannot_attach_or_read_a_recording_link()
    {
        var s = await NewSetupAsync("Manual");
        var outsider = await _world.AddLearnerAsync(s.Tenant, "Olga");
        var id = (await ScheduledAsync(s.Teacher, s.Course.Id, "https://meet.example.org/room")).GetProperty("id").GetGuid();
        await s.Teacher.Client.PostAsJsonAsync(Url(id, "/consent"), new { granted = true });
        await s.Teacher.Client.PostAsJsonAsync(Url(id, "/recording/link"), new { url = "https://zoom.example.org/rec/1" });
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.Client.GetAsync(Url(id, "/recording"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.Client.PostAsJsonAsync(Url(id, "/recording/link"), new { url = "https://zoom.example.org/rec/2" })).StatusCode);
    }
}
