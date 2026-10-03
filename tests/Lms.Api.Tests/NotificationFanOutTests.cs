using System.Net.Http.Json;
using System.Text.Json;
using Lms.Api.Infrastructure.Notifications;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lms.Api.Tests;

public sealed class NotificationFanOutTests : IClassFixture<LmsApiFactory>
{
    private readonly LmsApiFactory _factory;
    public NotificationFanOutTests(LmsApiFactory factory) => _factory = factory;

    private sealed record Setup(string Slug, HttpClient Admin, HttpClient Learner, HttpClient Outsider, Guid CourseId);

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<HttpClient> AddLearnerAsync(string slug, HttpClient admin, string name)
    {
        var email = $"{name}@{slug}.test";
        (await admin.PostAsJsonAsync("/api/v1/tenant/users", new { email, displayName = name, password = LmsApiFactory.AdminPassword, roleCode = "LEARNER" })).EnsureSuccessStatusCode();
        return _factory.CreateTenantClient(slug, await _factory.LoginAsync(slug, email, LmsApiFactory.AdminPassword));
    }

    private async Task<Setup> CreateAsync()
    {
        var (slug, _, adminToken) = await _factory.ProvisionTenantWithAdminAsync();
        var admin = _factory.CreateTenantClient(slug, adminToken);
        var course = await ReadAsync(await admin.PostAsJsonAsync("/api/v1/tenant/courses", new { code = "SCI-1", title = "Science" }));
        var courseId = course.GetProperty("course").GetProperty("id").GetGuid();
        (await admin.PostAsync($"/api/v1/tenant/courses/{courseId}/submit-review", null)).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/api/v1/tenant/courses/{courseId}/publish", null)).EnsureSuccessStatusCode();
        var learner = await AddLearnerAsync(slug, admin, "lena");
        (await learner.PostAsync($"/api/v1/tenant/courses/{courseId}/enroll", null)).EnsureSuccessStatusCode();
        var outsider = await AddLearnerAsync(slug, admin, "otto"); // not enrolled
        return new Setup(slug, admin, learner, outsider, courseId);
    }

    private static async Task<List<JsonElement>> InboxAsync(HttpClient client, string? template = null)
    {
        var items = await ReadAsync(await client.GetAsync("/api/v1/tenant/notifications"));
        return items.EnumerateArray().Where(item => template is null || item.GetProperty("templateCode").GetString() == template).ToList();
    }

    private static async Task<Guid> PublishedAssignmentAsync(HttpClient admin, Guid courseId, string title, DateTimeOffset? due)
    {
        var created = await ReadAsync(await admin.PostAsJsonAsync("/api/v1/tenant/assignments", new { courseId, title, maxPoints = 20, dueAtUtc = due, allowLate = false, latePenaltyPercent = 0 }));
        var id = created.GetProperty("id").GetGuid();
        (await admin.PostAsync($"/api/v1/tenant/assignments/{id}/publish", null)).EnsureSuccessStatusCode();
        return id;
    }

    private async Task<int> RunRemindersAsync(string slug, DateTimeOffset now, TimeSpan lookAhead)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LmsDbContext>();
        var tenant = await db.Tenants.IgnoreQueryFilters().SingleAsync(item => item.Slug == slug);
        ((TenantContext)scope.ServiceProvider.GetRequiredService<ITenantContext>()).Set(tenant.Id, tenant.Slug);
        return await scope.ServiceProvider.GetRequiredService<DeadlineReminderService>().RunAsync(db, tenant.Id, now, lookAhead, CancellationToken.None);
    }

    [Fact]
    public async Task Announcement_for_everyone_notifies_members_but_not_the_author()
    {
        var s = await CreateAsync();
        (await s.Admin.PostAsJsonAsync("/api/v1/tenant/community/announcements", new { title = "Holiday on Friday", body = "School is closed." })).EnsureSuccessStatusCode();

        Assert.Single(await InboxAsync(s.Learner, "ANNOUNCEMENT"));
        Assert.Single(await InboxAsync(s.Outsider, "ANNOUNCEMENT"));
        Assert.Empty(await InboxAsync(s.Admin, "ANNOUNCEMENT"));
        var message = (await InboxAsync(s.Learner, "ANNOUNCEMENT"))[0];
        Assert.Equal("Holiday on Friday", message.GetProperty("subject").GetString());
        Assert.Equal("School is closed.", message.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Course_announcement_only_reaches_enrolled_learners()
    {
        var s = await CreateAsync();
        (await s.Admin.PostAsJsonAsync("/api/v1/tenant/community/announcements", new { title = "Lab moved", body = "Room 4.", courseId = s.CourseId })).EnsureSuccessStatusCode();
        Assert.Single(await InboxAsync(s.Learner, "ANNOUNCEMENT"));
        Assert.Empty(await InboxAsync(s.Outsider, "ANNOUNCEMENT"));
    }

    [Fact]
    public async Task Long_announcement_bodies_are_shortened_in_the_notification()
    {
        var s = await CreateAsync();
        (await s.Admin.PostAsJsonAsync("/api/v1/tenant/community/announcements", new { title = "Long notice", body = new string('x', 1000) })).EnsureSuccessStatusCode();
        var body = (await InboxAsync(s.Learner, "ANNOUNCEMENT"))[0].GetProperty("body").GetString()!;
        Assert.True(body.Length <= 301);
        Assert.EndsWith("…", body);
    }

    [Fact]
    public async Task Publishing_an_assignment_notifies_enrolled_learners_only()
    {
        var s = await CreateAsync();
        await PublishedAssignmentAsync(s.Admin, s.CourseId, "Lab report", DateTimeOffset.UtcNow.AddDays(5));
        var message = Assert.Single(await InboxAsync(s.Learner, "ASSIGNMENT_PUBLISHED"));
        Assert.Equal("New assignment: Lab report", message.GetProperty("subject").GetString());
        Assert.Contains("Science", message.GetProperty("body").GetString());
        Assert.Empty(await InboxAsync(s.Outsider, "ASSIGNMENT_PUBLISHED"));
    }

    [Fact]
    public async Task Grading_notifies_the_learner_with_the_final_score()
    {
        var s = await CreateAsync();
        var id = await PublishedAssignmentAsync(s.Admin, s.CourseId, "Essay", null);
        var form = new MultipartFormDataContent { { new StringContent("answer"), "text" } };
        var submission = await ReadAsync(await s.Learner.PostAsync($"/api/v1/tenant/assignments/{id}/submission", form));
        (await s.Admin.PostAsJsonAsync($"/api/v1/tenant/assignments/submissions/{submission.GetProperty("id").GetGuid()}/grade", new { scorePoints = 17, feedback = "Good" })).EnsureSuccessStatusCode();

        var message = Assert.Single(await InboxAsync(s.Learner, "ASSIGNMENT_GRADED"));
        Assert.Contains("17", message.GetProperty("body").GetString());
        Assert.Contains("20", message.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Learners_who_opted_out_are_not_notified()
    {
        var s = await CreateAsync();
        (await s.Learner.PutAsJsonAsync("/api/v1/tenant/notification-preferences", new { templateCode = "ANNOUNCEMENT", channel = "InApp", enabled = false })).EnsureSuccessStatusCode();
        (await s.Admin.PostAsJsonAsync("/api/v1/tenant/community/announcements", new { title = "Quiet please", body = "Opt-out test" })).EnsureSuccessStatusCode();
        Assert.Empty(await InboxAsync(s.Learner, "ANNOUNCEMENT"));
        Assert.Single(await InboxAsync(s.Outsider, "ANNOUNCEMENT"));
    }

    [Fact]
    public async Task Reminder_goes_to_learners_who_have_not_submitted_and_only_once()
    {
        var s = await CreateAsync();
        var id = await PublishedAssignmentAsync(s.Admin, s.CourseId, "Due tomorrow", DateTimeOffset.UtcNow.AddHours(12));

        Assert.Equal(1, await RunRemindersAsync(s.Slug, DateTimeOffset.UtcNow, TimeSpan.FromHours(24)));
        var reminder = Assert.Single(await InboxAsync(s.Learner, "DEADLINE_REMINDER"));
        Assert.Equal("Upcoming deadline: Due tomorrow", reminder.GetProperty("subject").GetString());
        Assert.Empty(await InboxAsync(s.Outsider, "DEADLINE_REMINDER"));

        Assert.Equal(0, await RunRemindersAsync(s.Slug, DateTimeOffset.UtcNow, TimeSpan.FromHours(24)));
        Assert.Single(await InboxAsync(s.Learner, "DEADLINE_REMINDER"));
        _ = id;
    }

    [Fact]
    public async Task Reminders_skip_submitted_work_distant_deadlines_and_past_deadlines()
    {
        var s = await CreateAsync();
        var soon = await PublishedAssignmentAsync(s.Admin, s.CourseId, "Handed in", DateTimeOffset.UtcNow.AddHours(6));
        await PublishedAssignmentAsync(s.Admin, s.CourseId, "Next month", DateTimeOffset.UtcNow.AddDays(30));
        await PublishedAssignmentAsync(s.Admin, s.CourseId, "No deadline", null);
        var form = new MultipartFormDataContent { { new StringContent("done"), "text" } };
        (await s.Learner.PostAsync($"/api/v1/tenant/assignments/{soon}/submission", form)).EnsureSuccessStatusCode();

        Assert.Equal(0, await RunRemindersAsync(s.Slug, DateTimeOffset.UtcNow, TimeSpan.FromHours(24)));
        // Looking back past the deadline never reminds either.
        Assert.Equal(0, await RunRemindersAsync(s.Slug, DateTimeOffset.UtcNow.AddHours(7), TimeSpan.FromHours(1)));
        Assert.Empty(await InboxAsync(s.Learner, "DEADLINE_REMINDER"));
    }

    [Fact]
    public async Task Draft_assignments_never_trigger_reminders()
    {
        var s = await CreateAsync();
        (await s.Admin.PostAsJsonAsync("/api/v1/tenant/assignments", new { courseId = s.CourseId, title = "Unpublished", maxPoints = 10, dueAtUtc = DateTimeOffset.UtcNow.AddHours(5) })).EnsureSuccessStatusCode();
        Assert.Equal(0, await RunRemindersAsync(s.Slug, DateTimeOffset.UtcNow, TimeSpan.FromHours(24)));
    }

    [Fact]
    public async Task Notifications_are_isolated_between_tenants()
    {
        var a = await CreateAsync();
        var b = await CreateAsync();
        (await a.Admin.PostAsJsonAsync("/api/v1/tenant/community/announcements", new { title = "Only for A", body = "private" })).EnsureSuccessStatusCode();
        Assert.Empty(await InboxAsync(b.Learner, "ANNOUNCEMENT"));
    }
}
