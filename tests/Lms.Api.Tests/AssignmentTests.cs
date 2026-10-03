using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Lms.Api.Tests;

public sealed class AssignmentTests : IClassFixture<LmsApiFactory>
{
    private readonly LmsApiFactory _factory;
    public AssignmentTests(LmsApiFactory factory) => _factory = factory;

    private sealed record Setup(string Slug, HttpClient Admin, HttpClient Learner, Guid CourseId, Guid AssignmentId);

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<HttpClient> AddLearnerAsync(string slug, HttpClient admin, string name)
    {
        var email = $"{name}@{slug}.test";
        (await admin.PostAsJsonAsync("/api/v1/tenant/users", new { email, displayName = name, password = LmsApiFactory.AdminPassword, roleCode = "LEARNER" })).EnsureSuccessStatusCode();
        return _factory.CreateTenantClient(slug, await _factory.LoginAsync(slug, email, LmsApiFactory.AdminPassword));
    }

    private async Task<Setup> CreateAsync(DateTimeOffset? due = null, bool allowLate = false, int penalty = 0, bool publish = true, bool enroll = true)
    {
        var (slug, _, adminToken) = await _factory.ProvisionTenantWithAdminAsync();
        var admin = _factory.CreateTenantClient(slug, adminToken);
        var learner = await AddLearnerAsync(slug, admin, "lena");

        var course = await ReadAsync(await admin.PostAsJsonAsync("/api/v1/tenant/courses", new { code = "ENG-1", title = "English" }));
        var courseId = course.GetProperty("course").GetProperty("id").GetGuid();
        (await admin.PostAsync($"/api/v1/tenant/courses/{courseId}/submit-review", null)).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/api/v1/tenant/courses/{courseId}/publish", null)).EnsureSuccessStatusCode();
        if (enroll) (await learner.PostAsync($"/api/v1/tenant/courses/{courseId}/enroll", null)).EnsureSuccessStatusCode();

        var created = await admin.PostAsJsonAsync("/api/v1/tenant/assignments", new { courseId, title = "Essay one", instructions = "Write 300 words.", maxPoints = 50, dueAtUtc = due, allowLate, latePenaltyPercent = penalty });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var assignmentId = (await ReadAsync(created)).GetProperty("id").GetGuid();
        if (publish) Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/tenant/assignments/{assignmentId}/publish", null)).StatusCode);
        return new Setup(slug, admin, learner, courseId, assignmentId);
    }

    private static MultipartFormDataContent Form(string? text, string? fileName = null, string fileText = "file body")
    {
        var form = new MultipartFormDataContent();
        if (text is not null) form.Add(new StringContent(text), "text");
        if (fileName is not null) form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(fileText)), "file", fileName);
        return form;
    }

    [Fact]
    public async Task Creation_validates_points_and_penalty()
    {
        var s = await CreateAsync();
        var bad = await s.Admin.PostAsJsonAsync("/api/v1/tenant/assignments", new { courseId = s.CourseId, title = "Valid title", maxPoints = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var penalty = await s.Admin.PostAsJsonAsync("/api/v1/tenant/assignments", new { courseId = s.CourseId, title = "Valid title", maxPoints = 10, allowLate = true, latePenaltyPercent = 150 });
        Assert.Equal(HttpStatusCode.BadRequest, penalty.StatusCode);
    }

    [Fact]
    public async Task Learner_cannot_create_assignments_or_see_drafts()
    {
        var s = await CreateAsync(publish: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Learner.PostAsJsonAsync("/api/v1/tenant/assignments", new { courseId = s.CourseId, title = "Nope nope", maxPoints = 10 })).StatusCode);
        var list = await ReadAsync(await s.Learner.GetAsync("/api/v1/tenant/assignments"));
        Assert.Equal(0, list.GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await s.Learner.GetAsync($"/api/v1/tenant/assignments/{s.AssignmentId}")).StatusCode);
    }

    [Fact]
    public async Task Learner_submits_text_and_file_and_can_resubmit_until_graded()
    {
        var s = await CreateAsync();
        var first = await s.Learner.PostAsync($"/api/v1/tenant/assignments/{s.AssignmentId}/submission", Form("Draft answer", "answer.txt"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var again = await ReadAsync(await s.Learner.PostAsync($"/api/v1/tenant/assignments/{s.AssignmentId}/submission", Form("Better answer")));
        Assert.Equal(2, again.GetProperty("submissionCount").GetInt32());
        Assert.Equal("Better answer", again.GetProperty("textResponse").GetString());
        Assert.False(again.GetProperty("isLate").GetBoolean());
    }

    [Fact]
    public async Task Empty_submission_is_rejected()
    {
        var s = await CreateAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Learner.PostAsync($"/api/v1/tenant/assignments/{s.AssignmentId}/submission", Form(""))).StatusCode);
    }

    [Fact]
    public async Task Learner_must_be_enrolled_to_submit()
    {
        var s = await CreateAsync(enroll: false);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Learner.PostAsync($"/api/v1/tenant/assignments/{s.AssignmentId}/submission", Form("Hello"))).StatusCode);
    }

    [Fact]
    public async Task Late_submission_is_rejected_when_late_work_is_not_allowed()
    {
        var s = await CreateAsync(due: DateTimeOffset.UtcNow.AddHours(-1), allowLate: false);
        var response = await s.Learner.PostAsync($"/api/v1/tenant/assignments/{s.AssignmentId}/submission", Form("Too late"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Late_submission_is_flagged_and_penalised_when_graded()
    {
        var s = await CreateAsync(due: DateTimeOffset.UtcNow.AddHours(-1), allowLate: true, penalty: 20);
        var submitted = await ReadAsync(await s.Learner.PostAsync($"/api/v1/tenant/assignments/{s.AssignmentId}/submission", Form("Late but here")));
        Assert.True(submitted.GetProperty("isLate").GetBoolean());

        var graded = await ReadAsync(await s.Admin.PostAsJsonAsync($"/api/v1/tenant/assignments/submissions/{submitted.GetProperty("id").GetGuid()}/grade", new { scorePoints = 40, feedback = "Good effort" }));
        Assert.Equal(40m, graded.GetProperty("scorePoints").GetDecimal());
        Assert.Equal(32m, graded.GetProperty("finalPoints").GetDecimal());
        Assert.Equal("Graded", graded.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Grading_enforces_maximum_points_and_locks_resubmission()
    {
        var s = await CreateAsync();
        var submitted = await ReadAsync(await s.Learner.PostAsync($"/api/v1/tenant/assignments/{s.AssignmentId}/submission", Form("Answer")));
        var id = submitted.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.BadRequest, (await s.Admin.PostAsJsonAsync($"/api/v1/tenant/assignments/submissions/{id}/grade", new { scorePoints = 51 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PostAsJsonAsync($"/api/v1/tenant/assignments/submissions/{id}/grade", new { scorePoints = 50 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Learner.PostAsync($"/api/v1/tenant/assignments/{s.AssignmentId}/submission", Form("Changing my mind"))).StatusCode);
    }

    [Fact]
    public async Task Learner_cannot_grade_or_list_other_submissions()
    {
        var s = await CreateAsync();
        var submitted = await ReadAsync(await s.Learner.PostAsync($"/api/v1/tenant/assignments/{s.AssignmentId}/submission", Form("Answer")));
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Learner.PostAsJsonAsync($"/api/v1/tenant/assignments/submissions/{submitted.GetProperty("id").GetGuid()}/grade", new { scorePoints = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Learner.GetAsync($"/api/v1/tenant/assignments/{s.AssignmentId}/submissions")).StatusCode);
    }

    [Fact]
    public async Task Submission_files_are_private_to_the_author_and_graders()
    {
        var s = await CreateAsync();
        var submitted = await ReadAsync(await s.Learner.PostAsync($"/api/v1/tenant/assignments/{s.AssignmentId}/submission", Form("Answer", "work.txt", "secret contents")));
        var id = submitted.GetProperty("id").GetGuid();

        var mine = await s.Learner.GetAsync($"/api/v1/tenant/assignments/submissions/{id}/file");
        Assert.Equal("secret contents", await mine.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.GetAsync($"/api/v1/tenant/assignments/submissions/{id}/file")).StatusCode);

        var other = await AddLearnerAsync(s.Slug, s.Admin, "otto");
        (await other.PostAsync($"/api/v1/tenant/courses/{s.CourseId}/enroll", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/tenant/assignments/submissions/{id}/file")).StatusCode);
    }

    [Fact]
    public async Task Closed_assignments_stop_accepting_submissions()
    {
        var s = await CreateAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await s.Admin.PostAsync($"/api/v1/tenant/assignments/{s.AssignmentId}/close", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Learner.PostAsync($"/api/v1/tenant/assignments/{s.AssignmentId}/submission", Form("Answer"))).StatusCode);
    }

    [Fact]
    public async Task Teacher_list_shows_submission_counts_and_learner_list_shows_own_result()
    {
        var s = await CreateAsync();
        var submitted = await ReadAsync(await s.Learner.PostAsync($"/api/v1/tenant/assignments/{s.AssignmentId}/submission", Form("Answer")));
        (await s.Admin.PostAsJsonAsync($"/api/v1/tenant/assignments/submissions/{submitted.GetProperty("id").GetGuid()}/grade", new { scorePoints = 45, feedback = "Nice" })).EnsureSuccessStatusCode();

        var adminList = await ReadAsync(await s.Admin.GetAsync("/api/v1/tenant/assignments"));
        Assert.Equal(1, adminList[0].GetProperty("submissionCount").GetInt32());
        Assert.Equal(1, adminList[0].GetProperty("gradedCount").GetInt32());

        var learnerList = await ReadAsync(await s.Learner.GetAsync("/api/v1/tenant/assignments"));
        Assert.Equal("Nice", learnerList[0].GetProperty("mySubmission").GetProperty("feedback").GetString());
    }

    [Fact]
    public async Task Assignments_are_isolated_between_tenants()
    {
        var a = await CreateAsync();
        var b = await CreateAsync();
        var list = await ReadAsync(await b.Admin.GetAsync("/api/v1/tenant/assignments"));
        Assert.Equal(1, list.GetArrayLength());
        Assert.NotEqual(a.AssignmentId, list[0].GetProperty("id").GetGuid());
    }
}
