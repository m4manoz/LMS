using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Lms.Api.Features.Videos;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

public sealed class VideoTagRulesTests
{
    [Fact]
    public void Tags_are_trimmed_lowercased_squeezed_and_made_unique()
    {
        var (tags, error) = VideoTags.Clean(["  Algebra ", "algebra", "Week  1", "", null, "pre-calculus"]);
        Assert.Null(error);
        Assert.Equal(["algebra", "week 1", "pre-calculus"], tags);
    }

    [Theory]
    [InlineData("a|b")]
    [InlineData("<script>")]
    [InlineData("-leading")]
    [InlineData("ten,comma")]
    public void Tags_with_odd_characters_are_refused(string tag) => Assert.NotNull(VideoTags.Clean([tag]).Error);

    [Fact]
    public void A_tag_that_is_too_long_or_too_many_tags_are_refused()
    {
        Assert.NotNull(VideoTags.Clean([new string('a', 31)]).Error);
        Assert.Null(VideoTags.Clean([new string('a', 30)]).Error);
        Assert.NotNull(VideoTags.Clean(Enumerable.Range(0, 11).Select(index => $"tag {index}")).Error);
        Assert.Null(VideoTags.Clean(Enumerable.Range(0, 10).Select(index => $"tag {index}")).Error);
    }

    [Fact]
    public void Tags_are_stored_so_one_can_be_looked_for_exactly()
    {
        var stored = VideoTags.Store(["algebra", "week 1"]);
        Assert.Equal("|algebra|week 1|", stored);
        Assert.Contains(VideoTags.Pattern("algebra"), stored);
        Assert.DoesNotContain(VideoTags.Pattern("alg"), stored);
        Assert.Equal(["algebra", "week 1"], VideoTags.Read(stored));
        Assert.Equal("", VideoTags.Store([]));
        Assert.Empty(VideoTags.Read(""));
    }
}

/// <summary>Finding videos in a large library (tags, ordering) and keeping the library within its room.</summary>
public sealed class VideoOrganizingTests
{
    private const string Videos = "/api/v1/tenant/videos";
    private static readonly byte[] Mp4 = [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'m', (byte)'p', (byte)'4', (byte)'2', 1, 2, 3, 4, 5, 6, 7, 8];

    private sealed record Setup(TestWorld World, Tenant Tenant, Person Teacher, Person Ada, Person Outsider, CourseInfo Course);

    private static async Task<Setup> NewSetupAsync(Dictionary<string, string?>? more = null)
    {
        var factory = new LmsApiFactory { Transcoder = new FakeVideoTranscoder { Enabled = false }, ExtraSettings = more };
        var world = new TestWorld(factory);
        var t = await world.NewTenantAsync();
        var teacher = await world.AddPersonAsync(t, "Tara", "TEACHER");
        var ada = await world.AddLearnerAsync(t, "Ada");
        var outsider = await world.AddLearnerAsync(t, "Olga");
        var course = await world.NewCourseAsync(t, "ORG-1");
        Assert.True((await EnrollAsync(ada, course)).IsSuccessStatusCode);
        return new Setup(world, t, teacher, ada, outsider, course);
    }

    private static async Task<HttpResponseMessage> TryUploadAsync(Setup s, string title, byte[]? bytes = null, int seconds = 60)
    {
        var form = new MultipartFormDataContent { { new StringContent(s.Course.Id.ToString()), "courseId" }, { new StringContent(title), "title" }, { new StringContent(seconds.ToString()), "durationSeconds" } };
        var file = new ByteArrayContent(bytes ?? Mp4); file.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        form.Add(file, "file", $"{title}.mp4");
        return await s.Teacher.Client.PostAsync(Videos, form);
    }

    private static async Task<Guid> UploadAsync(Setup s, string title, byte[]? bytes = null, int seconds = 60)
    {
        var response = await TryUploadAsync(s, title, bytes, seconds);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> TagAsync(Setup s, Guid id, string title, params string[] tags)
        => s.Teacher.Client.PutAsJsonAsync($"{Videos}/{id}", new { title, tags });

    private static async Task<string[]> TitlesAsync(Person who, string query) => (await ReadAsync(await who.Client.GetAsync($"{Videos}{query}"))).EnumerateArray().Select(item => item.GetProperty("title").GetString()!).ToArray();

    [Fact]
    public async Task Staff_tag_videos_and_the_tags_come_back_cleaned_in_the_video()
    {
        var s = await NewSetupAsync();
        var id = await UploadAsync(s, "Limits");
        var saved = await TagAsync(s, id, "Limits", "  Algebra ", "week 1", "ALGEBRA");
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(["algebra", "week 1"], (await ReadAsync(saved)).GetProperty("tags").EnumerateArray().Select(item => item.GetString()).ToArray());
        Assert.Equal(["algebra", "week 1"], (await ReadAsync(await s.Ada.Client.GetAsync($"{Videos}/{id}"))).GetProperty("tags").EnumerateArray().Select(item => item.GetString()).ToArray());

        // Leaving the tags out changes nothing; sending none clears them.
        Assert.Equal(HttpStatusCode.OK, (await s.Teacher.Client.PutAsJsonAsync($"{Videos}/{id}", new { title = "Limits again" })).StatusCode);
        Assert.Equal(2, (await ReadAsync(await s.Ada.Client.GetAsync($"{Videos}/{id}"))).GetProperty("tags").GetArrayLength());
        Assert.Empty((await ReadAsync(await TagAsync(s, id, "Limits again"))).GetProperty("tags").EnumerateArray());
    }

    [Fact]
    public async Task Bad_tags_are_refused_and_learners_cannot_tag()
    {
        var s = await NewSetupAsync();
        var id = await UploadAsync(s, "Limits");
        Assert.Equal(HttpStatusCode.BadRequest, (await TagAsync(s, id, "Limits", "<b>bold</b>")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await TagAsync(s, id, "Limits", Enumerable.Range(0, 11).Select(index => $"t{index}").ToArray())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Ada.Client.PutAsJsonAsync($"{Videos}/{id}", new { title = "x", tags = new[] { "mine" } })).StatusCode);
    }

    [Fact]
    public async Task The_library_can_be_narrowed_to_a_tag_exactly_and_searched_by_tag()
    {
        var s = await NewSetupAsync();
        var limits = await UploadAsync(s, "Limits"); var series = await UploadAsync(s, "Series"); await UploadAsync(s, "Untagged");
        await TagAsync(s, limits, "Limits", "calculus", "week 1");
        await TagAsync(s, series, "Series", "calculus", "week 2", "advanced");

        Assert.Equal(["Series", "Limits"], await TitlesAsync(s.Ada, "?tag=calculus"));                       // newest first
        Assert.Equal(["Limits"], await TitlesAsync(s.Ada, "?tag=week%201"));
        Assert.Equal(["Series"], await TitlesAsync(s.Ada, "?tag=ADVANCED"));
        Assert.Empty(await TitlesAsync(s.Ada, "?tag=calc"));                                                   // a tag matches whole, not by part
        Assert.Equal(["Series"], await TitlesAsync(s.Ada, "?search=advanced"));                                // the search finds tags too
    }

    [Fact]
    public async Task The_tags_in_use_are_counted_for_the_people_who_can_see_the_videos()
    {
        var s = await NewSetupAsync();
        var one = await UploadAsync(s, "One"); var two = await UploadAsync(s, "Two");
        await TagAsync(s, one, "One", "calculus", "week 1");
        await TagAsync(s, two, "Two", "calculus");
        var tags = (await ReadAsync(await s.Ada.Client.GetAsync($"{Videos}/tags"))).EnumerateArray().Select(item => (item.GetProperty("tag").GetString(), item.GetProperty("count").GetInt32())).ToArray();
        Assert.Equal([("calculus", 2), ("week 1", 1)], tags);
        Assert.Empty(await TitlesAsync(s.Outsider, "?tag=calculus"));                                          // not enrolled: sees none
        Assert.Empty((await ReadAsync(await s.Outsider.Client.GetAsync($"{Videos}/tags"))).EnumerateArray());
    }

    [Fact]
    public async Task The_list_can_be_put_in_other_orders()
    {
        var s = await NewSetupAsync();
        var big = await UploadAsync(s, "Beta", new byte[2000].Select((_, index) => index < Mp4.Length ? Mp4[index] : (byte)0).ToArray(), seconds: 30);
        await UploadAsync(s, "Alpha", seconds: 90);
        await UploadAsync(s, "Gamma", seconds: 60);
        Assert.Equal(["Gamma", "Alpha", "Beta"], await TitlesAsync(s.Teacher, ""));
        Assert.Equal(["Beta", "Alpha", "Gamma"], await TitlesAsync(s.Teacher, "?sort=oldest"));
        Assert.Equal(["Alpha", "Beta", "Gamma"], await TitlesAsync(s.Teacher, "?sort=title"));
        Assert.Equal(["Alpha", "Gamma", "Beta"], await TitlesAsync(s.Teacher, "?sort=longest"));
        Assert.Equal("Beta", (await TitlesAsync(s.Teacher, "?sort=largest"))[0]);
        Assert.NotEqual(Guid.Empty, big);
    }

    // ---------- room ----------
    [Fact]
    public async Task A_library_with_a_quota_refuses_what_does_not_fit_and_says_how_full_it_is()
    {
        var s = await NewSetupAsync(new() { ["Videos:QuotaMegabytes"] = "1" });
        var usage = await ReadAsync(await s.Teacher.Client.GetAsync($"{Videos}/usage"));
        Assert.Equal(1024 * 1024, usage.GetProperty("quotaBytes").GetInt64());

        var fits = new byte[600 * 1024]; Mp4.CopyTo(fits, 0);
        await UploadAsync(s, "First", fits);
        var second = await TryUploadAsync(s, "Second", fits);                                                    // 1.2 MB in a 1 MB library
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, second.StatusCode);
        Assert.Contains("The video library is full", await second.Content.ReadAsStringAsync());

        var piece = await s.Teacher.Client.PostAsJsonAsync($"{Videos}/uploads", new { courseId = s.Course.Id, title = "Third", fileName = "third.mp4", contentType = "video/mp4", sizeBytes = 600 * 1024 });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, piece.StatusCode);                                    // the same rule before anything is sent in pieces
        await s.Teacher.Client.DeleteAsync($"{Videos}/{(await ReadAsync(await s.Teacher.Client.GetAsync(Videos))).EnumerateArray().Single().GetProperty("id").GetGuid()}");
        Assert.Equal(HttpStatusCode.Created, (await s.Teacher.Client.PostAsJsonAsync($"{Videos}/uploads", new { courseId = s.Course.Id, title = "Third", fileName = "third.mp4", contentType = "video/mp4", sizeBytes = 600 * 1024 })).StatusCode);   // room again
    }

    [Fact]
    public async Task Without_a_quota_there_is_no_limit_to_report()
    {
        var s = await NewSetupAsync();
        Assert.Equal(JsonValueKind.Null, (await ReadAsync(await s.Teacher.Client.GetAsync($"{Videos}/usage"))).GetProperty("quotaBytes").ValueKind);
    }
}
