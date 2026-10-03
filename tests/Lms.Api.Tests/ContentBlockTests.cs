using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lms.Api.Domain.Courses;
using Lms.Api.Infrastructure.Storage;

namespace Lms.Api.Tests;

public sealed class ContentBlockTests : IClassFixture<LmsApiFactory>
{
    private readonly LmsApiFactory _factory;
    public ContentBlockTests(LmsApiFactory factory) => _factory = factory;

    private sealed record Setup(string Slug, HttpClient Admin, HttpClient Learner, Guid CourseId, Guid LessonId);

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4 fake body");
    private static readonly byte[] Html = Encoding.ASCII.GetBytes("<html><script>alert(1)</script></html>");

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<(Guid CourseId, Guid LessonId)> CourseWithLessonAsync(HttpClient admin, string code = "ENG-1")
    {
        var course = await ReadAsync(await admin.PostAsJsonAsync("/api/v1/tenant/courses", new { code, title = $"Course {code}" }));
        var courseId = course.GetProperty("course").GetProperty("id").GetGuid();
        var module = await ReadAsync(await admin.PostAsJsonAsync($"/api/v1/tenant/courses/{courseId}/modules", new { title = "Module 1" }));
        var moduleId = module.GetProperty("id").GetGuid();
        var lesson = await ReadAsync(await admin.PostAsJsonAsync($"/api/v1/tenant/courses/{courseId}/modules/{moduleId}/lessons", new { title = "Lesson 1" }));
        return (courseId, lesson.GetProperty("id").GetGuid());
    }

    private async Task<Setup> CreateAsync(bool enrollLearner = true)
    {
        var (slug, _, adminToken) = await _factory.ProvisionTenantWithAdminAsync();
        var admin = _factory.CreateTenantClient(slug, adminToken);
        var (courseId, lessonId) = await CourseWithLessonAsync(admin);
        var email = $"lena@{slug}.test";
        (await admin.PostAsJsonAsync("/api/v1/tenant/users", new { email, displayName = "Lena", password = LmsApiFactory.AdminPassword, roleCode = "LEARNER" })).EnsureSuccessStatusCode();
        var learner = _factory.CreateTenantClient(slug, await _factory.LoginAsync(slug, email, LmsApiFactory.AdminPassword));
        if (enrollLearner) await PublishAsync(admin, courseId, learner);
        return new Setup(slug, admin, learner, courseId, lessonId);
    }

    private static async Task PublishAsync(HttpClient admin, Guid courseId, HttpClient? enroll = null)
    {
        (await admin.PostAsync($"/api/v1/tenant/courses/{courseId}/submit-review", null)).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/api/v1/tenant/courses/{courseId}/publish", null)).EnsureSuccessStatusCode();
        if (enroll is not null) (await enroll.PostAsync($"/api/v1/tenant/courses/{courseId}/enroll", null)).EnsureSuccessStatusCode();
    }

    private static string Url(Setup s, string suffix = "") => $"/api/v1/tenant/courses/{s.CourseId}/lessons/{s.LessonId}/blocks{suffix}";

    private static Task<HttpResponseMessage> AddJson(HttpClient client, string url, object body) => client.PostAsJsonAsync(url, body);

    private static Task<HttpResponseMessage> AddFile(HttpClient client, string url, string type, string fileName, string contentType, byte[] bytes, string? title = null)
    {
        var form = new MultipartFormDataContent { { new StringContent(type), "type" } };
        if (title is not null) form.Add(new StringContent(title), "title");
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        form.Add(file, "file", fileName);
        return client.PostAsync(url, form);
    }

    // ---------- text-based blocks ----------
    [Fact]
    public async Task Text_code_and_link_blocks_are_created_in_order()
    {
        var s = await CreateAsync(enrollLearner: false);
        Assert.Equal(HttpStatusCode.Created, (await AddJson(s.Admin, Url(s), new { type = "Text", text = "Welcome to the lesson." })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AddJson(s.Admin, Url(s), new { type = "Code", language = "python", text = "print('hi')", caption = "A first program" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AddJson(s.Admin, Url(s), new { type = "Link", title = "Docs", url = "https://example.org/docs" })).StatusCode);

        var blocks = await ReadAsync(await s.Admin.GetAsync(Url(s)));
        Assert.Equal(new[] { "Text", "Code", "Link" }, blocks.EnumerateArray().Select(b => b.GetProperty("type").GetString()).ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, blocks.EnumerateArray().Select(b => b.GetProperty("displayOrder").GetInt32()).ToArray());
        Assert.Equal("python", blocks[1].GetProperty("language").GetString());
    }

    [Theory]
    [InlineData("Nonsense", "x", null, "Unknown block type")]
    [InlineData("Text", "   ", null, "Write some text")]
    [InlineData("Code", "", null, "Paste the code")]
    [InlineData("Link", null, "javascript:alert(1)", "http or https")]
    [InlineData("Link", null, "ftp://example.org/file", "http or https")]
    [InlineData("Embed", null, "http://www.youtube.com/embed/abc", "https links")]
    [InlineData("Embed", null, "https://evil.example/embed", "https links")]
    [InlineData("Embed", null, "https://user:pass@www.youtube.com/embed/abc", "https links")]
    public async Task Invalid_blocks_are_rejected_with_a_reason(string type, string? text, string? url, string expected)
    {
        var s = await CreateAsync(enrollLearner: false);
        var response = await AddJson(s.Admin, Url(s), new { type, text, url });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, (await ReadAsync(response)).GetProperty("message").GetString());
    }

    [Fact]
    public async Task Allow_listed_video_embeds_are_accepted()
    {
        var s = await CreateAsync(enrollLearner: false);
        Assert.Equal(HttpStatusCode.Created, (await AddJson(s.Admin, Url(s), new { type = "Embed", url = "https://www.youtube.com/embed/dQw4w9WgXcQ" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AddJson(s.Admin, Url(s), new { type = "Embed", url = "https://player.vimeo.com/video/123" })).StatusCode);
    }

    [Fact]
    public async Task Field_lengths_are_limited()
    {
        var s = await CreateAsync(enrollLearner: false);
        Assert.Equal(HttpStatusCode.BadRequest, (await AddJson(s.Admin, Url(s), new { type = "Text", text = "ok", title = new string('t', 201) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AddJson(s.Admin, Url(s), new { type = "Text", text = new string('x', 20001) })).StatusCode);
    }

    // ---------- file blocks ----------
    [Fact]
    public async Task Image_pdf_and_download_blocks_store_their_file_with_metadata()
    {
        var s = await CreateAsync(enrollLearner: false);
        var image = await ReadAsync(await AddFile(s.Admin, Url(s), "Image", "diagram.png", "image/png", Png, "Diagram"));
        Assert.Equal("diagram.png", image.GetProperty("file").GetProperty("fileName").GetString());
        Assert.Equal("image/png", image.GetProperty("file").GetProperty("contentType").GetString());
        Assert.Equal(Png.Length, image.GetProperty("file").GetProperty("sizeBytes").GetInt64());
        Assert.Equal(HttpStatusCode.Created, (await AddFile(s.Admin, Url(s), "Pdf", "notes.pdf", "application/pdf", Pdf)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AddFile(s.Admin, Url(s), "Download", "template.docx", "application/octet-stream", [1, 2, 3])).StatusCode);
    }

    [Theory]
    [InlineData("Image", "page.html", "text/html")]
    [InlineData("Image", "logo.svg", "image/svg+xml")]
    [InlineData("Pdf", "x.pdf", "image/png")]
    [InlineData("Video", "clip.mp4", "application/x-msdownload")]
    public async Task File_blocks_reject_content_types_that_do_not_fit(string type, string fileName, string contentType)
    {
        var s = await CreateAsync(enrollLearner: false);
        Assert.Equal(HttpStatusCode.BadRequest, (await AddFile(s.Admin, Url(s), type, fileName, contentType, Png)).StatusCode);
    }

    [Fact]
    public async Task A_script_disguised_as_an_image_is_rejected_by_its_contents()
    {
        var s = await CreateAsync(enrollLearner: false);
        var response = await AddFile(s.Admin, Url(s), "Image", "innocent.png", "image/png", Html);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("do not match", (await ReadAsync(response)).GetProperty("message").GetString());
    }

    [Fact]
    public async Task File_blocks_need_a_file_and_other_blocks_refuse_one()
    {
        var s = await CreateAsync(enrollLearner: false);
        Assert.Equal(HttpStatusCode.BadRequest, (await AddJson(s.Admin, Url(s), new { type = "Image" })).StatusCode);
        var form = new MultipartFormDataContent { { new StringContent("Text"), "type" }, { new StringContent("hello"), "text" }, { new ByteArrayContent(Png), "file", "a.png" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Admin.PostAsync(Url(s), form)).StatusCode);
    }

    [Fact]
    public void File_signatures_are_recognised_for_each_format()
    {
        Assert.True(BlockFileRules.MatchesSignature("image/jpeg", [0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.True(BlockFileRules.MatchesSignature("image/gif", Encoding.ASCII.GetBytes("GIF89a")));
        Assert.True(BlockFileRules.MatchesSignature("image/webp", Encoding.ASCII.GetBytes("RIFF1234WEBP")));
        Assert.True(BlockFileRules.MatchesSignature("video/mp4", [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p']));
        Assert.True(BlockFileRules.MatchesSignature("video/webm", [0x1A, 0x45, 0xDF, 0xA3]));
        Assert.True(BlockFileRules.MatchesSignature("audio/mpeg", Encoding.ASCII.GetBytes("ID3\u0004")));
        Assert.True(BlockFileRules.MatchesSignature("audio/wav", Encoding.ASCII.GetBytes("RIFF1234WAVE")));
        Assert.False(BlockFileRules.MatchesSignature("image/png", Html));
        Assert.False(BlockFileRules.MatchesSignature("image/webp", Encoding.ASCII.GetBytes("RIFF1234WAVE")));
        Assert.Null(BlockFileRules.Validate(BlockType.Download, "text/html", Html)); // downloads are never rendered
    }

    // ---------- editing ----------
    [Fact]
    public async Task Blocks_can_be_edited_reordered_and_deleted_with_the_order_kept_tidy()
    {
        var s = await CreateAsync(enrollLearner: false);
        var a = (await ReadAsync(await AddJson(s.Admin, Url(s), new { type = "Text", text = "A" }))).GetProperty("id").GetGuid();
        var b = (await ReadAsync(await AddJson(s.Admin, Url(s), new { type = "Text", text = "B" }))).GetProperty("id").GetGuid();
        var c = (await ReadAsync(await AddJson(s.Admin, Url(s), new { type = "Text", text = "C" }))).GetProperty("id").GetGuid();

        var edited = await ReadAsync(await s.Admin.PutAsJsonAsync(Url(s, $"/{a}"), new { text = "A edited", title = "Intro" }));
        Assert.Equal("A edited", edited.GetProperty("text").GetString());
        Assert.Equal("Text", edited.GetProperty("type").GetString()); // type is fixed

        var reordered = await ReadAsync(await s.Admin.PutAsJsonAsync(Url(s, "/order"), new { blockIds = new[] { c, a, b } }));
        Assert.Equal(new[] { c, a, b }, reordered.EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToArray());

        Assert.Equal(HttpStatusCode.NoContent, (await s.Admin.DeleteAsync(Url(s, $"/{c}"))).StatusCode);
        var rest = await ReadAsync(await s.Admin.GetAsync(Url(s)));
        Assert.Equal(new[] { 1, 2 }, rest.EnumerateArray().Select(x => x.GetProperty("displayOrder").GetInt32()).ToArray());
    }

    [Fact]
    public async Task Reordering_must_list_every_block_exactly_once()
    {
        var s = await CreateAsync(enrollLearner: false);
        var a = (await ReadAsync(await AddJson(s.Admin, Url(s), new { type = "Text", text = "A" }))).GetProperty("id").GetGuid();
        await AddJson(s.Admin, Url(s), new { type = "Text", text = "B" });
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Admin.PutAsJsonAsync(Url(s, "/order"), new { blockIds = new[] { a } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Admin.PutAsJsonAsync(Url(s, "/order"), new { blockIds = new[] { a, a } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Admin.PutAsJsonAsync(Url(s, "/order"), new { blockIds = new[] { a, Guid.NewGuid() } })).StatusCode);
    }

    [Fact]
    public async Task Updates_are_validated_like_creation()
    {
        var s = await CreateAsync(enrollLearner: false);
        var link = (await ReadAsync(await AddJson(s.Admin, Url(s), new { type = "Link", url = "https://example.org" }))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Admin.PutAsJsonAsync(Url(s, $"/{link}"), new { url = "javascript:alert(1)" })).StatusCode);
    }

    // ---------- publication and access ----------
    [Fact]
    public async Task Published_courses_are_read_only()
    {
        var s = await CreateAsync(enrollLearner: false);
        var block = (await ReadAsync(await AddJson(s.Admin, Url(s), new { type = "Text", text = "Final wording" }))).GetProperty("id").GetGuid();
        await PublishAsync(s.Admin, s.CourseId);

        Assert.Equal(HttpStatusCode.Conflict, (await AddJson(s.Admin, Url(s), new { type = "Text", text = "More" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Admin.PutAsJsonAsync(Url(s, $"/{block}"), new { text = "Changed" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Admin.DeleteAsync(Url(s, $"/{block}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Admin.PutAsJsonAsync(Url(s, "/order"), new { blockIds = new[] { block } })).StatusCode);
        Assert.Equal(1, (await ReadAsync(await s.Admin.GetAsync(Url(s)))).GetArrayLength());
    }

    [Fact]
    public async Task Learners_read_blocks_only_when_enrolled_in_a_published_course()
    {
        var s = await CreateAsync(enrollLearner: false);
        await AddJson(s.Admin, Url(s), new { type = "Text", text = "Lesson body" });
        Assert.Equal(HttpStatusCode.NotFound, (await s.Learner.GetAsync(Url(s))).StatusCode); // still a draft

        await PublishAsync(s.Admin, s.CourseId);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Learner.GetAsync(Url(s))).StatusCode); // published, not enrolled

        (await s.Learner.PostAsync($"/api/v1/tenant/courses/{s.CourseId}/enroll", null)).EnsureSuccessStatusCode();
        var blocks = await ReadAsync(await s.Learner.GetAsync(Url(s)));
        Assert.Equal("Lesson body", blocks[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Learners_cannot_create_edit_or_delete_blocks()
    {
        var s = await CreateAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await AddJson(s.Learner, Url(s), new { type = "Text", text = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Learner.PutAsJsonAsync(Url(s, $"/{Guid.NewGuid()}"), new { text = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Learner.DeleteAsync(Url(s, $"/{Guid.NewGuid()}"))).StatusCode);
    }

    [Fact]
    public async Task A_lesson_from_another_course_cannot_be_used()
    {
        var s = await CreateAsync(enrollLearner: false);
        var (otherCourse, _) = await CourseWithLessonAsync(s.Admin, "OTHER-1");
        var wrong = $"/api/v1/tenant/courses/{otherCourse}/lessons/{s.LessonId}/blocks";
        Assert.Equal(HttpStatusCode.NotFound, (await AddJson(s.Admin, wrong, new { type = "Text", text = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Admin.GetAsync(wrong)).StatusCode);
    }

    // ---------- file download ----------
    [Fact]
    public async Task Block_files_download_only_for_enrolled_learners_and_staff_with_nosniff()
    {
        var s = await CreateAsync(enrollLearner: false);
        var block = await ReadAsync(await AddFile(s.Admin, Url(s), "Pdf", "notes.pdf", "application/pdf", Pdf));
        var path = block.GetProperty("file").GetProperty("downloadPath").GetString()!;
        await PublishAsync(s.Admin, s.CourseId);

        Assert.Equal(HttpStatusCode.NotFound, (await s.Learner.GetAsync(path)).StatusCode); // in the tenant, not enrolled
        (await s.Learner.PostAsync($"/api/v1/tenant/courses/{s.CourseId}/enroll", null)).EnsureSuccessStatusCode();

        var download = await s.Learner.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(Pdf, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("nosniff", download.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Blocks_and_files_are_isolated_between_tenants()
    {
        var a = await CreateAsync(enrollLearner: false);
        var b = await CreateAsync(enrollLearner: false);
        var block = await ReadAsync(await AddFile(a.Admin, Url(a), "Image", "a.png", "image/png", Png));
        Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.GetAsync(Url(a))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.GetAsync(block.GetProperty("file").GetProperty("downloadPath").GetString()!)).StatusCode);
    }
}
