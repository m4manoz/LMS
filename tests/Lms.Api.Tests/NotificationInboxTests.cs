using System.Net;
using System.Net.Http.Json;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

/// <summary>Reading and clearing a person's own notifications.</summary>
public sealed class NotificationInboxTests : IClassFixture<LmsApiFactory>
{
    private readonly TestWorld _world;

    public NotificationInboxTests(LmsApiFactory factory) { _world = new TestWorld(factory); }

    private static async Task<int> UnreadAsync(Person person)
        => (await ReadAsync(await person.Client.GetAsync("/api/v1/tenant/notifications?unread=true"))).GetArrayLength();

    /// <summary>Gives two learners an enrollment message each by enrolling them.</summary>
    private async Task<(Tenant Tenant, Person Ada, Person Ben)> SetupAsync()
    {
        var t = await _world.NewTenantAsync();
        var ada = await _world.AddLearnerAsync(t, "Ada");
        var ben = await _world.AddLearnerAsync(t, "Ben");
        var first = await _world.NewCourseAsync(t, "NI-1");
        var second = await _world.NewCourseAsync(t, "NI-2");
        foreach (var learner in new[] { ada, ben })
        {
            Assert.True((await EnrollAsync(learner, first)).IsSuccessStatusCode);
            Assert.True((await EnrollAsync(learner, second)).IsSuccessStatusCode);
        }
        return (t, ada, ben);
    }

    [Fact]
    public async Task Read_all_clears_my_unread_notifications_and_leaves_other_peoples_alone()
    {
        var (_, ada, ben) = await SetupAsync();
        Assert.Equal(2, await UnreadAsync(ada));
        Assert.Equal(2, await UnreadAsync(ben));

        var response = await ada.Client.PostAsync("/api/v1/tenant/notifications/read-all", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await ReadAsync(response)).GetProperty("marked").GetInt32());

        Assert.Equal(0, await UnreadAsync(ada));
        Assert.Equal(2, await UnreadAsync(ben));        // someone else's inbox is untouched
        var all = await ReadAsync(await ada.Client.GetAsync("/api/v1/tenant/notifications"));
        Assert.Equal(2, all.GetArrayLength());           // read messages stay in the inbox
        Assert.All(all.EnumerateArray(), item => Assert.NotEqual(System.Text.Json.JsonValueKind.Null, item.GetProperty("readAtUtc").ValueKind));
    }

    [Fact]
    public async Task Read_all_can_be_repeated_and_counts_only_what_it_changed()
    {
        var (_, ada, _) = await SetupAsync();
        await ada.Client.PostAsync("/api/v1/tenant/notifications/read-all", null);
        var again = await ReadAsync(await ada.Client.PostAsync("/api/v1/tenant/notifications/read-all", null));
        Assert.Equal(0, again.GetProperty("marked").GetInt32());
    }

    [Fact]
    public async Task One_notification_can_be_marked_read_and_only_by_its_owner()
    {
        var (_, ada, ben) = await SetupAsync();
        var mine = await ReadAsync(await ada.Client.GetAsync("/api/v1/tenant/notifications"));
        var id = mine[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await ben.Client.PostAsync($"/api/v1/tenant/notifications/{id}/read", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ada.Client.PostAsync($"/api/v1/tenant/notifications/{id}/read", null)).StatusCode);
        Assert.Equal(1, await UnreadAsync(ada));
    }
}
