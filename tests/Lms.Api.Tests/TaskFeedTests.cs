using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Lms.Api.Tests;

public sealed class TaskFeedTests : IClassFixture<LmsApiFactory>
{
    private readonly LmsApiFactory _factory;
    public TaskFeedTests(LmsApiFactory factory) => _factory = factory;

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static JsonElement? Find(JsonElement feed, string kind, string title)
        => feed.GetProperty("items").EnumerateArray().Cast<JsonElement?>().FirstOrDefault(item => item!.Value.GetProperty("kind").GetString() == kind && item.Value.GetProperty("title").GetString() == title);

    private async Task<(HttpClient Admin, HttpClient Learner, Guid CourseId)> TenantAsync(bool enroll = true)
    {
        var (slug, _, adminToken) = await _factory.ProvisionTenantWithAdminAsync();
        var admin = _factory.CreateTenantClient(slug, adminToken);
        var email = $"lena@{slug}.test";
        (await admin.PostAsJsonAsync("/api/v1/tenant/users", new { email, displayName = "Lena", password = LmsApiFactory.AdminPassword, roleCode = "LEARNER" })).EnsureSuccessStatusCode();
        var learner = _factory.CreateTenantClient(slug, await _factory.LoginAsync(slug, email, LmsApiFactory.AdminPassword));
        var course = await ReadAsync(await admin.PostAsJsonAsync("/api/v1/tenant/courses", new { code = "HIS-1", title = "History" }));
        var courseId = course.GetProperty("course").GetProperty("id").GetGuid();
        (await admin.PostAsync($"/api/v1/tenant/courses/{courseId}/submit-review", null)).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/api/v1/tenant/courses/{courseId}/publish", null)).EnsureSuccessStatusCode();
        if (enroll) (await learner.PostAsync($"/api/v1/tenant/courses/{courseId}/enroll", null)).EnsureSuccessStatusCode();
        return (admin, learner, courseId);
    }

    private static async Task<Guid> PublishedAssignmentAsync(HttpClient admin, Guid courseId, string title, DateTimeOffset? due, bool allowLate = true)
    {
        var created = await ReadAsync(await admin.PostAsJsonAsync("/api/v1/tenant/assignments", new { courseId, title, maxPoints = 10, dueAtUtc = due, allowLate, latePenaltyPercent = 0 }));
        var id = created.GetProperty("id").GetGuid();
        (await admin.PostAsync($"/api/v1/tenant/assignments/{id}/publish", null)).EnsureSuccessStatusCode();
        return id;
    }

    [Fact]
    public async Task Feed_requires_authentication()
    {
        var (slug, _, token) = await _factory.ProvisionTenantWithAdminAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateTenantClient(slug).GetAsync("/api/v1/tenant/tasks")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateTenantClient(slug, token).GetAsync("/api/v1/tenant/tasks")).StatusCode);
    }

    [Fact]
    public async Task Upcoming_assignment_is_a_todo_and_overdue_work_is_flagged_first()
    {
        var (admin, learner, courseId) = await TenantAsync();
        await PublishedAssignmentAsync(admin, courseId, "Due soon", DateTimeOffset.UtcNow.AddDays(3));
        await PublishedAssignmentAsync(admin, courseId, "Missed it", DateTimeOffset.UtcNow.AddDays(-2));

        var feed = await ReadAsync(await learner.GetAsync("/api/v1/tenant/tasks"));
        Assert.Equal("Todo", Find(feed, "assignment", "Due soon")!.Value.GetProperty("status").GetString());
        Assert.Equal("Overdue", Find(feed, "assignment", "Missed it")!.Value.GetProperty("status").GetString());
        Assert.Equal("Missed it", feed.GetProperty("items")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Submitting_moves_an_assignment_to_submitted()
    {
        var (admin, learner, courseId) = await TenantAsync();
        var id = await PublishedAssignmentAsync(admin, courseId, "Essay", DateTimeOffset.UtcNow.AddDays(1));
        var form = new MultipartFormDataContent { { new StringContent("My answer"), "text" } };
        (await learner.PostAsync($"/api/v1/tenant/assignments/{id}/submission", form)).EnsureSuccessStatusCode();

        var feed = await ReadAsync(await learner.GetAsync("/api/v1/tenant/tasks"));
        Assert.Equal("Submitted", Find(feed, "assignment", "Essay")!.Value.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Only_enrolled_courses_appear_for_learners()
    {
        var (admin, learner, courseId) = await TenantAsync(enroll: false);
        await PublishedAssignmentAsync(admin, courseId, "Hidden work", DateTimeOffset.UtcNow.AddDays(1));
        var feed = await ReadAsync(await learner.GetAsync("/api/v1/tenant/tasks"));
        Assert.Null(Find(feed, "assignment", "Hidden work"));
    }

    [Fact]
    public async Task Draft_assignments_are_not_listed_for_learners()
    {
        var (admin, learner, courseId) = await TenantAsync();
        (await admin.PostAsJsonAsync("/api/v1/tenant/assignments", new { courseId, title = "Still a draft", maxPoints = 10 })).EnsureSuccessStatusCode();
        var feed = await ReadAsync(await learner.GetAsync("/api/v1/tenant/tasks"));
        Assert.Null(Find(feed, "assignment", "Still a draft"));
    }

    [Fact]
    public async Task Teacher_sees_a_grading_item_with_the_number_waiting()
    {
        var (admin, learner, courseId) = await TenantAsync();
        var id = await PublishedAssignmentAsync(admin, courseId, "Lab report", DateTimeOffset.UtcNow.AddDays(1));
        var form = new MultipartFormDataContent { { new StringContent("Done"), "text" } };
        (await learner.PostAsync($"/api/v1/tenant/assignments/{id}/submission", form)).EnsureSuccessStatusCode();

        var feed = await ReadAsync(await admin.GetAsync("/api/v1/tenant/tasks"));
        var grading = Find(feed, "grading", "Lab report")!.Value;
        Assert.Equal(1, grading.GetProperty("count").GetInt32());
        Assert.Null(Find(await ReadAsync(await learner.GetAsync("/api/v1/tenant/tasks")), "grading", "Lab report"));
    }

    [Fact]
    public async Task Upcoming_live_classes_are_included_with_their_times()
    {
        var (admin, learner, _) = await TenantAsync();
        var start = DateTimeOffset.UtcNow.AddDays(2);
        (await admin.PostAsJsonAsync("/api/v1/tenant/live-classes/sessions", new { title = "Revision session", startAtUtc = start, endAtUtc = start.AddHours(1) })).EnsureSuccessStatusCode();

        var feed = await ReadAsync(await learner.GetAsync("/api/v1/tenant/tasks"));
        var live = Find(feed, "live-class", "Revision session")!.Value;
        Assert.Equal("Scheduled", live.GetProperty("status").GetString());
        Assert.Equal("live", live.GetProperty("target").GetString());
    }

    [Fact]
    public async Task A_live_class_whose_time_has_passed_is_marked_ended_so_it_is_not_a_to_do()
    {
        var (admin, learner, _) = await TenantAsync();
        var start = DateTimeOffset.UtcNow.AddDays(-2);
        (await admin.PostAsJsonAsync("/api/v1/tenant/live-classes/sessions", new { title = "Missed class", startAtUtc = start, endAtUtc = start.AddHours(1) })).EnsureSuccessStatusCode();
        var soon = DateTimeOffset.UtcNow.AddHours(3);
        (await admin.PostAsJsonAsync("/api/v1/tenant/live-classes/sessions", new { title = "Later today", startAtUtc = soon, endAtUtc = soon.AddHours(1) })).EnsureSuccessStatusCode();

        var feed = await ReadAsync(await learner.GetAsync("/api/v1/tenant/tasks"));
        Assert.Equal("Ended", Find(feed, "live-class", "Missed class")!.Value.GetProperty("status").GetString());   // still listed, so the calendar can show it
        Assert.Equal("Scheduled", Find(feed, "live-class", "Later today")!.Value.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Date_range_is_validated()
    {
        var (_, learner, _) = await TenantAsync();
        var backwards = await learner.GetAsync($"/api/v1/tenant/tasks?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"))}");
        Assert.Equal(HttpStatusCode.BadRequest, backwards.StatusCode);
        var huge = await learner.GetAsync($"/api/v1/tenant/tasks?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(800).ToString("O"))}");
        Assert.Equal(HttpStatusCode.BadRequest, huge.StatusCode);
    }

    [Fact]
    public async Task Feeds_are_isolated_between_tenants()
    {
        var (adminA, _, courseA) = await TenantAsync();
        await PublishedAssignmentAsync(adminA, courseA, "Tenant A only", DateTimeOffset.UtcNow.AddDays(1));
        var (_, learnerB, _) = await TenantAsync();
        Assert.Null(Find(await ReadAsync(await learnerB.GetAsync("/api/v1/tenant/tasks")), "assignment", "Tenant A only"));
    }
}
