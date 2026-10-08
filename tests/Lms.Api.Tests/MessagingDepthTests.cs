using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

/// <summary>Editing, deleting and attaching files to messages, and being told about them as they happen.</summary>
public sealed class MessagingDepthTests : IClassFixture<LmsApiFactory>
{
    private readonly TestWorld _world;
    private readonly LmsApiFactory _factory;
    public MessagingDepthTests(LmsApiFactory factory) { _factory = factory; _world = new TestWorld(factory); }

    private sealed record Chat(Tenant Tenant, Person Lena, Person Teacher, Guid ConversationId);

    private async Task<Chat> NewChatAsync()
    {
        var tenant = await _world.NewTenantAsync();
        var lena = await _world.AddLearnerAsync(tenant, "Lena");
        var teacher = await _world.AddPersonAsync(tenant, "Tara", "TEACHER");
        var response = await lena.Client.PostAsJsonAsync("/api/v1/tenant/messages/conversations/direct", new { userId = teacher.Id });
        response.EnsureSuccessStatusCode();
        return new Chat(tenant, lena, teacher, (await ReadAsync(response)).GetProperty("id").GetGuid());
    }

    private static string Url(Chat chat, string suffix = "") => $"/api/v1/tenant/messages/conversations/{chat.ConversationId}/messages{suffix}";

    private static async Task<Guid> SendAsync(Person from, Chat chat, string body)
    {
        var response = await from.Client.PostAsJsonAsync(Url(chat), new { body });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    private static MultipartFormDataContent Upload(string fileName, string text, string? body = null)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        content.Add(file, "file", fileName);
        if (body is not null) content.Add(new StringContent(body), "body");
        return content;
    }

    private static async Task<List<JsonElement>> MessagesAsync(Person who, Chat chat)
        => (await ReadAsync(await who.Client.GetAsync(Url(chat)))).EnumerateArray().ToList();

    // ---------- editing ----------
    [Fact]
    public async Task A_sender_can_edit_their_message_and_both_people_see_it_marked_edited()
    {
        var chat = await NewChatAsync();
        var id = await SendAsync(chat.Lena, chat, "Helo teacher");
        var edited = await ReadAsync(await chat.Lena.Client.PutAsJsonAsync(Url(chat, $"/{id}"), new { body = "  Hello teacher  " }));
        Assert.Equal("Hello teacher", edited.GetProperty("body").GetString());
        Assert.NotEqual(JsonValueKind.Null, edited.GetProperty("editedAtUtc").ValueKind);

        var seen = (await MessagesAsync(chat.Teacher, chat)).Single();
        Assert.Equal("Hello teacher", seen.GetProperty("body").GetString());
        Assert.NotEqual(JsonValueKind.Null, seen.GetProperty("editedAtUtc").ValueKind);
        var untouched = await SendAsync(chat.Lena, chat, "Second");
        Assert.Equal(JsonValueKind.Null, (await MessagesAsync(chat.Teacher, chat)).Single(item => item.GetProperty("id").GetGuid() == untouched).GetProperty("editedAtUtc").ValueKind);
    }

    [Fact]
    public async Task Saving_the_same_words_again_is_not_an_edit()
    {
        var chat = await NewChatAsync();
        var id = await SendAsync(chat.Lena, chat, "Same");
        await chat.Lena.Client.PutAsJsonAsync(Url(chat, $"/{id}"), new { body = "Same" });
        Assert.Equal(JsonValueKind.Null, (await MessagesAsync(chat.Lena, chat)).Single().GetProperty("editedAtUtc").ValueKind);
    }

    [Fact]
    public async Task Nobody_edits_someone_elses_message_not_even_staff_and_the_words_are_checked()
    {
        var chat = await NewChatAsync();
        var id = await SendAsync(chat.Lena, chat, "Mine");
        Assert.Equal(HttpStatusCode.Forbidden, (await chat.Teacher.Client.PutAsJsonAsync(Url(chat, $"/{id}"), new { body = "Rewritten" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await chat.Tenant.Admin.PutAsJsonAsync(Url(chat, $"/{id}"), new { body = "Rewritten" })).StatusCode);   // an admin is not in someone else's direct chat
        Assert.Equal(HttpStatusCode.BadRequest, (await chat.Lena.Client.PutAsJsonAsync(Url(chat, $"/{id}"), new { body = "   " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await chat.Lena.Client.PutAsJsonAsync(Url(chat, $"/{id}"), new { body = new string('x', 5001) })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await chat.Lena.Client.PutAsJsonAsync(Url(chat, $"/{Guid.NewGuid()}"), new { body = "x" })).StatusCode);
        Assert.Equal("Mine", (await MessagesAsync(chat.Teacher, chat)).Single().GetProperty("body").GetString());
    }

    // ---------- deleting ----------
    [Fact]
    public async Task A_deleted_message_stays_as_a_marker_with_its_words_gone_and_stops_counting_as_unread()
    {
        var chat = await NewChatAsync();
        var id = await SendAsync(chat.Lena, chat, "Oops, wrong chat");
        Assert.Equal(1, (await ReadAsync(await chat.Teacher.Client.GetAsync("/api/v1/tenant/messages/unread-count"))).GetProperty("count").GetInt32());

        Assert.Equal(HttpStatusCode.NoContent, (await chat.Lena.Client.DeleteAsync(Url(chat, $"/{id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await chat.Lena.Client.DeleteAsync(Url(chat, $"/{id}"))).StatusCode);   // doing it again is harmless

        var seen = (await MessagesAsync(chat.Teacher, chat)).Single();
        Assert.True(seen.GetProperty("isDeleted").GetBoolean());
        Assert.Equal("", seen.GetProperty("body").GetString());
        Assert.Equal(0, (await ReadAsync(await chat.Teacher.Client.GetAsync("/api/v1/tenant/messages/unread-count"))).GetProperty("count").GetInt32());
        var summary = (await ReadAsync(await chat.Teacher.Client.GetAsync("/api/v1/tenant/messages/conversations"))).EnumerateArray().Single();
        Assert.Equal("Message deleted", summary.GetProperty("lastMessagePreview").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await chat.Lena.Client.PutAsJsonAsync(Url(chat, $"/{id}"), new { body = "edit after delete" })).StatusCode);
    }

    [Fact]
    public async Task Only_the_sender_deletes_in_a_direct_chat()
    {
        var chat = await NewChatAsync();
        var id = await SendAsync(chat.Lena, chat, "Keep me");
        Assert.Equal(HttpStatusCode.Forbidden, (await chat.Teacher.Client.DeleteAsync(Url(chat, $"/{id}"))).StatusCode);
        Assert.False((await MessagesAsync(chat.Lena, chat)).Single().GetProperty("isDeleted").GetBoolean());
    }

    [Fact]
    public async Task A_moderator_can_remove_anyones_message_from_a_course_chat_but_a_classmate_cannot()
    {
        var tenant = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(tenant, "CHAT");
        var lena = await _world.AddLearnerAsync(tenant, "Lena");
        var cleo = await _world.AddLearnerAsync(tenant, "Cleo");
        foreach (var learner in new[] { lena, cleo }) Assert.True((await EnrollAsync(learner, course)).IsSuccessStatusCode);
        var opened = await ReadAsync(await lena.Client.PostAsJsonAsync("/api/v1/tenant/messages/conversations/course", new { courseId = course.Id }));
        var conversationId = opened.GetProperty("id").GetGuid();
        var url = $"/api/v1/tenant/messages/conversations/{conversationId}/messages";
        var sent = await ReadAsync(await lena.Client.PostAsJsonAsync(url, new { body = "Something rude" }));
        var messageId = sent.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await cleo.Client.DeleteAsync($"{url}/{messageId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await tenant.Admin.DeleteAsync($"{url}/{messageId}")).StatusCode);
        Assert.True((await ReadAsync(await cleo.Client.GetAsync(url)))[0].GetProperty("isDeleted").GetBoolean());
    }

    // ---------- attachments ----------
    [Fact]
    public async Task A_file_travels_with_a_message_and_the_other_person_downloads_it()
    {
        var chat = await NewChatAsync();
        var sent = await ReadAsync(await chat.Lena.Client.PostAsync(Url(chat, "/upload"), Upload("homework.txt", "my homework", "Here it is")));
        Assert.Equal("Here it is", sent.GetProperty("body").GetString());
        Assert.Equal("homework.txt", sent.GetProperty("attachment").GetProperty("fileName").GetString());
        var id = sent.GetProperty("id").GetGuid();

        var seen = (await MessagesAsync(chat.Teacher, chat)).Single();
        Assert.Equal(11, seen.GetProperty("attachment").GetProperty("sizeBytes").GetInt64());
        var download = await chat.Teacher.Client.GetAsync(Url(chat, $"/{id}/attachment"));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("my homework", await download.Content.ReadAsStringAsync());
        Assert.Equal("homework.txt", download.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
    }

    [Fact]
    public async Task A_message_can_be_just_a_file_and_the_inbox_says_so()
    {
        var chat = await NewChatAsync();
        Assert.Equal(HttpStatusCode.Created, (await chat.Lena.Client.PostAsync(Url(chat, "/upload"), Upload("notes.txt", "x"))).StatusCode);
        var summary = (await ReadAsync(await chat.Teacher.Client.GetAsync("/api/v1/tenant/messages/conversations"))).EnumerateArray().Single();
        Assert.Equal("Attachment: notes.txt", summary.GetProperty("lastMessagePreview").GetString());
        // An edit cannot empty a message that has a file, but a message with neither is refused.
        var id = (await MessagesAsync(chat.Lena, chat)).Single().GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await chat.Lena.Client.PutAsJsonAsync(Url(chat, $"/{id}"), new { body = "" })).StatusCode);
    }

    [Fact]
    public async Task Risky_empty_missing_and_oversized_files_are_refused_and_outsiders_cannot_fetch_one()
    {
        var chat = await NewChatAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await chat.Lena.Client.PostAsync(Url(chat, "/upload"), Upload("run.exe", "MZ"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await chat.Lena.Client.PostAsync(Url(chat, "/upload"), Upload("page.html", "<script>"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await chat.Lena.Client.PostAsync(Url(chat, "/upload"), new MultipartFormDataContent())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await chat.Lena.Client.PostAsync(Url(chat, "/upload"), Upload("a.txt", "x", new string('y', 5001)))).StatusCode);
        Assert.Empty(await MessagesAsync(chat.Lena, chat));

        var sent = await ReadAsync(await chat.Lena.Client.PostAsync(Url(chat, "/upload"), Upload("secret.txt", "private")));
        var id = sent.GetProperty("id").GetGuid();
        var stranger = await _world.AddLearnerAsync(chat.Tenant, "Sam");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Client.GetAsync(Url(chat, $"/{id}/attachment"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Client.PostAsync(Url(chat, "/upload"), Upload("a.txt", "x"))).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_message_takes_its_file_with_it()
    {
        var chat = await NewChatAsync();
        var sent = await ReadAsync(await chat.Lena.Client.PostAsync(Url(chat, "/upload"), Upload("gone.txt", "bye")));
        var id = sent.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await chat.Teacher.Client.GetAsync(Url(chat, $"/{id}/attachment"))).StatusCode);
        await chat.Lena.Client.DeleteAsync(Url(chat, $"/{id}"));
        Assert.Equal(HttpStatusCode.NotFound, (await chat.Teacher.Client.GetAsync(Url(chat, $"/{id}/attachment"))).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await MessagesAsync(chat.Teacher, chat)).Single().GetProperty("attachment").ValueKind);
    }

    // ---------- live updates ----------
    /// <summary>Opens the live stream and returns a reader positioned after the greeting.</summary>
    private static async Task<(HttpResponseMessage Response, StreamReader Reader, CancellationTokenSource Cancel)> OpenStreamAsync(Person person)
    {
        var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var response = await person.Client.GetAsync("/api/v1/tenant/messages/stream", HttpCompletionOption.ResponseHeadersRead, cancel.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancel.Token));
        Assert.Equal(": connected", await reader.ReadLineAsync(cancel.Token));
        await reader.ReadLineAsync(cancel.Token);   // the blank line that ends the greeting
        return (response, reader, cancel);
    }

    private static async Task<JsonElement> NextEventAsync(StreamReader reader, TimeSpan wait)
    {
        using var timeout = new CancellationTokenSource(wait);
        string? type = null, data = null;
        while (true)
        {
            var line = await reader.ReadLineAsync(timeout.Token);
            if (line is null) throw new EndOfStreamException();
            if (line.StartsWith("event: ")) type = line[7..];
            else if (line.StartsWith("data: ")) data = line[6..];
            else if (line.Length == 0 && data is not null) { Assert.Equal("message", type); return JsonDocument.Parse(data).RootElement.Clone(); }
        }
    }

    [Fact]
    public async Task The_other_person_is_told_at_once_about_a_new_edited_and_deleted_message()
    {
        var chat = await NewChatAsync();
        var (response, reader, cancel) = await OpenStreamAsync(chat.Teacher);
        using (response) using (cancel)
        {
            var id = await SendAsync(chat.Lena, chat, "Live!");
            var created = await NextEventAsync(reader, TimeSpan.FromSeconds(5));
            Assert.Equal(("message", chat.ConversationId, id), (created.GetProperty("type").GetString(), created.GetProperty("conversationId").GetGuid(), created.GetProperty("messageId").GetGuid()));

            await chat.Lena.Client.PutAsJsonAsync(Url(chat, $"/{id}"), new { body = "Live, edited" });
            Assert.Equal("edited", (await NextEventAsync(reader, TimeSpan.FromSeconds(5))).GetProperty("type").GetString());
            await chat.Lena.Client.DeleteAsync(Url(chat, $"/{id}"));
            Assert.Equal("deleted", (await NextEventAsync(reader, TimeSpan.FromSeconds(5))).GetProperty("type").GetString());
            cancel.Cancel();
        }
    }

    [Fact]
    public async Task People_outside_the_conversation_and_other_organizations_hear_nothing()
    {
        var chat = await NewChatAsync();
        var bystander = await _world.AddLearnerAsync(chat.Tenant, "Bea");
        var otherTenant = await _world.NewTenantAsync();
        var foreigner = await _world.AddLearnerAsync(otherTenant, "Fay");
        var (r1, bystanderReader, c1) = await OpenStreamAsync(bystander);
        var (r2, foreignReader, c2) = await OpenStreamAsync(foreigner);
        var (r3, teacherReader, c3) = await OpenStreamAsync(chat.Teacher);
        using (r1) using (r2) using (r3) using (c1) using (c2) using (c3)
        {
            await SendAsync(chat.Lena, chat, "Private");
            await NextEventAsync(teacherReader, TimeSpan.FromSeconds(5));   // the real recipient hears it, so the others have had their chance
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NextEventAsync(bystanderReader, TimeSpan.FromMilliseconds(700)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NextEventAsync(foreignReader, TimeSpan.FromMilliseconds(700)));
            c1.Cancel(); c2.Cancel(); c3.Cancel();
        }
    }

    [Fact]
    public async Task A_course_chat_signals_enrolled_learners_and_staff_but_not_other_learners()
    {
        var tenant = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(tenant, "LIVE");
        var lena = await _world.AddLearnerAsync(tenant, "Lena");
        var cleo = await _world.AddLearnerAsync(tenant, "Cleo");
        var outsider = await _world.AddLearnerAsync(tenant, "Otto");
        foreach (var learner in new[] { lena, cleo }) Assert.True((await EnrollAsync(learner, course)).IsSuccessStatusCode);
        var conversationId = (await ReadAsync(await lena.Client.PostAsJsonAsync("/api/v1/tenant/messages/conversations/course", new { courseId = course.Id }))).GetProperty("id").GetGuid();

        var (r1, cleoReader, c1) = await OpenStreamAsync(cleo);
        var (r2, outsiderReader, c2) = await OpenStreamAsync(outsider);
        var (r3, adminReader, c3) = await OpenStreamAsync(new Person("Admin", "", tenant.AdminId, tenant.Admin));
        using (r1) using (r2) using (r3) using (c1) using (c2) using (c3)
        {
            (await lena.Client.PostAsJsonAsync($"/api/v1/tenant/messages/conversations/{conversationId}/messages", new { body = "Hello class" })).EnsureSuccessStatusCode();
            Assert.Equal(conversationId, (await NextEventAsync(cleoReader, TimeSpan.FromSeconds(5))).GetProperty("conversationId").GetGuid());
            Assert.Equal(conversationId, (await NextEventAsync(adminReader, TimeSpan.FromSeconds(5))).GetProperty("conversationId").GetGuid());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NextEventAsync(outsiderReader, TimeSpan.FromMilliseconds(700)));
            c1.Cancel(); c2.Cancel(); c3.Cancel();
        }
    }

    [Fact]
    public async Task The_stream_needs_a_signed_in_person()
    {
        var chat = await NewChatAsync();
        var anonymous = _factory.CreateTenantClient(chat.Tenant.Slug);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/tenant/messages/stream", HttpCompletionOption.ResponseHeadersRead)).StatusCode);
    }
}
