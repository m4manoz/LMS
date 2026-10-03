using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Lms.Api.Tests;

public sealed class GradebookTests : IClassFixture<LmsApiFactory>
{
    private readonly LmsApiFactory _factory;
    public GradebookTests(LmsApiFactory factory) => _factory = factory;

    private sealed record Setup(string Slug, HttpClient Admin, HttpClient Learner, Guid LearnerId, Guid CourseId);

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<(HttpClient Client, Guid Id)> AddLearnerAsync(string slug, HttpClient admin, string name, Guid courseId)
    {
        var email = $"{name}@{slug}.test";
        (await admin.PostAsJsonAsync("/api/v1/tenant/users", new { email, displayName = name, password = LmsApiFactory.AdminPassword, roleCode = "LEARNER" })).EnsureSuccessStatusCode();
        var client = _factory.CreateTenantClient(slug, await _factory.LoginAsync(slug, email, LmsApiFactory.AdminPassword));
        (await client.PostAsync($"/api/v1/tenant/courses/{courseId}/enroll", null)).EnsureSuccessStatusCode();
        var me = await ReadAsync(await client.GetAsync("/api/v1/tenant/me"));
        return (client, Guid.Parse(me.GetProperty("userId").GetString()!));
    }

    private async Task<Setup> CreateAsync()
    {
        var (slug, _, adminToken) = await _factory.ProvisionTenantWithAdminAsync();
        var admin = _factory.CreateTenantClient(slug, adminToken);
        var course = await ReadAsync(await admin.PostAsJsonAsync("/api/v1/tenant/courses", new { code = "MAT-1", title = "Maths" }));
        var courseId = course.GetProperty("course").GetProperty("id").GetGuid();
        (await admin.PostAsync($"/api/v1/tenant/courses/{courseId}/submit-review", null)).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/api/v1/tenant/courses/{courseId}/publish", null)).EnsureSuccessStatusCode();
        var (learner, id) = await AddLearnerAsync(slug, admin, "Lena", courseId);
        return new Setup(slug, admin, learner, id, courseId);
    }

    private static async Task<Guid> PublishedAssignmentAsync(HttpClient admin, Guid courseId, string title, int max, DateTimeOffset? due = null, int penalty = 0)
    {
        var created = await ReadAsync(await admin.PostAsJsonAsync("/api/v1/tenant/assignments", new { courseId, title, maxPoints = max, dueAtUtc = due, allowLate = penalty > 0 || due is not null, latePenaltyPercent = penalty }));
        var id = created.GetProperty("id").GetGuid();
        (await admin.PostAsync($"/api/v1/tenant/assignments/{id}/publish", null)).EnsureSuccessStatusCode();
        return id;
    }

    private static async Task<Guid> SubmitAsync(HttpClient learner, Guid assignmentId)
    {
        var form = new MultipartFormDataContent { { new StringContent("answer"), "text" } };
        var response = await ReadAsync(await learner.PostAsync($"/api/v1/tenant/assignments/{assignmentId}/submission", form));
        return response.GetProperty("id").GetGuid();
    }

    private static async Task GradeAsync(HttpClient admin, Guid submissionId, decimal score)
        => (await admin.PostAsJsonAsync($"/api/v1/tenant/assignments/submissions/{submissionId}/grade", new { scorePoints = score, feedback = "Well done" })).EnsureSuccessStatusCode();

    [Fact]
    public async Task Class_gradebook_shows_graded_pending_and_missing_cells()
    {
        var s = await CreateAsync();
        var (other, _) = await AddLearnerAsync(s.Slug, s.Admin, "Otto", s.CourseId);
        var (third, _) = await AddLearnerAsync(s.Slug, s.Admin, "Tess", s.CourseId);
        var essay = await PublishedAssignmentAsync(s.Admin, s.CourseId, "Essay", 50);

        await GradeAsync(s.Admin, await SubmitAsync(s.Learner, essay), 40);   // Lena: graded 40/50
        await SubmitAsync(other, essay);                                      // Otto: submitted, not graded
        _ = third;                                                            // Tess: nothing

        var book = await ReadAsync(await s.Admin.GetAsync($"/api/v1/tenant/gradebook/courses/{s.CourseId}"));
        Assert.Equal(1, book.GetProperty("items").GetArrayLength());
        var cells = book.GetProperty("learners").EnumerateArray().ToDictionary(l => l.GetProperty("name").GetString()!, l => l.GetProperty("cells")[0]);
        Assert.Equal("Graded", cells["Lena"].GetProperty("status").GetString());
        Assert.Equal(80m, cells["Lena"].GetProperty("percent").GetDecimal());
        Assert.Equal("Pending", cells["Otto"].GetProperty("status").GetString());
        Assert.Equal("Missing", cells["Tess"].GetProperty("status").GetString());
        Assert.Equal(80m, book.GetProperty("itemAverages")[0].GetDecimal());
    }

    [Fact]
    public async Task Overall_percentage_is_total_points_earned_over_total_possible()
    {
        var s = await CreateAsync();
        var small = await PublishedAssignmentAsync(s.Admin, s.CourseId, "Quiz-like", 10);
        var large = await PublishedAssignmentAsync(s.Admin, s.CourseId, "Project", 90);
        await GradeAsync(s.Admin, await SubmitAsync(s.Learner, small), 10);   // 100%
        await GradeAsync(s.Admin, await SubmitAsync(s.Learner, large), 45);   // 50%

        var book = await ReadAsync(await s.Admin.GetAsync($"/api/v1/tenant/gradebook/courses/{s.CourseId}"));
        var learner = book.GetProperty("learners")[0];
        Assert.Equal(55m, learner.GetProperty("earnedPoints").GetDecimal());
        Assert.Equal(100m, learner.GetProperty("possiblePoints").GetDecimal());
        Assert.Equal(55m, learner.GetProperty("overallPercent").GetDecimal()); // not the 75% mean of the two percentages
    }

    [Fact]
    public async Task Late_penalty_is_reflected_in_the_gradebook()
    {
        var s = await CreateAsync();
        var id = await PublishedAssignmentAsync(s.Admin, s.CourseId, "Late work", 100, DateTimeOffset.UtcNow.AddHours(-1), penalty: 25);
        await GradeAsync(s.Admin, await SubmitAsync(s.Learner, id), 80);

        var book = await ReadAsync(await s.Admin.GetAsync($"/api/v1/tenant/gradebook/courses/{s.CourseId}"));
        var cell = book.GetProperty("learners")[0].GetProperty("cells")[0];
        Assert.Equal(60m, cell.GetProperty("score").GetDecimal());
        Assert.True(cell.GetProperty("isLate").GetBoolean());
    }

    [Fact]
    public async Task Assessment_scores_appear_alongside_assignments()
    {
        var s = await CreateAsync();
        var created = await ReadAsync(await s.Admin.PostAsJsonAsync($"/api/v1/tenant/courses/{s.CourseId}/assessments", new { title = "Unit quiz", instructions = "", attemptLimit = 1 }));
        var assessmentId = created.GetProperty("id").GetGuid();
        (await s.Admin.PostAsJsonAsync($"/api/v1/tenant/assessments/{assessmentId}/questions", new { type = "MultipleChoice", prompt = "2 + 2?", options = new[] { "3", "4" }, correctAnswers = new[] { "4" }, points = 5 })).EnsureSuccessStatusCode();
        (await s.Admin.PostAsync($"/api/v1/tenant/assessments/{assessmentId}/publish", null)).EnsureSuccessStatusCode();

        var attempt = await ReadAsync(await s.Learner.PostAsync($"/api/v1/tenant/assessments/{assessmentId}/attempts", null));
        var attemptId = attempt.GetProperty("attempt").GetProperty("id").GetGuid();
        var questionId = attempt.GetProperty("questions")[0].GetProperty("id").GetGuid();
        (await s.Learner.PutAsJsonAsync($"/api/v1/tenant/assessment-attempts/{attemptId}/answers/{questionId}", new { answers = new[] { "4" }, text = (string?)null })).EnsureSuccessStatusCode();
        (await s.Learner.PostAsync($"/api/v1/tenant/assessment-attempts/{attemptId}/submit", null)).EnsureSuccessStatusCode();

        var book = await ReadAsync(await s.Admin.GetAsync($"/api/v1/tenant/gradebook/courses/{s.CourseId}"));
        Assert.Equal("assessment", book.GetProperty("items")[0].GetProperty("kind").GetString());
        var cell = book.GetProperty("learners")[0].GetProperty("cells")[0];
        Assert.Equal("Graded", cell.GetProperty("status").GetString());
        Assert.Equal(100m, cell.GetProperty("percent").GetDecimal());
    }

    [Fact]
    public async Task Learner_sees_only_their_own_grades_with_feedback()
    {
        var s = await CreateAsync();
        var id = await PublishedAssignmentAsync(s.Admin, s.CourseId, "Essay", 20);
        await GradeAsync(s.Admin, await SubmitAsync(s.Learner, id), 15);
        var (other, _) = await AddLearnerAsync(s.Slug, s.Admin, "Otto", s.CourseId);

        var mine = await ReadAsync(await s.Learner.GetAsync("/api/v1/tenant/gradebook/me"));
        Assert.Equal(75m, mine[0].GetProperty("overallPercent").GetDecimal());
        Assert.Equal("Well done", mine[0].GetProperty("rows")[0].GetProperty("feedback").GetString());

        var others = await ReadAsync(await other.GetAsync("/api/v1/tenant/gradebook/me"));
        Assert.Null(others[0].GetProperty("overallPercent").ValueKind == JsonValueKind.Null ? null : "has grade");
        Assert.Equal("Missing", others[0].GetProperty("rows")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Learners_cannot_open_the_class_gradebook_or_export()
    {
        var s = await CreateAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Learner.GetAsync($"/api/v1/tenant/gradebook/courses/{s.CourseId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Learner.GetAsync($"/api/v1/tenant/gradebook/courses/{s.CourseId}/export.csv")).StatusCode);
    }

    [Fact]
    public async Task Csv_export_contains_scores_and_neutralises_formula_names()
    {
        var s = await CreateAsync();
        var (evil, _) = await AddLearnerAsync(s.Slug, s.Admin, "=cmd", s.CourseId);
        var id = await PublishedAssignmentAsync(s.Admin, s.CourseId, "Essay", 50);
        await GradeAsync(s.Admin, await SubmitAsync(s.Learner, id), 45);
        _ = evil;

        var response = await s.Admin.GetAsync($"/api/v1/tenant/gradebook/courses/{s.CourseId}/export.csv");
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        var csv = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"Essay (/50)\"", csv);
        Assert.Contains("\"Lena\"", csv);
        Assert.Contains("\"45\"", csv);
        Assert.Contains("\"'=cmd\"", csv);
        Assert.DoesNotContain("\"=cmd\"", csv);
    }

    [Fact]
    public async Task Gradebook_is_isolated_between_tenants()
    {
        var a = await CreateAsync();
        var b = await CreateAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.GetAsync($"/api/v1/tenant/gradebook/courses/{a.CourseId}")).StatusCode);
    }
}
