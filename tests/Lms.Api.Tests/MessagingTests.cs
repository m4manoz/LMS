using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Lms.Api.Tests;

public sealed class MessagingTests : IClassFixture<LmsApiFactory>
{
    private readonly LmsApiFactory _factory;
    public MessagingTests(LmsApiFactory factory) => _factory = factory;

    private sealed record Person(HttpClient Client, Guid Id, string Name);
    private sealed record Setup(string Slug, Person Admin, Person Lena, Person Otto, Guid CourseId);

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<Person> AddLearnerAsync(string slug, HttpClient admin, string name, Guid? enrollIn = null)
    {
        var email = $"{name}@{slug}.test";
        (await admin.PostAsJsonAsync("/api/v1/tenant/users", new { email, displayName = name, password = LmsApiFactory.AdminPassword, roleCode = "LEARNER" })).EnsureSuccessStatusCode();
        var client = _factory.CreateTenantClient(slug, await _factory.LoginAsync(slug, email, LmsApiFactory.AdminPassword));
        if (enrollIn is Guid courseId) (await client.PostAsync($"/api/v1/tenant/courses/{courseId}/enroll", null)).EnsureSuccessStatusCode();
        var me = await ReadAsync(await client.GetAsync("/api/v1/tenant/me"));
        return new Person(client, Guid.Parse(me.GetProperty("userId").GetString()!), name);
    }

    /// <summary>A tenant with an admin (staff), an enrolled learner Lena and a not-enrolled learner Otto.</summary>
    private async Task<Setup> CreateAsync()
    {
        var (slug, _, adminToken) = await _factory.ProvisionTenantWithAdminAsync();
        var adminClient = _factory.CreateTenantClient(slug, adminToken);
        var adminMe = await ReadAsync(await adminClient.GetAsync("/api/v1/tenant/me"));
        var course = await ReadAsync(await adminClient.PostAsJsonAsync("/api/v1/tenant/courses", new { code = "ART-1", title = "Art" }));
        var courseId = course.GetProperty("course").GetProperty("id").GetGuid();
        (await adminClient.PostAsync($"/api/v1/tenant/courses/{courseId}/submit-review", null)).EnsureSuccessStatusCode();
        (await adminClient.PostAsync($"/api/v1/tenant/courses/{courseId}/publish", null)).EnsureSuccessStatusCode();
        var lena = await AddLearnerAsync(slug, adminClient, "Lena", courseId);
        var otto = await AddLearnerAsync(slug, adminClient, "Otto");
        return new Setup(slug, new Person(adminClient, Guid.Parse(adminMe.GetProperty("userId").GetString()!), "Admin"), lena, otto, courseId);
    }

    private static async Task<Guid> DirectAsync(Person from, Person to)
    {
        var response = await from.Client.PostAsJsonAsync("/api/v1/tenant/messages/conversations/direct", new { userId = to.Id });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> SendAsync(Person from, Guid conversationId, string body)
        => from.Client.PostAsJsonAsync($"/api/v1/tenant/messages/conversations/{conversationId}/messages", new { body });

    private static async Task<int> UnreadAsync(Person person)
        => (await ReadAsync(await person.Client.GetAsync("/api/v1/tenant/messages/unread-count"))).GetProperty("count").GetInt32();

    [Fact]
    public async Task Learner_can_message_a_teacher_and_both_see_the_conversation()
    {
        var s = await CreateAsync();
        var id = await DirectAsync(s.Lena, s.Admin);
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(s.Lena, id, "Hello teacher")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(s.Admin, id, "Hi Lena")).StatusCode);

        var messages = await ReadAsync(await s.Lena.Client.GetAsync($"/api/v1/tenant/messages/conversations/{id}/messages"));
        Assert.Equal(2, messages.GetArrayLength());
        Assert.True(messages[0].GetProperty("isMine").GetBoolean());
        Assert.Equal("Admin", messages[1].GetProperty("senderName").GetString());

        var lenaList = await ReadAsync(await s.Lena.Client.GetAsync("/api/v1/tenant/messages/conversations"));
        Assert.Equal("Admin", lenaList.EnumerateArray().Single(c => c.GetProperty("kind").GetString() == "Direct").GetProperty("title").GetString());
        var adminList = await ReadAsync(await s.Admin.Client.GetAsync("/api/v1/tenant/messages/conversations"));
        Assert.Equal("Lena", adminList.EnumerateArray().Single(c => c.GetProperty("kind").GetString() == "Direct").GetProperty("title").GetString());
    }

    [Fact]
    public async Task Starting_a_direct_conversation_twice_returns_the_same_one_from_either_side()
    {
        var s = await CreateAsync();
        var first = await DirectAsync(s.Lena, s.Admin);
        Assert.Equal(first, await DirectAsync(s.Lena, s.Admin));
        Assert.Equal(first, await DirectAsync(s.Admin, s.Lena));
    }

    [Fact]
    public async Task Learners_cannot_start_conversations_with_each_other_by_default()
    {
        var s = await CreateAsync();
        var response = await s.Lena.Client.PostAsJsonAsync("/api/v1/tenant/messages/conversations/direct", new { userId = s.Otto.Id });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var contacts = await ReadAsync(await s.Lena.Client.GetAsync("/api/v1/tenant/messages/contacts"));
        var names = contacts.EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Contains("Admin", names);
        Assert.DoesNotContain("Otto", names);
        Assert.All(contacts.EnumerateArray(), c => Assert.True(c.GetProperty("isStaff").GetBoolean()));
    }

    [Fact]
    public async Task Staff_can_contact_anyone_and_search_filters_contacts()
    {
        var s = await CreateAsync();
        var all = await ReadAsync(await s.Admin.Client.GetAsync("/api/v1/tenant/messages/contacts"));
        Assert.Equal(2, all.GetArrayLength());
        var filtered = await ReadAsync(await s.Admin.Client.GetAsync("/api/v1/tenant/messages/contacts?q=ott"));
        Assert.Equal("Otto", filtered[0].GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.Client.PostAsJsonAsync("/api/v1/tenant/messages/conversations/direct", new { userId = s.Otto.Id })).StatusCode);
    }

    [Fact]
    public async Task You_cannot_message_yourself_or_a_stranger()
    {
        var s = await CreateAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Admin.Client.PostAsJsonAsync("/api/v1/tenant/messages/conversations/direct", new { userId = s.Admin.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Admin.Client.PostAsJsonAsync("/api/v1/tenant/messages/conversations/direct", new { userId = Guid.NewGuid() })).StatusCode);
    }

    [Fact]
    public async Task Third_parties_cannot_read_or_post_in_someone_elses_conversation()
    {
        var s = await CreateAsync();
        var id = await DirectAsync(s.Lena, s.Admin);
        await SendAsync(s.Lena, id, "private");
        Assert.Equal(HttpStatusCode.NotFound, (await s.Otto.Client.GetAsync($"/api/v1/tenant/messages/conversations/{id}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(s.Otto, id, "intruding")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Otto.Client.PostAsync($"/api/v1/tenant/messages/conversations/{id}/read", null)).StatusCode);
    }

    [Fact]
    public async Task Message_length_is_validated()
    {
        var s = await CreateAsync();
        var id = await DirectAsync(s.Lena, s.Admin);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(s.Lena, id, "   ")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(s.Lena, id, new string('x', 5001))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(s.Lena, id, new string('x', 5000))).StatusCode);
    }

    [Fact]
    public async Task Unread_counts_rise_for_the_recipient_and_clear_when_read()
    {
        var s = await CreateAsync();
        var id = await DirectAsync(s.Lena, s.Admin);
        await SendAsync(s.Lena, id, "one");
        await SendAsync(s.Lena, id, "two");

        Assert.Equal(2, await UnreadAsync(s.Admin));
        Assert.Equal(0, await UnreadAsync(s.Lena)); // your own messages are never unread

        var list = await ReadAsync(await s.Admin.Client.GetAsync("/api/v1/tenant/messages/conversations"));
        Assert.Equal(2, list[0].GetProperty("unreadCount").GetInt32());
        Assert.Equal("two", list[0].GetProperty("lastMessagePreview").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await s.Admin.Client.PostAsync($"/api/v1/tenant/messages/conversations/{id}/read", null)).StatusCode);
        Assert.Equal(0, await UnreadAsync(s.Admin));

        await SendAsync(s.Lena, id, "three");
        Assert.Equal(1, await UnreadAsync(s.Admin));
    }

    [Fact]
    public async Task Course_chat_is_open_to_enrolled_learners_and_staff_only()
    {
        var s = await CreateAsync();
        var opened = await s.Lena.Client.PostAsJsonAsync("/api/v1/tenant/messages/conversations/course", new { courseId = s.CourseId });
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        var id = (await ReadAsync(opened)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Created, (await SendAsync(s.Lena, id, "Anyone up for study group?")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Otto.Client.PostAsJsonAsync("/api/v1/tenant/messages/conversations/course", new { courseId = s.CourseId })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Otto.Client.GetAsync($"/api/v1/tenant/messages/conversations/{id}/messages")).StatusCode);

        // Staff can read and reply, and see the same single chat room.
        Assert.Equal(id, (await ReadAsync(await s.Admin.Client.PostAsJsonAsync("/api/v1/tenant/messages/conversations/course", new { courseId = s.CourseId }))).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(s.Admin, id, "Yes, Thursday")).StatusCode);
    }

    [Fact]
    public async Task Course_chat_messages_are_unread_for_other_members_including_those_who_never_opened_it()
    {
        var s = await CreateAsync();
        var classmate = await AddLearnerAsync(s.Slug, s.Admin.Client, "Cleo", s.CourseId);
        var id = (await ReadAsync(await s.Lena.Client.PostAsJsonAsync("/api/v1/tenant/messages/conversations/course", new { courseId = s.CourseId }))).GetProperty("id").GetGuid();
        await SendAsync(s.Lena, id, "Hello class");

        Assert.Equal(1, await UnreadAsync(classmate));
        var list = await ReadAsync(await classmate.Client.GetAsync("/api/v1/tenant/messages/conversations"));
        Assert.Contains("course chat", list[0].GetProperty("title").GetString());
        Assert.Equal(0, await UnreadAsync(s.Lena));
        Assert.Equal(0, await UnreadAsync(s.Otto)); // not enrolled
    }

    [Fact]
    public async Task Draft_courses_have_no_chat()
    {
        var s = await CreateAsync();
        var draft = await ReadAsync(await s.Admin.Client.PostAsJsonAsync("/api/v1/tenant/courses", new { code = "DRAFT-1", title = "Draft" }));
        var draftId = draft.GetProperty("course").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await s.Admin.Client.PostAsJsonAsync("/api/v1/tenant/messages/conversations/course", new { courseId = draftId })).StatusCode);
    }

    [Fact]
    public async Task Conversations_are_isolated_between_tenants()
    {
        var a = await CreateAsync();
        var b = await CreateAsync();
        var id = await DirectAsync(a.Lena, a.Admin);
        await SendAsync(a.Lena, id, "secret");
        Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.Client.GetAsync($"/api/v1/tenant/messages/conversations/{id}/messages")).StatusCode);
        Assert.Equal(0, await UnreadAsync(b.Admin));
    }
}
