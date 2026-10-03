using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

/// <summary>The video library: uploads, who can watch, signed playback, progress, analytics, lessons and deleting.</summary>
public sealed class VideoLibraryTests : IClassFixture<LmsApiFactory>
{
    private const string Videos = "/api/v1/tenant/videos";
    private readonly LmsApiFactory _factory;
    private readonly TestWorld _world;

    public VideoLibraryTests(LmsApiFactory factory) { _factory = factory; _world = new TestWorld(factory); }

    /// <summary>The first bytes of a real MP4 file ("ftyp" at offset 4) followed by filler.</summary>
    private static byte[] Mp4(int size = 4096)
    {
        var bytes = new byte[Math.Max(size, 32)];
        new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'m', (byte)'p', (byte)'4', (byte)'2', 0, 0, 0, 0, (byte)'m', (byte)'p', (byte)'4', (byte)'2', (byte)'i', (byte)'s', (byte)'o', (byte)'m' }.CopyTo(bytes, 0);
        for (var i = 24; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);
        return bytes;
    }

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8];

    private static MultipartFormDataContent UploadForm(Guid? courseId, string title = "Intro to limits", byte[]? bytes = null, string contentType = "video/mp4", string fileName = "intro.mp4", int? duration = 600, Guid? lessonId = null, bool withFile = true)
    {
        var form = new MultipartFormDataContent();
        if (courseId is Guid course) form.Add(new StringContent(course.ToString()), "courseId");
        form.Add(new StringContent(title), "title");
        form.Add(new StringContent("About limits"), "description");
        if (duration is int seconds) form.Add(new StringContent(seconds.ToString()), "durationSeconds");
        if (lessonId is Guid lesson) form.Add(new StringContent(lesson.ToString()), "lessonId");
        if (withFile)
        {
            var file = new ByteArrayContent(bytes ?? Mp4());
            file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(file, "file", fileName);
        }
        return form;
    }

    private static async Task<JsonElement> UploadAsync(HttpClient client, Guid courseId, string title = "Intro to limits", int? duration = 600, byte[]? bytes = null)
    {
        var response = await client.PostAsync(Videos, UploadForm(courseId, title, bytes, duration: duration));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync(response);
    }

    private sealed record Setup(Tenant Tenant, Person Teacher, Person Ada, Person Ben, CourseInfo Course);

    /// <summary>A published course with a teacher, an enrolled learner (Ada) and a learner who is not enrolled (Ben).</summary>
    private async Task<Setup> NewSetupAsync(TestWorld? world = null, bool publish = true)
    {
        world ??= _world;
        var t = await world.NewTenantAsync();
        var teacher = await world.AddPersonAsync(t, "Tara", "TEACHER");
        var ada = await world.AddLearnerAsync(t, "Ada");
        var ben = await world.AddLearnerAsync(t, "Ben");
        var course = await world.NewCourseAsync(t, "VID-1", publish: publish);
        if (publish) Assert.True((await EnrollAsync(ada, course)).IsSuccessStatusCode);
        return new Setup(t, teacher, ada, ben, course);
    }

    private static string Url(Guid id, string tail = "") => $"{Videos}/{id}{tail}";
    private static Guid Id(JsonElement video) => video.GetProperty("id").GetGuid();

    // ---------- uploading ----------
    [Fact]
    public async Task Staff_upload_a_video_and_it_is_ready_to_watch_with_its_details()
    {
        var s = await NewSetupAsync();
        var video = await UploadAsync(s.Teacher.Client, s.Course.Id, "  Intro to limits ", 754, Mp4(5000));
        Assert.Equal("Intro to limits", video.GetProperty("title").GetString());   // trimmed
        Assert.Equal("Uploaded", video.GetProperty("type").GetString());
        Assert.Equal("Ready", video.GetProperty("status").GetString());
        Assert.Equal(5000, video.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(754, video.GetProperty("durationSeconds").GetInt32());
        Assert.Equal("video/mp4", video.GetProperty("contentType").GetString());
        Assert.Equal(s.Course.Title, video.GetProperty("courseTitle").GetString());
        Assert.Equal("Tara", video.GetProperty("createdBy").GetString());
    }

    [Fact]
    public async Task An_upload_is_checked_for_a_course_a_title_and_a_real_video_file()
    {
        var s = await NewSetupAsync();
        var other = await _world.NewCourseAsync(s.Tenant, "VID-OTHER", publish: false);
        async Task<HttpStatusCode> Post(MultipartFormDataContent form) => (await s.Teacher.Client.PostAsync(Videos, form)).StatusCode;

        Assert.Equal(HttpStatusCode.BadRequest, await Post(UploadForm(s.Course.Id, withFile: false)));                                              // no file
        Assert.Equal(HttpStatusCode.BadRequest, await Post(UploadForm(null)));                                                                       // no course
        Assert.Equal(HttpStatusCode.BadRequest, await Post(UploadForm(Guid.NewGuid())));                                                             // unknown course
        Assert.Equal(HttpStatusCode.BadRequest, await Post(UploadForm(s.Course.Id, title: "   ")));                                                  // no title
        Assert.Equal(HttpStatusCode.BadRequest, await Post(UploadForm(s.Course.Id, title: new string('x', 251))));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(UploadForm(s.Course.Id, bytes: Encoding.ASCII.GetBytes("<html><script>alert(1)</script></html>"), contentType: "text/html", fileName: "x.html")));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(UploadForm(s.Course.Id, bytes: Png, contentType: "video/mp4", fileName: "fake.mp4")));    // says video, is an image
        Assert.Equal(HttpStatusCode.BadRequest, await Post(UploadForm(s.Course.Id, bytes: Mp4(), contentType: "audio/mpeg", fileName: "song.mp3")));  // audio is not video
        var lessonOfOtherCourse = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.BadRequest, await Post(UploadForm(s.Course.Id, lessonId: lessonOfOtherCourse)));                                  // lesson that is not in the course
        Assert.Empty((await ReadAsync(await s.Teacher.Client.GetAsync(Videos))).EnumerateArray());                                                   // nothing was kept
        Assert.NotEqual(Guid.Empty, other.Id);
    }

    [Fact]
    public async Task Learners_cannot_upload_change_or_delete_videos()
    {
        var s = await NewSetupAsync();
        var id = Id(await UploadAsync(s.Teacher.Client, s.Course.Id));
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Ada.Client.PostAsync(Videos, UploadForm(s.Course.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Ada.Client.PutAsJsonAsync(Url(id), new { title = "Mine now" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Ada.Client.DeleteAsync(Url(id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Ada.Client.PostAsJsonAsync($"{Videos}/external", new { courseId = s.Course.Id, title = "x", url = "https://youtu.be/x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Ada.Client.GetAsync($"{Videos}/usage")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Ada.Client.GetAsync(Url(id, "/analytics"))).StatusCode);
    }

    // ---------- who can see what ----------
    [Fact]
    public async Task Learners_see_ready_videos_of_published_courses_they_are_enrolled_in_and_nothing_else()
    {
        var s = await NewSetupAsync();
        var draftCourse = await _world.NewCourseAsync(s.Tenant, "VID-DRAFT", publish: false);
        var visible = Id(await UploadAsync(s.Teacher.Client, s.Course.Id, "Visible"));
        var hidden = Id(await UploadAsync(s.Teacher.Client, draftCourse.Id, "In a draft course"));

        var ada = await ReadAsync(await s.Ada.Client.GetAsync(Videos));
        Assert.Equal(["Visible"], ada.EnumerateArray().Select(item => item.GetProperty("title").GetString()));
        Assert.Empty((await ReadAsync(await s.Ben.Client.GetAsync(Videos))).EnumerateArray());                  // not enrolled
        Assert.Equal(HttpStatusCode.NotFound, (await s.Ben.Client.GetAsync(Url(visible))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Ada.Client.GetAsync(Url(hidden))).StatusCode);            // the course is not published
        Assert.Equal(2, (await ReadAsync(await s.Teacher.Client.GetAsync(Videos))).GetArrayLength());           // staff see everything
        Assert.Equal(HttpStatusCode.OK, (await s.Ada.Client.GetAsync(Url(visible))).StatusCode);
    }

    [Fact]
    public async Task The_library_can_be_filtered_by_course_type_and_text()
    {
        var s = await NewSetupAsync();
        var second = await _world.NewCourseAsync(s.Tenant, "VID-2");
        await UploadAsync(s.Teacher.Client, s.Course.Id, "Limits");
        await UploadAsync(s.Teacher.Client, second.Id, "Derivatives");
        await s.Teacher.Client.PostAsJsonAsync($"{Videos}/external", new { courseId = s.Course.Id, title = "Khan Academy limits", url = "https://www.youtube.com/embed/abc" });

        async Task<string[]> Titles(string query) => (await ReadAsync(await s.Teacher.Client.GetAsync($"{Videos}{query}"))).EnumerateArray().Select(item => item.GetProperty("title").GetString()!).OrderBy(x => x).ToArray();
        Assert.Equal(["Derivatives", "Khan Academy limits", "Limits"], await Titles(""));
        Assert.Equal(["Derivatives"], await Titles($"?courseId={second.Id}"));
        Assert.Equal(["Khan Academy limits"], await Titles("?type=External"));
        Assert.Equal(["Khan Academy limits"], await Titles("?search=KHAN"));            // ignores case
        Assert.Equal(["Derivatives", "Khan Academy limits", "Limits"], await Titles("?search=limits"));   // also looks in descriptions
        Assert.Equal(["Derivatives", "Khan Academy limits", "Limits"], await Titles("?status=Ready"));
        Assert.Empty(await Titles("?status=Failed"));
    }

    // ---------- playback ----------
    [Fact]
    public async Task Playback_uses_a_signed_link_that_works_without_signing_in_and_supports_seeking()
    {
        var s = await NewSetupAsync();
        var bytes = Mp4(10_000);
        var id = Id(await UploadAsync(s.Teacher.Client, s.Course.Id, bytes: bytes));

        var link = await ReadAsync(await s.Ada.Client.GetAsync(Url(id, "/link")));
        Assert.Equal("stream", link.GetProperty("kind").GetString());
        var url = link.GetProperty("url").GetString()!;
        Assert.Contains("?token=", url);
        Assert.NotEqual(JsonValueKind.Null, link.GetProperty("expiresAtUtc").ValueKind);

        // A video player has no sign-in header, so the link alone must be enough.
        var player = _factory.CreateClient();
        var whole = await player.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, whole.StatusCode);
        Assert.Equal("video/mp4", whole.Content.Headers.ContentType!.MediaType);
        Assert.Equal(bytes, await whole.Content.ReadAsByteArrayAsync());
        Assert.Equal("nosniff", whole.Headers.GetValues("X-Content-Type-Options").Single());

        var part = new HttpRequestMessage(HttpMethod.Get, url);
        part.Headers.Range = new RangeHeaderValue(100, 199);
        var partial = await player.SendAsync(part);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(bytes[100..200], await partial.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_playback_link_is_refused_to_people_who_may_not_watch_and_for_anything_forged_or_expired()
    {
        var s = await NewSetupAsync();
        var id = Id(await UploadAsync(s.Teacher.Client, s.Course.Id));
        var otherId = Id(await UploadAsync(s.Teacher.Client, s.Course.Id, "Another"));
        Assert.Equal(HttpStatusCode.NotFound, (await s.Ben.Client.GetAsync(Url(id, "/link"))).StatusCode);              // not enrolled: no link at all

        var url = (await ReadAsync(await s.Ada.Client.GetAsync(Url(id, "/link")))).GetProperty("url").GetString()!;
        var token = url[(url.IndexOf("token=", StringComparison.Ordinal) + 6)..];
        var player = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await player.GetAsync(Url(id, "/stream"))).StatusCode);                  // no token
        Assert.Equal(HttpStatusCode.NotFound, (await player.GetAsync(Url(id, "/stream?token=garbage"))).StatusCode);    // forged
        Assert.Equal(HttpStatusCode.NotFound, (await player.GetAsync(Url(id, $"/stream?token={token}x"))).StatusCode);  // tampered with
        Assert.Equal(HttpStatusCode.NotFound, (await player.GetAsync(Url(otherId, $"/stream?token={token}"))).StatusCode); // good for one video only

        // A token whose time is up is refused, even though it was properly made.
        var protector = _factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("lms.video.playback.v1").ToTimeLimitedDataProtector();
        var parts = Uri.UnescapeDataString(token);
        var payload = protector.Unprotect(parts);
        var expired = protector.Protect(payload, TimeSpan.FromSeconds(-5));
        Assert.Equal(HttpStatusCode.NotFound, (await player.GetAsync(Url(id, $"/stream?token={Uri.EscapeDataString(expired)}"))).StatusCode);
    }

    [Fact]
    public async Task A_link_from_one_organization_cannot_open_another_organizations_video()
    {
        var a = await NewSetupAsync();
        var b = await NewSetupAsync();
        var aVideo = Id(await UploadAsync(a.Teacher.Client, a.Course.Id));
        var bVideo = Id(await UploadAsync(b.Teacher.Client, b.Course.Id));
        var url = (await ReadAsync(await a.Ada.Client.GetAsync(Url(aVideo, "/link")))).GetProperty("url").GetString()!;
        var token = url[(url.IndexOf("token=", StringComparison.Ordinal) + 6)..];
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.CreateClient().GetAsync(Url(bVideo, $"/stream?token={token}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Teacher.Client.GetAsync(Url(aVideo))).StatusCode);              // and by id, within the API
        Assert.Equal(HttpStatusCode.NotFound, (await b.Teacher.Client.GetAsync(Url(aVideo, "/link"))).StatusCode);
    }

    [Fact]
    public async Task With_object_storage_playback_uses_the_stores_own_expiring_link()
    {
        await using var factory = new LmsApiFactory { UseFakeObjectStorage = true };
        var world = new TestWorld(factory);
        var s = await NewSetupAsync(world);
        var id = Id(await UploadAsync(s.Teacher.Client, s.Course.Id));
        var link = await ReadAsync(await s.Ada.Client.GetAsync(Url(id, "/link")));
        Assert.Equal("direct", link.GetProperty("kind").GetString());
        Assert.StartsWith("https://objects.test/", link.GetProperty("url").GetString());
        Assert.Single(factory.Store.Keys);
    }

    [Fact]
    public async Task A_video_linked_from_elsewhere_plays_embedded_only_on_trusted_hosts()
    {
        var s = await NewSetupAsync();
        async Task<JsonElement> Add(string url) => await ReadAsync(await s.Teacher.Client.PostAsJsonAsync($"{Videos}/external", new { courseId = s.Course.Id, title = url, url }));
        var youtube = await Add("https://www.youtube-nocookie.com/embed/abc123");
        var other = await Add("https://videos.school.example.org/lecture-1");
        Assert.Equal("External", youtube.GetProperty("type").GetString());

        var embedded = await ReadAsync(await s.Ada.Client.GetAsync(Url(Id(youtube), "/link")));
        Assert.Equal("external", embedded.GetProperty("kind").GetString());
        Assert.True(embedded.GetProperty("embeddable").GetBoolean());
        var plain = await ReadAsync(await s.Ada.Client.GetAsync(Url(Id(other), "/link")));
        Assert.False(plain.GetProperty("embeddable").GetBoolean());                                                 // opened in its own tab, not framed
        Assert.Equal("https://videos.school.example.org/lecture-1", plain.GetProperty("url").GetString());
    }

    [Fact]
    public async Task A_link_to_a_video_must_be_a_clean_https_address_for_a_real_course()
    {
        var s = await NewSetupAsync();
        Task<HttpResponseMessage> Post(object body) => s.Teacher.Client.PostAsJsonAsync($"{Videos}/external", body);
        foreach (var bad in new string?[] { null, "", "not a link", "http://videos.example.org/1", "javascript:alert(1)", "https://user:pw@videos.example.org/1" })
            Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { courseId = s.Course.Id, title = "Lecture", url = bad })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { courseId = s.Course.Id, title = "", url = "https://videos.example.org/1" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { courseId = Guid.NewGuid(), title = "Lecture", url = "https://videos.example.org/1" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Post(new { courseId = s.Course.Id, title = "Lecture", url = "https://videos.example.org/1" })).StatusCode);
    }

    // ---------- progress ----------
    [Fact]
    public async Task Progress_is_remembered_so_playback_can_resume_and_the_video_can_complete()
    {
        var s = await NewSetupAsync();
        var id = Id(await UploadAsync(s.Teacher.Client, s.Course.Id, duration: null));       // length not known yet

        var first = await ReadAsync(await s.Ada.Client.PostAsJsonAsync(Url(id, "/progress"), new { positionSeconds = 120, durationSeconds = 600, watchedSecondsDelta = 15, started = true }));
        Assert.Equal(120, first.GetProperty("lastPositionSeconds").GetInt32());
        Assert.Equal(20, first.GetProperty("percent").GetInt32());
        Assert.False(first.GetProperty("completed").GetBoolean());

        var listed = await ReadAsync(await s.Ada.Client.GetAsync(Url(id)));
        Assert.Equal(600, listed.GetProperty("durationSeconds").GetInt32());                  // the first player told us the length
        Assert.Equal(120, listed.GetProperty("myProgress").GetProperty("lastPositionSeconds").GetInt32());

        await s.Ada.Client.PostAsJsonAsync(Url(id, "/progress"), new { positionSeconds = 30, watchedSecondsDelta = 15 });          // went back
        var back = await ReadAsync(await s.Ada.Client.GetAsync(Url(id)));
        Assert.Equal(30, back.GetProperty("myProgress").GetProperty("lastPositionSeconds").GetInt32());
        Assert.Equal(20, back.GetProperty("myProgress").GetProperty("percent").GetInt32());   // the furthest point still counts

        var done = await ReadAsync(await s.Ada.Client.PostAsJsonAsync(Url(id, "/progress"), new { positionSeconds = 560, watchedSecondsDelta = 15 }));
        Assert.True(done.GetProperty("completed").GetBoolean());                                // past 90%
    }

    [Fact]
    public async Task Progress_reports_cannot_inflate_the_numbers()
    {
        var s = await NewSetupAsync();
        var id = Id(await UploadAsync(s.Teacher.Client, s.Course.Id, duration: 100));
        await s.Ada.Client.PostAsJsonAsync(Url(id, "/progress"), new { positionSeconds = 99999, watchedSecondsDelta = 999999, durationSeconds = 5 });
        await s.Ada.Client.PostAsJsonAsync(Url(id, "/progress"), new { positionSeconds = -50, watchedSecondsDelta = -999 });
        var mine = (await ReadAsync(await s.Ada.Client.GetAsync(Url(id)))).GetProperty("myProgress");
        Assert.Equal(0, mine.GetProperty("lastPositionSeconds").GetInt32());                   // negative becomes zero
        Assert.Equal(100, mine.GetProperty("percent").GetInt32());                              // a position past the end counts as the end
        Assert.Equal(100, (await ReadAsync(await s.Ada.Client.GetAsync(Url(id)))).GetProperty("durationSeconds").GetInt32()); // a learner cannot change a known length

        var analytics = await ReadAsync(await s.Teacher.Client.GetAsync(Url(id, "/analytics")));
        Assert.Equal(120, analytics.GetProperty("watchedSeconds").GetInt32());                  // 120 for the huge report, 0 for the negative one
    }

    [Fact]
    public async Task Only_people_who_may_watch_can_report_progress()
    {
        var s = await NewSetupAsync();
        var id = Id(await UploadAsync(s.Teacher.Client, s.Course.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await s.Ben.Client.PostAsJsonAsync(Url(id, "/progress"), new { positionSeconds = 10 })).StatusCode);
    }

    // ---------- analytics ----------
    [Fact]
    public async Task Analytics_show_how_far_people_got_and_where_they_stopped()
    {
        var s = await NewSetupAsync();
        var cleo = await _world.AddLearnerAsync(s.Tenant, "Cleo");
        var dan = await _world.AddLearnerAsync(s.Tenant, "Dan");
        foreach (var learner in new[] { cleo, dan }) Assert.True((await EnrollAsync(learner, s.Course)).IsSuccessStatusCode);   // Ada, Cleo and Dan are enrolled
        var id = Id(await UploadAsync(s.Teacher.Client, s.Course.Id, duration: 1000));

        async Task Watch(Person who, int position, int seconds, bool started = true)
            => await who.Client.PostAsJsonAsync(Url(id, "/progress"), new { positionSeconds = position, watchedSecondsDelta = seconds, started });
        await Watch(s.Ada, 1000, 100);      // finished
        await Watch(s.Ada, 1000, 100);
        await Watch(cleo, 600, 100);        // got past half
        // Dan never pressed play.

        var analytics = await ReadAsync(await s.Teacher.Client.GetAsync(Url(id, "/analytics")));
        Assert.Equal(3, analytics.GetProperty("eligibleLearners").GetInt32());
        Assert.Equal(2, analytics.GetProperty("viewers").GetInt32());
        Assert.Equal(1, analytics.GetProperty("completed").GetInt32());
        Assert.Equal(3, analytics.GetProperty("plays").GetInt32());
        Assert.Equal(300, analytics.GetProperty("watchedSeconds").GetInt32());
        var funnel = analytics.GetProperty("funnel").EnumerateArray().ToDictionary(step => step.GetProperty("percent").GetInt32(), step => step.GetProperty("viewers").GetInt32());
        Assert.Equal(2, funnel[25]);
        Assert.Equal(2, funnel[50]);
        Assert.Equal(1, funnel[75]);       // only Ada got beyond three quarters
        Assert.Equal(1, funnel[100]);
        Assert.Equal(15, analytics.GetProperty("averageWatchedPercent").GetInt32());   // Ada played 20% of the length (200 s), Cleo 10%: averaged over those who watched
    }

    [Fact]
    public async Task Without_a_known_length_the_analytics_still_count_viewers_but_draw_no_funnel()
    {
        var s = await NewSetupAsync();
        var id = Id(await UploadAsync(s.Teacher.Client, s.Course.Id, duration: null));
        await s.Ada.Client.PostAsJsonAsync(Url(id, "/progress"), new { positionSeconds = 30, watchedSecondsDelta = 15, started = true });
        var analytics = await ReadAsync(await s.Teacher.Client.GetAsync(Url(id, "/analytics")));
        Assert.Equal(1, analytics.GetProperty("viewers").GetInt32());
        Assert.Empty(analytics.GetProperty("funnel").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, analytics.GetProperty("averageWatchedPercent").ValueKind);
    }

    // ---------- editing ----------
    [Fact]
    public async Task Staff_can_rename_a_video_but_not_leave_it_without_a_title()
    {
        var s = await NewSetupAsync();
        var id = Id(await UploadAsync(s.Teacher.Client, s.Course.Id, duration: null));
        var updated = await ReadAsync(await s.Teacher.Client.PutAsJsonAsync(Url(id), new { title = "  Limits, part 1 ", description = "Revised", durationSeconds = 321 }));
        Assert.Equal("Limits, part 1", updated.GetProperty("title").GetString());
        Assert.Equal("Revised", updated.GetProperty("description").GetString());
        Assert.Equal(321, updated.GetProperty("durationSeconds").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Teacher.Client.PutAsJsonAsync(Url(id), new { title = "  " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Teacher.Client.PutAsJsonAsync(Url(id), new { title = "ok", description = new string('x', 2001) })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Teacher.Client.PutAsJsonAsync(Url(Guid.NewGuid()), new { title = "ok" })).StatusCode);
    }

    // ---------- putting a video in a lesson ----------
    private async Task<(Setup Setup, Guid VideoId)> DraftCourseWithVideoAsync()
    {
        var s = await NewSetupAsync(publish: false);
        var id = Id(await UploadAsync(s.Teacher.Client, s.Course.Id));
        return (s, id);
    }

    [Fact]
    public async Task A_video_can_be_added_to_a_draft_lesson_and_learners_then_watch_it_there_after_publishing()
    {
        var (s, id) = await DraftCourseWithVideoAsync();
        var lessonId = s.Course.LessonIds[0][0];
        var attached = await s.Teacher.Client.PostAsJsonAsync(Url(id, "/attach"), new { lessonId });
        Assert.Equal(HttpStatusCode.Created, attached.StatusCode);
        Assert.Equal("Video", (await ReadAsync(attached)).GetProperty("blockType").GetString());
        Assert.Equal(lessonId, (await ReadAsync(await s.Teacher.Client.GetAsync(Url(id)))).GetProperty("lessonId").GetGuid());

        var blocks = await ReadAsync(await s.Tenant.Admin.GetAsync($"/api/v1/tenant/courses/{s.Course.Id}/lessons/{lessonId}/blocks"));
        var block = Assert.Single(blocks.EnumerateArray());
        Assert.Equal("Video", block.GetProperty("type").GetString());
        Assert.Equal("Intro to limits", block.GetProperty("title").GetString());

        await s.Tenant.Admin.PostAsync($"/api/v1/tenant/courses/{s.Course.Id}/submit-review", null);
        await s.Tenant.Admin.PostAsync($"/api/v1/tenant/courses/{s.Course.Id}/publish", null);
        Assert.True((await EnrollAsync(s.Ada, s.Course)).IsSuccessStatusCode);
        var seen = await ReadAsync(await s.Ada.Client.GetAsync($"/api/v1/tenant/courses/{s.Course.Id}/lessons/{lessonId}/blocks"));
        var path = Assert.Single(seen.EnumerateArray()).GetProperty("file").GetProperty("downloadPath").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await s.Ada.Client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Attaching_needs_an_editable_lesson_of_the_videos_own_course()
    {
        var (s, id) = await DraftCourseWithVideoAsync();
        var otherCourse = await _world.NewCourseAsync(s.Tenant, "VID-OTHER", publish: false);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Teacher.Client.PostAsJsonAsync(Url(id, "/attach"), new { lessonId = otherCourse.LessonIds[0][0] })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Teacher.Client.PostAsJsonAsync(Url(id, "/attach"), new { lessonId = Guid.NewGuid() })).StatusCode);

        // Once the course is published its lessons are locked until a new version is started.
        var lessonId = s.Course.LessonIds[0][0];
        await s.Tenant.Admin.PostAsync($"/api/v1/tenant/courses/{s.Course.Id}/submit-review", null);
        await s.Tenant.Admin.PostAsync($"/api/v1/tenant/courses/{s.Course.Id}/publish", null);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Teacher.Client.PostAsJsonAsync(Url(id, "/attach"), new { lessonId })).StatusCode);
    }

    [Fact]
    public async Task A_linked_video_becomes_an_embed_on_trusted_hosts_and_a_plain_link_elsewhere()
    {
        var s = await NewSetupAsync(publish: false);
        var lessonId = s.Course.LessonIds[0][0];
        async Task<Guid> Add(string url) => Id(await ReadAsync(await s.Teacher.Client.PostAsJsonAsync($"{Videos}/external", new { courseId = s.Course.Id, title = "Linked", url })));
        var embed = await ReadAsync(await s.Teacher.Client.PostAsJsonAsync(Url(await Add("https://www.youtube.com/embed/abc"), "/attach"), new { lessonId }));
        var link = await ReadAsync(await s.Teacher.Client.PostAsJsonAsync(Url(await Add("https://videos.school.example.org/1"), "/attach"), new { lessonId }));
        Assert.Equal("Embed", embed.GetProperty("blockType").GetString());
        Assert.Equal("Link", link.GetProperty("blockType").GetString());
    }

    // ---------- deleting ----------
    [Fact]
    public async Task Deleting_a_video_removes_its_file_and_watch_history()
    {
        await using var factory = new LmsApiFactory { UseFakeObjectStorage = true };
        var world = new TestWorld(factory);
        var s = await NewSetupAsync(world);
        var id = Id(await UploadAsync(s.Teacher.Client, s.Course.Id));
        await s.Ada.Client.PostAsJsonAsync(Url(id, "/progress"), new { positionSeconds = 10, watchedSecondsDelta = 10, started = true });
        Assert.Single(factory.Store.Keys);

        Assert.Equal(HttpStatusCode.NoContent, (await s.Teacher.Client.DeleteAsync(Url(id))).StatusCode);
        Assert.Empty(factory.Store.Keys);                                                                    // the stored file is gone too
        Assert.Equal(HttpStatusCode.NotFound, (await s.Teacher.Client.GetAsync(Url(id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Teacher.Client.DeleteAsync(Url(id))).StatusCode);
        Assert.Empty((await ReadAsync(await s.Ada.Client.GetAsync(Videos))).EnumerateArray());
    }

    [Fact]
    public async Task A_video_used_in_a_lesson_keeps_working_there_after_it_leaves_the_library()
    {
        await using var factory = new LmsApiFactory { UseFakeObjectStorage = true };
        var world = new TestWorld(factory);
        var s = await NewSetupAsync(world, publish: false);
        var id = Id(await UploadAsync(s.Teacher.Client, s.Course.Id));
        var lessonId = s.Course.LessonIds[0][0];
        await s.Teacher.Client.PostAsJsonAsync(Url(id, "/attach"), new { lessonId });

        Assert.Equal(HttpStatusCode.NoContent, (await s.Teacher.Client.DeleteAsync(Url(id))).StatusCode);
        Assert.Single(factory.Store.Keys);                                                                   // the lesson still needs the file
        var blocks = await ReadAsync(await s.Tenant.Admin.GetAsync($"/api/v1/tenant/courses/{s.Course.Id}/lessons/{lessonId}/blocks"));
        var path = Assert.Single(blocks.EnumerateArray()).GetProperty("file").GetProperty("downloadPath").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await s.Tenant.Admin.GetAsync(path)).StatusCode);
    }

    // ---------- storage usage ----------
    [Fact]
    public async Task Usage_totals_the_library_by_type_and_status()
    {
        var s = await NewSetupAsync();
        await UploadAsync(s.Teacher.Client, s.Course.Id, "One", bytes: Mp4(1000));
        await UploadAsync(s.Teacher.Client, s.Course.Id, "Two", bytes: Mp4(2000));
        await s.Teacher.Client.PostAsJsonAsync($"{Videos}/external", new { courseId = s.Course.Id, title = "Link", url = "https://videos.example.org/1" });
        var usage = await ReadAsync(await s.Teacher.Client.GetAsync($"{Videos}/usage"));
        Assert.Equal(3, usage.GetProperty("count").GetInt32());
        Assert.Equal(3000, usage.GetProperty("totalBytes").GetInt64());
        var byType = usage.GetProperty("byType").EnumerateArray().ToDictionary(item => item.GetProperty("name").GetString()!, item => item.GetProperty("count").GetInt32());
        Assert.Equal(2, byType["Uploaded"]);
        Assert.Equal(1, byType["External"]);
        Assert.Equal(3, usage.GetProperty("byStatus").EnumerateArray().Single().GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Each_organization_has_its_own_library()
    {
        var a = await NewSetupAsync();
        var b = await NewSetupAsync();
        await UploadAsync(a.Teacher.Client, a.Course.Id);
        Assert.Empty((await ReadAsync(await b.Teacher.Client.GetAsync(Videos))).EnumerateArray());
        Assert.Equal(0, (await ReadAsync(await b.Teacher.Client.GetAsync($"{Videos}/usage"))).GetProperty("count").GetInt32());
    }
}
