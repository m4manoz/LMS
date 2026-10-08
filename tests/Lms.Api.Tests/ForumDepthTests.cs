using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

/// <summary>Editing forum posts with their history kept, and attachments on posts.</summary>
public sealed class ForumDepthTests : IClassFixture<LmsApiFactory>
{
    private readonly TestWorld _world;
    public ForumDepthTests(LmsApiFactory factory) => _world = new TestWorld(factory);

    private const string Base = "/api/v1/tenant/community";

    private sealed record Forum(Tenant Tenant, Person Lena, Person Cleo, Person Teacher);

    private async Task<Forum> NewForumAsync()
    {
        var tenant = await _world.NewTenantAsync();
        return new Forum(tenant, await _world.AddLearnerAsync(tenant, "Lena"), await _world.AddLearnerAsync(tenant, "Cleo"), await _world.AddPersonAsync(tenant, "Tara", "TEACHER"));
    }

    private static async Task<Guid> ThreadAsync(Person by, string title = "How do fractions work?", string body = "I do not get it.")
    {
        var response = await by.Client.PostAsJsonAsync($"{Base}/threads", new { title, body });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> ReplyAsync(Person by, Guid threadId, string body = "Try this.")
    {
        (await by.Client.PostAsJsonAsync($"{Base}/threads/{threadId}/replies", new { body })).EnsureSuccessStatusCode();
        return (await ReadAsync(await by.Client.GetAsync($"{Base}/threads/{threadId}"))).GetProperty("replies").EnumerateArray().Last().GetProperty("id").GetGuid();
    }

    private static MultipartFormDataContent Upload(string fileName, string text, Guid? replyId = null)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        content.Add(file, "file", fileName);
        if (replyId is Guid id) content.Add(new StringContent(id.ToString()), "replyId");
        return content;
    }

    private static async Task<JsonElement> DetailAsync(Person who, Guid threadId) => await ReadAsync(await who.Client.GetAsync($"{Base}/threads/{threadId}"));

    // ---------- editing and history ----------
    [Fact]
    public async Task An_author_can_edit_a_thread_and_the_earlier_words_are_kept_as_history()
    {
        var f = await NewForumAsync();
        var id = await ThreadAsync(f.Lena);
        Assert.Equal(HttpStatusCode.NoContent, (await f.Lena.Client.PutAsJsonAsync($"{Base}/threads/{id}", new { title = "How do fractions work? (week 3)", body = "I do not get adding them." })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await f.Lena.Client.PutAsJsonAsync($"{Base}/threads/{id}", new { title = "How do fractions work? (week 3)", body = "I do not get adding unlike ones." })).StatusCode);

        var detail = await DetailAsync(f.Cleo, id);
        Assert.Equal("I do not get adding unlike ones.", detail.GetProperty("body").GetString());
        Assert.NotEqual(JsonValueKind.Null, detail.GetProperty("editedAtUtc").ValueKind);

        var history = (await ReadAsync(await f.Lena.Client.GetAsync($"{Base}/threads/{id}/history"))).EnumerateArray().ToList();
        Assert.Equal(2, history.Count);
        Assert.Equal("I do not get adding them.", history[0].GetProperty("previousBody").GetString());   // newest first
        Assert.Equal("I do not get it.", history[1].GetProperty("previousBody").GetString());
        Assert.Equal("How do fractions work?", history[1].GetProperty("previousTitle").GetString());
        Assert.Equal("Lena", history[0].GetProperty("editedByName").GetString());
    }

    [Fact]
    public async Task Saving_a_post_unchanged_leaves_no_history_and_no_edited_mark()
    {
        var f = await NewForumAsync();
        var id = await ThreadAsync(f.Lena, "Same title", "Same body");
        Assert.Equal(HttpStatusCode.NoContent, (await f.Lena.Client.PutAsJsonAsync($"{Base}/threads/{id}", new { title = "Same title", body = "  Same body " })).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await DetailAsync(f.Lena, id)).GetProperty("editedAtUtc").ValueKind);
        Assert.Empty((await ReadAsync(await f.Lena.Client.GetAsync($"{Base}/threads/{id}/history"))).EnumerateArray());
    }

    [Fact]
    public async Task A_reply_can_be_edited_with_its_own_history()
    {
        var f = await NewForumAsync();
        var thread = await ThreadAsync(f.Lena);
        var reply = await ReplyAsync(f.Cleo, thread, "Add the tops");
        Assert.Equal(HttpStatusCode.NoContent, (await f.Cleo.Client.PutAsJsonAsync($"{Base}/replies/{reply}", new { body = "Add the tops after matching the bottoms" })).StatusCode);

        var shown = (await DetailAsync(f.Lena, thread)).GetProperty("replies")[0];
        Assert.Equal("Add the tops after matching the bottoms", shown.GetProperty("body").GetString());
        Assert.NotEqual(JsonValueKind.Null, shown.GetProperty("editedAtUtc").ValueKind);
        var history = Assert.Single((await ReadAsync(await f.Cleo.Client.GetAsync($"{Base}/replies/{reply}/history"))).EnumerateArray());
        Assert.Equal("Add the tops", history.GetProperty("previousBody").GetString());
        Assert.Empty((await ReadAsync(await f.Lena.Client.GetAsync($"{Base}/threads/{thread}/history"))).EnumerateArray());   // the thread's own history is separate
    }

    [Fact]
    public async Task Only_the_author_and_moderators_edit_or_read_history_and_a_moderator_edit_is_recorded_as_theirs()
    {
        var f = await NewForumAsync();
        var id = await ThreadAsync(f.Lena);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Cleo.Client.PutAsJsonAsync($"{Base}/threads/{id}", new { title = "Hijacked title", body = "Hijacked" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Cleo.Client.GetAsync($"{Base}/threads/{id}/history")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await f.Teacher.Client.PutAsJsonAsync($"{Base}/threads/{id}", new { title = "How do fractions work?", body = "I do not get it. [edited by moderator]" })).StatusCode);
        var history = Assert.Single((await ReadAsync(await f.Lena.Client.GetAsync($"{Base}/threads/{id}/history"))).EnumerateArray());
        Assert.Equal("Tara", history.GetProperty("editedByName").GetString());   // the author can see that a moderator changed it
        Assert.Equal(HttpStatusCode.OK, (await f.Teacher.Client.GetAsync($"{Base}/threads/{id}/history")).StatusCode);
    }

    [Fact]
    public async Task Edits_are_checked_and_a_locked_thread_only_lets_moderators_edit()
    {
        var f = await NewForumAsync();
        var id = await ThreadAsync(f.Lena);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Lena.Client.PutAsJsonAsync($"{Base}/threads/{id}", new { title = "Hi", body = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Lena.Client.PutAsJsonAsync($"{Base}/threads/{id}", new { title = "Fine title", body = "  " })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Lena.Client.PutAsJsonAsync($"{Base}/threads/{Guid.NewGuid()}", new { title = "Fine title", body = "x" })).StatusCode);

        var reply = await ReplyAsync(f.Cleo, id);
        (await f.Teacher.Client.PostAsJsonAsync($"{Base}/threads/{id}/lock", new { value = true })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await f.Lena.Client.PutAsJsonAsync($"{Base}/threads/{id}", new { title = "Fine title", body = "after lock" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Cleo.Client.PutAsJsonAsync($"{Base}/replies/{reply}", new { body = "after lock" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await f.Teacher.Client.PutAsJsonAsync($"{Base}/threads/{id}", new { title = "Fine title", body = "moderator can" })).StatusCode);
    }

    // ---------- attachments ----------
    [Fact]
    public async Task A_thread_and_a_reply_each_carry_files_anyone_can_download()
    {
        var f = await NewForumAsync();
        var thread = await ThreadAsync(f.Lena);
        var reply = await ReplyAsync(f.Cleo, thread);
        var onThread = await ReadAsync(await f.Lena.Client.PostAsync($"{Base}/threads/{thread}/attachments", Upload("question.txt", "page 4")));
        var onReply = await ReadAsync(await f.Cleo.Client.PostAsync($"{Base}/threads/{thread}/attachments", Upload("worked.txt", "1/2 + 1/3 = 5/6", reply)));
        Assert.Equal("question.txt", onThread.GetProperty("fileName").GetString());

        var detail = await DetailAsync(f.Teacher, thread);
        Assert.Equal(new[] { "question.txt" }, detail.GetProperty("attachments").EnumerateArray().Select(item => item.GetProperty("fileName").GetString()).ToArray());
        Assert.Equal(new[] { "worked.txt" }, detail.GetProperty("replies")[0].GetProperty("attachments").EnumerateArray().Select(item => item.GetProperty("fileName").GetString()).ToArray());

        var download = await f.Teacher.Client.GetAsync($"{Base}/attachments/{onReply.GetProperty("id").GetGuid()}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("1/2 + 1/3 = 5/6", await download.Content.ReadAsStringAsync());
        Assert.Equal("worked.txt", download.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal(HttpStatusCode.NotFound, (await f.Teacher.Client.GetAsync($"{Base}/attachments/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Only_the_author_or_a_moderator_attaches_or_removes_files()
    {
        var f = await NewForumAsync();
        var thread = await ThreadAsync(f.Lena);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Cleo.Client.PostAsync($"{Base}/threads/{thread}/attachments", Upload("sneaky.txt", "x"))).StatusCode);
        var attachment = (await ReadAsync(await f.Lena.Client.PostAsync($"{Base}/threads/{thread}/attachments", Upload("mine.txt", "x")))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Cleo.Client.DeleteAsync($"{Base}/attachments/{attachment}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await f.Lena.Client.DeleteAsync($"{Base}/attachments/{attachment}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Lena.Client.GetAsync($"{Base}/attachments/{attachment}")).StatusCode);
        Assert.Empty((await DetailAsync(f.Lena, thread)).GetProperty("attachments").EnumerateArray());

        var other = (await ReadAsync(await f.Lena.Client.PostAsync($"{Base}/threads/{thread}/attachments", Upload("moderated.txt", "x")))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await f.Teacher.Client.DeleteAsync($"{Base}/attachments/{other}")).StatusCode);
    }

    [Fact]
    public async Task Attachments_are_limited_in_number_type_and_size_and_blocked_on_locked_threads()
    {
        var f = await NewForumAsync();
        var thread = await ThreadAsync(f.Lena);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Lena.Client.PostAsync($"{Base}/threads/{thread}/attachments", Upload("run.exe", "MZ"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Lena.Client.PostAsync($"{Base}/threads/{thread}/attachments", new MultipartFormDataContent())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Lena.Client.PostAsync($"{Base}/threads/{Guid.NewGuid()}/attachments", Upload("a.txt", "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Lena.Client.PostAsync($"{Base}/threads/{thread}/attachments", Upload("a.txt", "x", Guid.NewGuid()))).StatusCode);   // a reply that is not in this thread

        for (var index = 1; index <= 5; index++) Assert.Equal(HttpStatusCode.Created, (await f.Lena.Client.PostAsync($"{Base}/threads/{thread}/attachments", Upload($"f{index}.txt", "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Lena.Client.PostAsync($"{Base}/threads/{thread}/attachments", Upload("f6.txt", "x"))).StatusCode);

        var locked = await ThreadAsync(f.Lena, "Locked soon");
        (await f.Teacher.Client.PostAsJsonAsync($"{Base}/threads/{locked}/lock", new { value = true })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await f.Lena.Client.PostAsync($"{Base}/threads/{locked}/attachments", Upload("late.txt", "x"))).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_reply_or_thread_takes_its_files_and_history_with_it()
    {
        var f = await NewForumAsync();
        var thread = await ThreadAsync(f.Lena);
        var reply = await ReplyAsync(f.Cleo, thread, "Original");
        var onThread = (await ReadAsync(await f.Lena.Client.PostAsync($"{Base}/threads/{thread}/attachments", Upload("t.txt", "t")))).GetProperty("id").GetGuid();
        var onReply = (await ReadAsync(await f.Cleo.Client.PostAsync($"{Base}/threads/{thread}/attachments", Upload("r.txt", "r", reply)))).GetProperty("id").GetGuid();
        await f.Cleo.Client.PutAsJsonAsync($"{Base}/replies/{reply}", new { body = "Changed" });

        Assert.Equal(HttpStatusCode.NoContent, (await f.Cleo.Client.DeleteAsync($"{Base}/replies/{reply}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Lena.Client.GetAsync($"{Base}/attachments/{onReply}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.Lena.Client.GetAsync($"{Base}/attachments/{onThread}")).StatusCode);   // the thread's own file stays

        Assert.Equal(HttpStatusCode.NoContent, (await f.Lena.Client.DeleteAsync($"{Base}/threads/{thread}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Lena.Client.GetAsync($"{Base}/attachments/{onThread}")).StatusCode);
    }

    [Fact]
    public async Task Other_organizations_see_none_of_it()
    {
        var f = await NewForumAsync();
        var thread = await ThreadAsync(f.Lena);
        var attachment = (await ReadAsync(await f.Lena.Client.PostAsync($"{Base}/threads/{thread}/attachments", Upload("t.txt", "t")))).GetProperty("id").GetGuid();
        var other = await _world.NewTenantAsync();
        var foreigner = await _world.AddLearnerAsync(other, "Fay");
        Assert.Equal(HttpStatusCode.NotFound, (await foreigner.Client.GetAsync($"{Base}/attachments/{attachment}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreigner.Client.GetAsync($"{Base}/threads/{thread}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreigner.Client.PutAsJsonAsync($"{Base}/threads/{thread}", new { title = "Takeover", body = "x" })).StatusCode);
    }
}
