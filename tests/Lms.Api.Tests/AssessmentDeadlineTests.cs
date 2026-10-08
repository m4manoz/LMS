using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

/// <summary>Assessments can open and close at set times, and those dates show up in the learner's tasks and calendar.</summary>
public sealed class AssessmentDeadlineTests : IClassFixture<LmsApiFactory>
{
    private readonly TestWorld _world;
    public AssessmentDeadlineTests(LmsApiFactory factory) => _world = new TestWorld(factory);

    private sealed record Setup(Tenant Tenant, CourseInfo Course, Person Learner);

    private async Task<Setup> NewSetupAsync()
    {
        var tenant = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(tenant, "DUE");
        var learner = await _world.AddLearnerAsync(tenant, "Lena");
        Assert.True((await EnrollAsync(learner, course)).IsSuccessStatusCode);
        return new Setup(tenant, course, learner);
    }

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("O");

    private static async Task<Guid> PublishedAsync(Setup s, object settings)
    {
        var created = await s.Tenant.Admin.PostAsJsonAsync($"/api/v1/tenant/courses/{s.Course.Id}/assessments", settings);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await ReadAsync(created)).GetProperty("id").GetGuid();
        (await s.Tenant.Admin.PostAsJsonAsync($"/api/v1/tenant/assessments/{id}/questions", new { type = "MultipleChoice", prompt = "Q", options = new[] { "a", "b" }, correctAnswers = new[] { "a" }, points = 1 })).EnsureSuccessStatusCode();
        (await s.Tenant.Admin.PostAsync($"/api/v1/tenant/assessments/{id}/publish", null)).EnsureSuccessStatusCode();
        return id;
    }

    private static Task<HttpResponseMessage> Change(Setup s, Guid id, object settings) => s.Tenant.Admin.PutAsJsonAsync($"/api/v1/tenant/assessments/{id}", settings);
    private static Task<HttpResponseMessage> Start(Setup s, Guid id) => s.Learner.Client.PostAsync($"/api/v1/tenant/assessments/{id}/attempts", null);

    private static async Task<JsonElement?> TaskAsync(Person who, Guid assessmentId)
    {
        var feed = await ReadAsync(await who.Client.GetAsync("/api/v1/tenant/tasks"));
        var match = feed.GetProperty("items").EnumerateArray().Where(item => item.GetProperty("id").GetString() == $"assessment:{assessmentId}").ToList();
        return match.Count == 0 ? null : match[0];
    }

    [Fact]
    public async Task An_assessment_cannot_be_started_before_it_opens_or_after_it_closes()
    {
        var s = await NewSetupAsync();
        var opens = DateTimeOffset.UtcNow.AddHours(2);
        var id = await PublishedAsync(s, new { title = "Later", attemptLimit = 2, opensAtUtc = Iso(opens), dueAtUtc = Iso(opens.AddDays(1)) });
        var early = await Start(s, id);
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Contains("not open yet", await early.Content.ReadAsStringAsync());

        Assert.True((await Change(s, id, new { title = "Later", attemptLimit = 2, opensAtUtc = Iso(DateTimeOffset.UtcNow.AddHours(-1)), dueAtUtc = Iso(DateTimeOffset.UtcNow.AddHours(5)) })).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Start(s, id)).StatusCode);

        Assert.True((await Change(s, id, new { title = "Later", attemptLimit = 2, dueAtUtc = Iso(DateTimeOffset.UtcNow.AddMinutes(-1)) })).IsSuccessStatusCode);
        var late = await Start(s, id);
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Contains("has closed", await late.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_deadline_must_come_after_the_opening_time_and_can_be_cleared()
    {
        var s = await NewSetupAsync();
        var now = DateTimeOffset.UtcNow;
        var created = await s.Tenant.Admin.PostAsJsonAsync($"/api/v1/tenant/courses/{s.Course.Id}/assessments", new { title = "Backwards", opensAtUtc = Iso(now.AddDays(2)), dueAtUtc = Iso(now.AddDays(1)) });
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);

        var id = await PublishedAsync(s, new { title = "Dated", dueAtUtc = Iso(now.AddDays(1)) });
        Assert.Equal(HttpStatusCode.BadRequest, (await Change(s, id, new { title = "Dated", opensAtUtc = Iso(now.AddDays(3)), dueAtUtc = Iso(now.AddDays(1)) })).StatusCode);
        var cleared = await ReadAsync(await Change(s, id, new { title = "Dated" }));
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("dueAtUtc").ValueKind);
        Assert.Equal(HttpStatusCode.Created, (await Start(s, id)).StatusCode);
    }

    [Fact]
    public async Task An_attempt_ends_at_the_deadline_if_that_comes_before_its_time_limit()
    {
        var s = await NewSetupAsync();
        var due = DateTimeOffset.UtcNow.AddMinutes(10);
        var id = await PublishedAsync(s, new { title = "Squeezed", timeLimitMinutes = 60, attemptLimit = 2, dueAtUtc = Iso(due) });
        var attempt = await ReadAsync(await Start(s, id));
        var expires = attempt.GetProperty("expiresAtUtc").GetDateTimeOffset();
        Assert.True(Math.Abs((expires - due).TotalSeconds) < 2, $"expected about {due:O}, got {expires:O}");   // not an hour from now

        // With a roomy deadline the time limit is what counts.
        var roomy = await PublishedAsync(s, new { title = "Roomy", timeLimitMinutes = 30, attemptLimit = 2, dueAtUtc = Iso(DateTimeOffset.UtcNow.AddDays(1)) });
        var second = await ReadAsync(await Start(s, roomy));
        Assert.Equal(30, Math.Round((second.GetProperty("expiresAtUtc").GetDateTimeOffset() - second.GetProperty("attempt").GetProperty("startedAtUtc").GetDateTimeOffset()).TotalMinutes));
    }

    [Fact]
    public async Task Extra_time_from_an_accommodation_runs_past_the_deadline_by_the_same_amount()
    {
        var s = await NewSetupAsync();
        var due = DateTimeOffset.UtcNow.AddMinutes(5);
        var id = await PublishedAsync(s, new { title = "Accommodated", timeLimitMinutes = 20, attemptLimit = 2, dueAtUtc = Iso(due) });
        Assert.True((await s.Tenant.Admin.PutAsJsonAsync($"/api/v1/tenant/courses/{s.Course.Id}/accommodations/{s.Learner.Id}", new { extraTimePercent = 50, extraAttempts = 0 })).IsSuccessStatusCode);   // 20 -> 30 minutes
        var attempt = await ReadAsync(await Start(s, id));
        var expires = attempt.GetProperty("expiresAtUtc").GetDateTimeOffset();
        Assert.True(Math.Abs((expires - due.AddMinutes(10)).TotalSeconds) < 2, $"expected deadline plus 10 minutes, got {expires:O}");
    }

    [Fact]
    public async Task When_the_deadline_passes_mid_attempt_answers_stop_but_the_saved_ones_can_still_be_submitted()
    {
        var s = await NewSetupAsync();
        var id = await PublishedAsync(s, new { title = "Closing", attemptLimit = 2, dueAtUtc = Iso(DateTimeOffset.UtcNow.AddHours(1)) });
        var attempt = await ReadAsync(await Start(s, id));
        var attemptId = attempt.GetProperty("attempt").GetProperty("id").GetGuid();
        var questionId = attempt.GetProperty("questions")[0].GetProperty("id").GetGuid();
        (await s.Learner.Client.PutAsJsonAsync($"/api/v1/tenant/assessment-attempts/{attemptId}/answers/{questionId}", new { answers = new[] { "a" } })).EnsureSuccessStatusCode();

        Assert.True((await Change(s, id, new { title = "Closing", attemptLimit = 2, dueAtUtc = Iso(DateTimeOffset.UtcNow.AddMinutes(-1)) })).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Learner.Client.PutAsJsonAsync($"/api/v1/tenant/assessment-attempts/{attemptId}/answers/{questionId}", new { answers = new[] { "b" } })).StatusCode);
        var submitted = await ReadAsync(await s.Learner.Client.PostAsync($"/api/v1/tenant/assessment-attempts/{attemptId}/submit", null));
        Assert.Equal(100m, submitted.GetProperty("attempt").GetProperty("percentage").GetDecimal());   // what was saved before the close counts
        Assert.True(submitted.GetProperty("attempt").GetProperty("submittedAfterTimeLimit").GetBoolean());
    }

    [Fact]
    public async Task The_learners_tasks_show_the_deadline_and_whether_it_was_met_or_missed()
    {
        var s = await NewSetupAsync();
        var upcoming = await PublishedAsync(s, new { title = "Upcoming", dueAtUtc = Iso(DateTimeOffset.UtcNow.AddDays(3)) });
        var undated = await PublishedAsync(s, new { title = "Undated" });
        var later = await PublishedAsync(s, new { title = "Far away", dueAtUtc = Iso(DateTimeOffset.UtcNow.AddDays(200)) });
        var missed = await PublishedAsync(s, new { title = "Missed" });
        var finished = await PublishedAsync(s, new { title = "Finished", attemptLimit = 2 });

        var item = await TaskAsync(s.Learner, upcoming);
        Assert.Equal("Todo", item!.Value.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, item.Value.GetProperty("dueAtUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await TaskAsync(s.Learner, undated))!.Value.GetProperty("dueAtUtc").ValueKind);
        Assert.Null(await TaskAsync(s.Learner, later));   // dated outside the window: not shown

        Assert.True((await Change(s, missed, new { title = "Missed", dueAtUtc = Iso(DateTimeOffset.UtcNow.AddDays(-2)) })).IsSuccessStatusCode);
        Assert.Equal("Overdue", (await TaskAsync(s.Learner, missed))!.Value.GetProperty("status").GetString());
        Assert.True((await Change(s, missed, new { title = "Missed", dueAtUtc = Iso(DateTimeOffset.UtcNow.AddDays(-60)) })).IsSuccessStatusCode);
        Assert.Null(await TaskAsync(s.Learner, missed));   // long past: it drops off the list

        var attempt = await ReadAsync(await Start(s, finished));
        var attemptId = attempt.GetProperty("attempt").GetProperty("id").GetGuid();
        await s.Learner.Client.PostAsync($"/api/v1/tenant/assessment-attempts/{attemptId}/submit", null);
        Assert.True((await Change(s, finished, new { title = "Finished", attemptLimit = 2, dueAtUtc = Iso(DateTimeOffset.UtcNow.AddDays(-1)) })).IsSuccessStatusCode);
        Assert.Equal("Done", (await TaskAsync(s.Learner, finished))!.Value.GetProperty("status").GetString());   // started before the close, so it is not "missed"
    }

    [Fact]
    public async Task Existing_assessments_without_dates_behave_as_before()
    {
        var s = await NewSetupAsync();
        var id = await PublishedAsync(s, new { title = "Plain", attemptLimit = 1 });
        var response = await Start(s, id);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await ReadAsync(response)).GetProperty("expiresAtUtc").ValueKind);
    }
}
