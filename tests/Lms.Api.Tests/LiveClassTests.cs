using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

/// <summary>Live classes: scheduling, who can see and join them, attendance, announcements, chat, polls, hands and recording.</summary>
public sealed class LiveClassTests : IClassFixture<LmsApiFactory>
{
    private const string Sessions = "/api/v1/tenant/live-classes/sessions";
    private readonly TestWorld _world;

    public LiveClassTests(LmsApiFactory factory) { _world = new TestWorld(factory); }

    /// <summary>An organization with a teacher, an enrolled learner, a learner who is not enrolled, and a published course.</summary>
    private sealed record Setup(Tenant Tenant, Person Teacher, Person Ada, Person Outsider, CourseInfo Course);

    private async Task<Setup> NewSetupAsync()
    {
        var t = await _world.NewTenantAsync();
        var teacher = await _world.AddPersonAsync(t, "Tara", "TEACHER");
        var ada = await _world.AddLearnerAsync(t, "Ada");
        var outsider = await _world.AddLearnerAsync(t, "Olga");
        var course = await _world.NewCourseAsync(t, "LIVE-1");
        Assert.True((await EnrollAsync(ada, course)).IsSuccessStatusCode);
        return new Setup(t, teacher, ada, outsider, course);
    }

    /// <summary>Schedules a class as the teacher. By default it started five minutes ago, so joining makes it Live.</summary>
    private static async Task<Guid> ScheduleAsync(Person teacher, Guid? courseId, string title = "Algebra revision", int startOffsetMinutes = -5)
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(startOffsetMinutes);
        var response = await teacher.Client.PostAsJsonAsync(Sessions, new { courseId, title, description = "Weekly revision", startAtUtc = start, endAtUtc = start.AddHours(1) });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    private static string Url(Guid id, string tail = "") => $"{Sessions}/{id}{tail}";

    private static async Task<JsonElement> ListAsync(Person person) => await ReadAsync(await person.Client.GetAsync(Sessions));
    private static bool Contains(JsonElement list, Guid id) => list.EnumerateArray().Any(item => item.GetProperty("id").GetGuid() == id);

    // ---------- scheduling ----------
    [Fact]
    public async Task A_teacher_schedules_a_class_and_it_starts_out_scheduled_with_join_links()
    {
        var s = await NewSetupAsync();
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var response = await s.Teacher.Client.PostAsJsonAsync(Sessions, new { courseId = s.Course.Id, title = "  Calculus lab  ", description = "Bring a calculator", startAtUtc = start, endAtUtc = start.AddHours(2) });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal("Calculus lab", body.GetProperty("title").GetString());   // trimmed
        Assert.Equal("Scheduled", body.GetProperty("status").GetString());
        Assert.Equal("local", body.GetProperty("provider").GetString());
        var id = body.GetProperty("id").GetGuid();
        Assert.Contains(id.ToString(), body.GetProperty("joinUrl").GetString());
        Assert.Contains("host=1", body.GetProperty("hostUrl").GetString());
        Assert.Equal(s.Teacher.Id, body.GetProperty("hostUserId").GetGuid());
    }

    [Fact]
    public async Task Scheduling_checks_the_title_the_times_the_course_and_the_permission()
    {
        var s = await NewSetupAsync();
        var start = DateTimeOffset.UtcNow.AddDays(1);
        Task<HttpResponseMessage> Post(object body, Person? who = null) => (who ?? s.Teacher).Client.PostAsJsonAsync(Sessions, body);

        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { title = " ", startAtUtc = start, endAtUtc = start.AddHours(1) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { title = new string('x', 251), startAtUtc = start, endAtUtc = start.AddHours(1) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { title = "Backwards", startAtUtc = start, endAtUtc = start.AddHours(-1) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { title = "Zero length", startAtUtc = start, endAtUtc = start })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(new { courseId = Guid.NewGuid(), title = "No such course", startAtUtc = start, endAtUtc = start.AddHours(1) })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(new { title = "Sneaky", startAtUtc = start, endAtUtc = start.AddHours(1) }, s.Ada)).StatusCode);   // learners cannot schedule
    }

    // ---------- who can see it ----------
    [Fact]
    public async Task Learners_see_only_classes_of_courses_they_are_enrolled_in()
    {
        var s = await NewSetupAsync();
        var forCourse = await ScheduleAsync(s.Teacher, s.Course.Id, "For the course");
        var noCourse = await ScheduleAsync(s.Teacher, null, "Staff only");

        var ada = await ListAsync(s.Ada);
        Assert.True(Contains(ada, forCourse));
        Assert.False(Contains(ada, noCourse));                       // a class without a course is for staff and its host only
        Assert.False(Contains(await ListAsync(s.Outsider), forCourse));
        var teacher = await ListAsync(s.Teacher);
        Assert.True(Contains(teacher, forCourse) && Contains(teacher, noCourse));
        Assert.True(Contains(await ReadAsync(await s.Tenant.Admin.GetAsync(Sessions)), forCourse));   // an administrator sees every class
    }

    [Fact]
    public async Task A_learner_who_withdraws_stops_seeing_and_joining_the_class()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s.Teacher, s.Course.Id);
        var enrollments = await ReadAsync(await s.Ada.Client.GetAsync("/api/v1/tenant/enrollments"));
        var enrollmentId = enrollments.EnumerateArray().Single().GetProperty("id").GetGuid();
        Assert.True((await s.Ada.Client.PostAsync($"/api/v1/tenant/enrollments/{enrollmentId}/withdraw", null)).IsSuccessStatusCode);

        Assert.False(Contains(await ListAsync(s.Ada), id));
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Ada.Client.PostAsync(Url(id, "/join"), null)).StatusCode);
    }

    // ---------- joining and attendance ----------
    [Fact]
    public async Task Joining_a_class_that_has_started_makes_it_live_and_records_attendance()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s.Teacher, s.Course.Id);

        var joined = await s.Ada.Client.PostAsync(Url(id, "/join"), null);
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        var body = await ReadAsync(joined);
        Assert.Equal("Live", body.GetProperty("session").GetProperty("status").GetString());
        Assert.Contains(id.ToString(), body.GetProperty("meetingUrl").GetString());
        Assert.Equal("Present", body.GetProperty("attendance").GetProperty("status").GetString());

        var attendance = await ReadAsync(await s.Teacher.Client.GetAsync(Url(id, "/attendance")));
        var row = Assert.Single(attendance.EnumerateArray());
        Assert.Equal("Ada", row.GetProperty("userName").GetString());
        Assert.Equal("Present", row.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Joining_before_the_start_does_not_make_it_live()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s.Teacher, s.Course.Id, "Tomorrow", startOffsetMinutes: 24 * 60);
        var joined = await ReadAsync(await s.Ada.Client.PostAsync(Url(id, "/join"), null));
        Assert.Equal("Scheduled", joined.GetProperty("session").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Leaving_records_the_time_and_rejoining_brings_the_person_back()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s.Teacher, s.Course.Id);
        await s.Ada.Client.PostAsync(Url(id, "/join"), null);

        var left = await ReadAsync(await s.Ada.Client.PostAsync(Url(id, "/leave"), null));
        Assert.Equal("Left", left.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, left.GetProperty("leftAtUtc").ValueKind);
        Assert.True(left.GetProperty("durationSeconds").GetInt32() >= 0);

        var again = await ReadAsync(await s.Ada.Client.PostAsync(Url(id, "/join"), null));
        Assert.Equal("Present", again.GetProperty("attendance").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, again.GetProperty("attendance").GetProperty("leftAtUtc").ValueKind);
        Assert.Single((await ReadAsync(await s.Teacher.Client.GetAsync(Url(id, "/attendance")))).EnumerateArray());   // still one row per person
    }

    [Fact]
    public async Task Leaving_without_having_joined_is_not_found()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s.Teacher, s.Course.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Ada.Client.PostAsync(Url(id, "/leave"), null)).StatusCode);
    }

    [Fact]
    public async Task People_outside_the_course_cannot_join_or_read_anything_about_the_class()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s.Teacher, s.Course.Id);
        foreach (var (method, tail) in new[] { ("POST", "/join"), ("GET", "/attendance"), ("GET", "/announcements"), ("GET", "/chat"), ("GET", "/polls"), ("GET", "/recording") })
        {
            var response = method == "POST" ? await s.Outsider.Client.PostAsync(Url(id, tail), null) : await s.Outsider.Client.GetAsync(Url(id, tail));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Outsider.Client.PostAsJsonAsync(Url(id, "/chat"), new { message = "hi" })).StatusCode);
    }

    [Fact]
    public async Task Only_teaching_staff_can_close_a_class_and_nobody_can_join_it_afterwards()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s.Teacher, s.Course.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Ada.Client.PostAsync(Url(id, "/close"), null)).StatusCode);
        var closed = await ReadAsync(await s.Teacher.Client.PostAsync(Url(id, "/close"), null));
        Assert.Equal("Completed", closed.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await s.Ada.Client.PostAsync(Url(id, "/join"), null)).StatusCode);
    }

    // ---------- announcements ----------
    [Fact]
    public async Task Announcements_are_written_by_staff_and_read_by_the_class_with_pinned_first()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s.Teacher, s.Course.Id);
        Assert.Equal(HttpStatusCode.Created, (await s.Teacher.Client.PostAsJsonAsync(Url(id, "/announcements"), new { body = "Bring paper" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await s.Teacher.Client.PostAsJsonAsync(Url(id, "/announcements"), new { body = "Room changed", isPinned = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Teacher.Client.PostAsJsonAsync(Url(id, "/announcements"), new { body = "  " })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Ada.Client.PostAsJsonAsync(Url(id, "/announcements"), new { body = "Learners cannot" })).StatusCode);

        var seen = await ReadAsync(await s.Ada.Client.GetAsync(Url(id, "/announcements")));
        Assert.Equal(["Room changed", "Bring paper"], seen.EnumerateArray().Select(item => item.GetProperty("body").GetString()));
        Assert.Equal("Tara", seen[0].GetProperty("authorName").GetString());
    }

    // ---------- chat ----------
    [Fact]
    public async Task Teacher_and_learners_chat_in_the_class_with_names_and_limits()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s.Teacher, s.Course.Id);
        Assert.Equal(HttpStatusCode.Created, (await s.Ada.Client.PostAsJsonAsync(Url(id, "/chat"), new { message = "  Is this on the exam?  " })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await s.Teacher.Client.PostAsJsonAsync(Url(id, "/chat"), new { message = "Yes, chapter 3." })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Ada.Client.PostAsJsonAsync(Url(id, "/chat"), new { message = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Ada.Client.PostAsJsonAsync(Url(id, "/chat"), new { message = new string('x', 4001) })).StatusCode);

        var chat = await ReadAsync(await s.Teacher.Client.GetAsync(Url(id, "/chat")));
        Assert.Equal(2, chat.GetArrayLength());
        var question = chat.EnumerateArray().Single(item => item.GetProperty("userName").GetString() == "Ada");
        Assert.Equal("Is this on the exam?", question.GetProperty("message").GetString());   // trimmed
    }

    [Fact]
    public async Task A_guardian_cannot_post_to_the_chat()
    {
        var s = await NewSetupAsync();
        var guardian = await _world.AddPersonAsync(s.Tenant, "Gabe", "GUARDIAN");
        var id = await ScheduleAsync(s.Teacher, s.Course.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await guardian.Client.PostAsJsonAsync(Url(id, "/chat"), new { message = "hello" })).StatusCode);
    }

    // ---------- polls ----------
    [Fact]
    public async Task A_poll_collects_one_vote_per_person_that_can_be_changed_until_it_is_closed()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s.Teacher, s.Course.Id);
        var created = await s.Teacher.Client.PostAsJsonAsync(Url(id, "/polls"), new { question = "Ready for a break?", options = new[] { "Yes", "No", "  yes  " } });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var poll = await ReadAsync(created);
        var pollId = poll.GetProperty("id").GetGuid();
        Assert.Equal(["Yes", "No"], poll.GetProperty("options").EnumerateArray().Select(item => item.GetString()));   // duplicates collapse
        Assert.True(poll.GetProperty("isOpen").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await s.Ada.Client.PostAsJsonAsync(Url(id, $"/polls/{pollId}/vote"), new { optionIndex = 0 })).StatusCode);
        var changed = await ReadAsync(await s.Ada.Client.PostAsJsonAsync(Url(id, $"/polls/{pollId}/vote"), new { optionIndex = 1 }));
        Assert.Equal([0, 1], changed.GetProperty("results").EnumerateArray().Select(item => item.GetProperty("votes").GetInt32()));   // her second vote replaced the first
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Ada.Client.PostAsJsonAsync(Url(id, $"/polls/{pollId}/vote"), new { optionIndex = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Ada.Client.PostAsync(Url(id, $"/polls/{pollId}/close"), null)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await s.Teacher.Client.PostAsync(Url(id, $"/polls/{pollId}/close"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Ada.Client.PostAsJsonAsync(Url(id, $"/polls/{pollId}/vote"), new { optionIndex = 0 })).StatusCode);
    }

    [Fact]
    public async Task A_poll_needs_a_question_and_between_two_and_ten_distinct_options_and_staff()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s.Teacher, s.Course.Id);
        Task<HttpResponseMessage> Post(object body, Person? who = null) => (who ?? s.Teacher).Client.PostAsJsonAsync(Url(id, "/polls"), body);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { question = "", options = new[] { "a", "b" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { question = "Q?", options = new[] { "only one" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { question = "Q?", options = new[] { "A", "a" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { question = "Q?", options = Enumerable.Range(1, 11).Select(i => $"Option {i}").ToArray() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(new { question = "Q?", options = new[] { "a", "b" } }, s.Ada)).StatusCode);
    }

    // ---------- raised hands ----------
    [Fact]
    public async Task A_learner_raises_and_lowers_a_hand_and_everyone_in_the_class_sees_the_list_but_outsiders_do_not()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s.Teacher, s.Course.Id);
        var raised = await ReadAsync(await s.Ada.Client.PostAsJsonAsync(Url(id, "/hand-raise"), new { raised = true }));
        Assert.Equal("Raised", raised.GetProperty("status").GetString());

        var list = await ReadAsync(await s.Teacher.Client.GetAsync(Url(id, "/hand-raises")));
        Assert.Equal("Ada", Assert.Single(list.EnumerateArray()).GetProperty("userName").GetString());
        Assert.Equal("Ada", Assert.Single((await ReadAsync(await s.Ada.Client.GetAsync(Url(id, "/hand-raises")))).EnumerateArray()).GetProperty("userName").GetString());   // classmates see it too
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Outsider.Client.GetAsync(Url(id, "/hand-raises"))).StatusCode);                                                       // not in the class

        await s.Ada.Client.PostAsJsonAsync(Url(id, "/hand-raise"), new { raised = false });
        Assert.Empty((await ReadAsync(await s.Teacher.Client.GetAsync(Url(id, "/hand-raises")))).EnumerateArray());
    }

    [Fact]
    public async Task Staff_can_put_a_learners_hand_down_and_the_class_screens_are_told_about_hands()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s.Teacher, s.Course.Id);
        await s.Ada.Client.PostAsJsonAsync(Url(id, "/hand-raise"), new { raised = true });
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Ada.Client.PostAsync(Url(id, $"/hand-raises/{s.Ada.Id}/lower"), null)).StatusCode);   // learners lower their own with hand-raise
        Assert.Equal(HttpStatusCode.NotFound, (await s.Teacher.Client.PostAsync(Url(id, $"/hand-raises/{Guid.NewGuid()}/lower"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Teacher.Client.PostAsync(Url(id, $"/hand-raises/{s.Ada.Id}/lower"), null)).StatusCode);
        Assert.Empty((await ReadAsync(await s.Teacher.Client.GetAsync(Url(id, "/hand-raises")))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await s.Teacher.Client.PostAsync(Url(id, $"/hand-raises/{s.Ada.Id}/lower"), null)).StatusCode);   // already down

        // The live-update stream treats a hand going up as news, so open screens refresh.
        using var stream = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var request = new HttpRequestMessage(HttpMethod.Get, Url(id, "/events"));
        using var response = await s.Teacher.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stream.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(stream.Token));
        await reader.ReadLineAsync(stream.Token);                                                              // the first heartbeat
        await reader.ReadLineAsync(stream.Token); await reader.ReadLineAsync(stream.Token); await reader.ReadLineAsync(stream.Token);
        await s.Ada.Client.PostAsJsonAsync(Url(id, "/hand-raise"), new { raised = true });
        var seen = new List<string>();
        while (!seen.Contains("event: refresh") && !stream.IsCancellationRequested) seen.Add((await reader.ReadLineAsync(stream.Token)) ?? "");
        Assert.Contains("event: refresh", seen);
    }

    // ---------- recording ----------
    [Fact]
    public async Task A_recording_needs_the_hosts_consent_and_then_becomes_available()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s.Teacher, s.Course.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Ada.Client.GetAsync(Url(id, "/recording"))).StatusCode);                  // not requested yet
        Assert.Equal(HttpStatusCode.Conflict, (await s.Teacher.Client.PostAsync(Url(id, "/recording/request"), null)).StatusCode); // no consent
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Ada.Client.PostAsync(Url(id, "/recording/request"), null)).StatusCode);    // learners cannot ask

        var consent = await ReadAsync(await s.Teacher.Client.PostAsJsonAsync(Url(id, "/consent"), new { granted = true }));
        Assert.True(consent.GetProperty("granted").GetBoolean());
        var requested = await s.Teacher.Client.PostAsync(Url(id, "/recording/request"), null);
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        Assert.Equal("Requested", (await ReadAsync(requested)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Accepted, (await s.Teacher.Client.PostAsync(Url(id, "/recording/request"), null)).StatusCode);   // asking again is harmless

        // The background worker picks it up within a few seconds.
        JsonElement recording = default;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            recording = await ReadAsync(await s.Ada.Client.GetAsync(Url(id, "/recording")));
            if (recording.GetProperty("status").GetString() == "Available") break;
            await Task.Delay(500);
        }
        Assert.Equal("Available", recording.GetProperty("status").GetString());
        Assert.Contains(id.ToString(), recording.GetProperty("recordingUrl").GetString());
        Assert.NotEqual(JsonValueKind.Null, recording.GetProperty("retainUntilUtc").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await s.Teacher.Client.PostAsync(Url(id, "/recording/request"), null)).StatusCode);          // already available
        Assert.Equal(HttpStatusCode.Conflict, (await s.Teacher.Client.PostAsync(Url(id, "/recording/retry"), null)).StatusCode);       // only failed ones are retried
    }

    [Fact]
    public async Task Withdrawing_consent_stops_a_new_recording_request()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s.Teacher, s.Course.Id);
        await s.Teacher.Client.PostAsJsonAsync(Url(id, "/consent"), new { granted = true });
        await s.Teacher.Client.PostAsJsonAsync(Url(id, "/consent"), new { granted = false });
        Assert.Equal(HttpStatusCode.Conflict, (await s.Teacher.Client.PostAsync(Url(id, "/recording/request"), null)).StatusCode);
    }

    [Fact]
    public async Task Any_member_can_record_their_own_consent_but_outsiders_cannot()
    {
        var s = await NewSetupAsync();
        var id = await ScheduleAsync(s.Teacher, s.Course.Id);
        Assert.Equal(HttpStatusCode.OK, (await s.Ada.Client.PostAsJsonAsync(Url(id, "/consent"), new { granted = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Outsider.Client.PostAsJsonAsync(Url(id, "/consent"), new { granted = true })).StatusCode);
    }

    // ---------- organizations are separate ----------
    [Fact]
    public async Task One_organizations_classes_are_invisible_to_another()
    {
        var a = await NewSetupAsync();
        var b = await NewSetupAsync();
        var id = await ScheduleAsync(a.Teacher, a.Course.Id);
        Assert.False(Contains(await ListAsync(b.Teacher), id));
        var status = (await b.Teacher.Client.PostAsync(Url(id, "/join"), null)).StatusCode;
        Assert.True(status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden, $"joining another organization's class returned {status}");
    }
}
