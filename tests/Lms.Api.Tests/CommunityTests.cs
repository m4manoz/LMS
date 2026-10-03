using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Lms.Api.Tests;

public sealed class CommunityTests : IClassFixture<LmsApiFactory>
{
    private readonly LmsApiFactory _factory;
    public CommunityTests(LmsApiFactory factory) => _factory = factory;

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<(string Slug, HttpClient Admin, HttpClient Learner)> TenantWithLearnerAsync()
    {
        var (slug, _, adminToken) = await _factory.ProvisionTenantWithAdminAsync();
        var admin = _factory.CreateTenantClient(slug, adminToken);
        var email = $"learner@{slug}.test";
        (await admin.PostAsJsonAsync("/api/v1/tenant/users", new { email, displayName = "Lena Learner", password = LmsApiFactory.AdminPassword, roleCode = "LEARNER" })).EnsureSuccessStatusCode();
        var learner = _factory.CreateTenantClient(slug, await _factory.LoginAsync(slug, email, LmsApiFactory.AdminPassword));
        return (slug, admin, learner);
    }

    private static async Task<Guid> CreateThreadAsync(HttpClient client, string title = "Question about week 1")
    {
        var response = await client.PostAsJsonAsync("/api/v1/tenant/community/threads", new { title, body = "Can someone explain this?" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Learner_can_start_a_thread_and_reply()
    {
        var (_, admin, learner) = await TenantWithLearnerAsync();
        var threadId = await CreateThreadAsync(learner);

        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync($"/api/v1/tenant/community/threads/{threadId}/replies", new { body = "Sure, see lesson 2." })).StatusCode);

        var detail = await ReadAsync(await learner.GetAsync($"/api/v1/tenant/community/threads/{threadId}"));
        Assert.Equal("Lena Learner", detail.GetProperty("authorName").GetString());
        Assert.Equal(1, detail.GetProperty("replies").GetArrayLength());

        var list = await ReadAsync(await learner.GetAsync("/api/v1/tenant/community/threads"));
        Assert.Equal(1, list[0].GetProperty("replyCount").GetInt32());
    }

    [Fact]
    public async Task Thread_validation_rejects_short_titles_and_unknown_courses()
    {
        var (_, _, learner) = await TenantWithLearnerAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await learner.PostAsJsonAsync("/api/v1/tenant/community/threads", new { title = "Hi", body = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await learner.PostAsJsonAsync("/api/v1/tenant/community/threads", new { courseId = Guid.NewGuid(), title = "Valid title", body = "x" })).StatusCode);
    }

    [Fact]
    public async Task Locked_threads_reject_replies_and_only_moderators_can_lock()
    {
        var (_, admin, learner) = await TenantWithLearnerAsync();
        var threadId = await CreateThreadAsync(learner);

        Assert.Equal(HttpStatusCode.Forbidden, (await learner.PostAsJsonAsync($"/api/v1/tenant/community/threads/{threadId}/lock", new { value = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/v1/tenant/community/threads/{threadId}/lock", new { value = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await learner.PostAsJsonAsync($"/api/v1/tenant/community/threads/{threadId}/replies", new { body = "Too late" })).StatusCode);
    }

    [Fact]
    public async Task Pinned_threads_are_listed_first()
    {
        var (_, admin, learner) = await TenantWithLearnerAsync();
        var first = await CreateThreadAsync(learner, "Older thread");
        await CreateThreadAsync(learner, "Newer thread");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/v1/tenant/community/threads/{first}/pin", new { value = true })).StatusCode);

        var list = await ReadAsync(await learner.GetAsync("/api/v1/tenant/community/threads"));
        Assert.Equal("Older thread", list[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Authors_and_moderators_can_delete_but_other_learners_cannot()
    {
        var (slug, admin, learner) = await TenantWithLearnerAsync();
        var otherEmail = $"other@{slug}.test";
        (await admin.PostAsJsonAsync("/api/v1/tenant/users", new { email = otherEmail, displayName = "Other", password = LmsApiFactory.AdminPassword, roleCode = "LEARNER" })).EnsureSuccessStatusCode();
        var other = _factory.CreateTenantClient(slug, await _factory.LoginAsync(slug, otherEmail, LmsApiFactory.AdminPassword));

        var threadId = await CreateThreadAsync(learner);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.DeleteAsync($"/api/v1/tenant/community/threads/{threadId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await learner.DeleteAsync($"/api/v1/tenant/community/threads/{threadId}")).StatusCode);

        var second = await CreateThreadAsync(learner, "Another question");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/tenant/community/threads/{second}")).StatusCode);
    }

    [Fact]
    public async Task Threads_are_isolated_between_tenants()
    {
        var (_, _, learnerA) = await TenantWithLearnerAsync();
        var (_, _, learnerB) = await TenantWithLearnerAsync();
        await CreateThreadAsync(learnerA);
        var list = await ReadAsync(await learnerB.GetAsync("/api/v1/tenant/community/threads"));
        Assert.Equal(0, list.GetArrayLength());
    }

    [Fact]
    public async Task Only_staff_can_publish_announcements_and_everyone_can_read_them()
    {
        var (_, admin, learner) = await TenantWithLearnerAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await learner.PostAsJsonAsync("/api/v1/tenant/community/announcements", new { title = "Hello", body = "World" })).StatusCode);

        var created = await admin.PostAsJsonAsync("/api/v1/tenant/community/announcements", new { title = "Term starts Monday", body = "Welcome back.", isPinned = true });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var list = await ReadAsync(await learner.GetAsync("/api/v1/tenant/community/announcements"));
        Assert.Equal(1, list.GetArrayLength());
        Assert.True(list[0].GetProperty("isPinned").GetBoolean());
    }

    [Fact]
    public async Task Expired_announcements_are_hidden_and_past_expiry_is_rejected()
    {
        var (_, admin, learner) = await TenantWithLearnerAsync();
        var past = await admin.PostAsJsonAsync("/api/v1/tenant/community/announcements", new { title = "Stale", body = "x", expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5) });
        Assert.Equal(HttpStatusCode.BadRequest, past.StatusCode);

        (await admin.PostAsJsonAsync("/api/v1/tenant/community/announcements", new { title = "Fresh", body = "x", expiresAtUtc = DateTimeOffset.UtcNow.AddDays(1) })).EnsureSuccessStatusCode();
        var list = await ReadAsync(await learner.GetAsync("/api/v1/tenant/community/announcements"));
        Assert.Equal(1, list.GetArrayLength());
    }
}
