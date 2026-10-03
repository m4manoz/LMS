using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lms.Api.Infrastructure.Videos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

/// <summary>Stands in for an OpenAI-style service and remembers what it was asked.</summary>
public sealed class FakeAiHandler : HttpMessageHandler
{
    public sealed record Call(string Path, string? Authorization, string Body);
    public List<Call> Calls { get; } = [];
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public string QuestionsJson { get; set; } = """{"questions":[{"question":"What does a limit describe?","options":["Behaviour near a point","The slope","The area","The period"],"answerIndex":0,"timestampSeconds":0,"explanation":"Said at the start."},{"question":"When is a function continuous?","options":["When the limit equals the value","Never","Always"],"answerIndex":0,"timestampSeconds":16}]}""";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Add(new Call(request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(), body));
        if (Status != HttpStatusCode.OK) return Json(Status, """{"error":{"message":"Incorrect API key provided."}}""");
        if (request.RequestUri.AbsolutePath.EndsWith("/audio/transcriptions"))
            return Json(HttpStatusCode.OK, """{"language":"english","duration":12.0,"segments":[{"start":0.0,"end":4.5,"text":" Limits describe how a function behaves near a point."},{"start":4.5,"end":9.0,"text":" Continuity means the limit equals the value."}]}""");
        var json = body.Contains("json_object");
        var content = json ? QuestionsJson : "A short paragraph.\n- [0:00] Limits describe behaviour near a point.";
        return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content } } } }));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

public sealed class TranscriptParserTests
{
    [Fact]
    public void WebVTT_with_a_header_notes_cue_names_tags_and_short_times_is_read()
    {
        var cues = TranscriptParser.Parse("WEBVTT\n\nNOTE made by hand\n\nintro\n00:01.500 --> 00:04.000 align:start\n<v Tara>Hello <b>everyone</b>\nand welcome &amp; hello\n\n01:02:03.250 --> 01:02:05.000\nLate line", out var error)!;
        Assert.Null(error);
        Assert.Equal(2, cues.Count);
        Assert.Equal(new TranscriptCue(1500, 4000, "Hello everyone and welcome & hello"), cues[0]);
        Assert.Equal(3723250, cues[1].StartMs);
    }

    [Fact]
    public void SRT_with_numbers_and_comma_times_is_read_and_cues_are_put_in_order()
    {
        var cues = TranscriptParser.Parse("2\r\n00:00:05,000 --> 00:00:07,500\r\nSecond\r\n\r\n1\r\n00:00:01,200 --> 00:00:03,000\r\nFirst", out _)!;
        Assert.Equal(["First", "Second"], cues.Select(cue => cue.Text));
        Assert.Equal(1200, cues[0].StartMs);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Just some words with no times at all.")]
    [InlineData("WEBVTT\n\n00:00:01.000 --> not a time\nHi")]
    [InlineData("00:99:01.000 --> 00:99:02.000\nHi")]
    public void Text_without_readable_timed_lines_is_refused_with_a_reason(string text)
    {
        Assert.Null(TranscriptParser.Parse(text, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Practice_questions_need_a_question_two_to_six_different_answers_and_a_marked_one()
    {
        PracticeQuestion Q(string q = "Why?", string[]? options = null, int answer = 0) => new(q, options ?? ["a", "b"], answer);
        Assert.Null(PracticeQuestions.Validate([Q()]));
        Assert.NotNull(PracticeQuestions.Validate([]));
        Assert.NotNull(PracticeQuestions.Validate([Q(" ")]));
        Assert.NotNull(PracticeQuestions.Validate([Q(options: ["only"])]));
        Assert.NotNull(PracticeQuestions.Validate([Q(options: ["a", "A"])]));
        Assert.NotNull(PracticeQuestions.Validate([Q(answer: 2)]));
        Assert.NotNull(PracticeQuestions.Validate([Q(answer: -1)]));
        Assert.NotNull(PracticeQuestions.Validate([Q(options: ["a", " "])]));
    }
}

/// <summary>Transcripts, search, summaries and practice questions for videos, and the service that makes them.</summary>
public sealed class VideoAiTests
{
    private const string Videos = "/api/v1/tenant/videos";
    private const string SettingsUrl = "/api/v1/tenant/integrations/video-ai";
    private const string Key = "sk-test-key-0123456789abcdef";

    private const string Vtt = """
        WEBVTT

        00:00:00.000 --> 00:00:05.000
        A limit describes how a function behaves as its input gets close to a point.

        00:00:05.000 --> 00:00:10.000
        We write the limit of f of x as x approaches a, and the limit may exist even when f is undefined at a.

        00:00:10.000 --> 00:00:16.000
        For example, the function sine of x over x has a limit of one as x approaches zero, although it is undefined at zero.

        00:00:16.000 --> 00:00:22.000
        Continuity means that the limit at a point equals the value of the function at that point.

        00:00:22.000 --> 00:00:28.000
        A function is continuous when the limit exists, the value exists, and the two are equal.

        00:00:28.000 --> 00:00:34.000
        Derivatives are built from limits, because the derivative measures the limit of a difference quotient.
        """;

    private sealed record Setup(TestWorld World, LmsApiFactory Factory, FakeVideoTranscoder Fake, FakeAiHandler Ai, Tenant Tenant, Person Teacher, Person Ada, Person Ben, CourseInfo Course);

    private static async Task<Setup> NewSetupAsync(bool conversion = true)
    {
        var fake = new FakeVideoTranscoder { Enabled = conversion };
        var ai = new FakeAiHandler();
        var factory = new LmsApiFactory { Transcoder = fake, HttpHandler = ai };
        var world = new TestWorld(factory);
        var t = await world.NewTenantAsync();
        var teacher = await world.AddPersonAsync(t, "Tara", "TEACHER");
        var ada = await world.AddLearnerAsync(t, "Ada");
        var ben = await world.AddLearnerAsync(t, "Ben");
        var course = await world.NewCourseAsync(t, "AI-1");
        Assert.True((await EnrollAsync(ada, course)).IsSuccessStatusCode);
        return new Setup(world, factory, fake, ai, t, teacher, ada, ben, course);
    }

    private static async Task<Guid> UploadAsync(Setup s, string title = "Limits")
    {
        var bytes = new byte[2048];
        new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'m', (byte)'p', (byte)'4', (byte)'2' }.CopyTo(bytes, 0);
        var form = new MultipartFormDataContent { { new StringContent(s.Course.Id.ToString()), "courseId" }, { new StringContent(title), "title" } };
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        form.Add(file, "file", "lecture.mp4");
        var response = await s.Teacher.Client.PostAsync(Videos, form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> ReadyVideoAsync(Setup s, string title = "Limits")
    {
        var id = await UploadAsync(s, title);
        await s.Factory.Services.GetRequiredService<VideoProcessingService>().RunOnceAsync(CancellationToken.None);
        return id;
    }

    private static Task<HttpResponseMessage> PasteAsync(Person who, Guid id, string text = Vtt, string? language = "en")
        => who.Client.PostAsJsonAsync($"{Videos}/{id}/transcript", new { text, language });

    private static Task<HttpResponseMessage> UseOpenAiAsync(Setup s, bool auto = false, string? key = Key, string url = "https://ai.example.org/v1")
        => s.Tenant.Admin.PutAsJsonAsync(SettingsUrl, new { provider = "OpenAiCompatible", baseUrl = url, apiKey = key, autoTranscribe = auto });

    private static Task<int> TranscribeAsync(Setup s) => s.Factory.Services.GetRequiredService<VideoTranscriptionService>().RunOnceAsync(CancellationToken.None);

    // ---------- the organization's setting ----------
    [Fact]
    public async Task Without_a_setting_the_local_provider_is_used_and_only_administrators_can_see_or_change_it()
    {
        var s = await NewSetupAsync();
        var saved = await ReadAsync(await s.Tenant.Admin.GetAsync(SettingsUrl));
        Assert.Equal("Local", saved.GetProperty("provider").GetString());
        Assert.False(saved.GetProperty("apiKeySet").GetBoolean());
        Assert.Equal("whisper-1", saved.GetProperty("transcriptionModel").GetString());
        Assert.True(saved.GetProperty("conversionAvailable").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Teacher.Client.GetAsync(SettingsUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Teacher.Client.PutAsJsonAsync(SettingsUrl, new { provider = "Local" })).StatusCode);
    }

    [Fact]
    public async Task The_service_needs_a_clean_https_address_valid_model_names_and_a_key()
    {
        var s = await NewSetupAsync();
        Task<HttpResponseMessage> Put(object body) => s.Tenant.Admin.PutAsJsonAsync(SettingsUrl, body);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(new { provider = "Skynet" })).StatusCode);
        foreach (var bad in new[] { "http://ai.example.org/v1", "javascript:alert(1)", "not a link", "https://u:p@ai.example.org/v1", "https://ai.example.org/v1?x=1" })
            Assert.Equal(HttpStatusCode.BadRequest, (await UseOpenAiAsync(s, url: bad)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UseOpenAiAsync(s, key: null)).StatusCode);                                  // the first save needs a key
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(new { provider = "OpenAiCompatible", apiKey = Key, chatModel = "bad model!" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await UseOpenAiAsync(s, url: "https://ai.example.org/v1/")).StatusCode);
        Assert.Equal("https://ai.example.org/v1", (await ReadAsync(await s.Tenant.Admin.GetAsync(SettingsUrl))).GetProperty("baseUrl").GetString());   // tidied
    }

    [Fact]
    public async Task The_key_is_never_returned_or_stored_in_plain_text_and_a_blank_key_keeps_the_saved_one()
    {
        var s = await NewSetupAsync();
        await UseOpenAiAsync(s);
        var body = await (await s.Tenant.Admin.GetAsync(SettingsUrl)).Content.ReadAsStringAsync();
        Assert.DoesNotContain(Key, body);
        Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("apiKeySet").GetBoolean());
        await s.World.WithDbAsync(s.Tenant.Slug, async db =>
        {
            var row = await db.VideoAiSettings.SingleAsync();
            Assert.False(string.IsNullOrEmpty(row.ApiKeyProtected));
            Assert.DoesNotContain(Key, row.ApiKeyProtected);
        });

        Assert.Equal(HttpStatusCode.OK, (await UseOpenAiAsync(s, key: null, url: "https://other.example.org/v1")).StatusCode);   // new address, same key
        var id = await ReadyVideoAsync(s);
        await PasteAsync(s.Teacher, id);
        await s.Teacher.Client.PostAsJsonAsync($"{Videos}/{id}/insights", new { kind = "Summary" });
        Assert.Equal($"Bearer {Key}", s.Ai.Calls.Last().Authorization);
        Assert.Equal("/v1/chat/completions", s.Ai.Calls.Last().Path);
    }

    // ---------- transcripts pasted by hand ----------
    [Fact]
    public async Task Staff_paste_a_transcript_and_enrolled_learners_read_it_but_others_do_not()
    {
        var s = await NewSetupAsync();
        var id = await ReadyVideoAsync(s);
        Assert.Equal("None", (await ReadAsync(await s.Ada.Client.GetAsync($"{Videos}/{id}/transcript"))).GetProperty("status").GetString());

        var response = await PasteAsync(s.Teacher, id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var transcript = await ReadAsync(await s.Ada.Client.GetAsync($"{Videos}/{id}/transcript"));
        Assert.Equal("Ready", transcript.GetProperty("status").GetString());
        Assert.Equal("Manual", transcript.GetProperty("source").GetString());
        Assert.Equal("en", transcript.GetProperty("language").GetString());
        var lines = transcript.GetProperty("segments").EnumerateArray().ToList();
        Assert.Equal(6, lines.Count);
        Assert.Equal(16.0, lines[3].GetProperty("startSeconds").GetDouble());
        Assert.StartsWith("Continuity means", lines[3].GetProperty("text").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await s.Ben.Client.GetAsync($"{Videos}/{id}/transcript")).StatusCode);          // not enrolled
        Assert.Equal(HttpStatusCode.Forbidden, (await PasteAsync(s.Ada, id)).StatusCode);                                        // learners cannot add one
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Ada.Client.DeleteAsync($"{Videos}/{id}/transcript")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await PasteAsync(s.Teacher, id, "WEBVTT\n\n00:00:00.000 --> 00:00:03.000\nReplaced.")).StatusCode);   // pasting again replaces it
        Assert.Single((await ReadAsync(await s.Ada.Client.GetAsync($"{Videos}/{id}/transcript"))).GetProperty("segments").EnumerateArray());
        Assert.Equal(HttpStatusCode.NoContent, (await s.Teacher.Client.DeleteAsync($"{Videos}/{id}/transcript")).StatusCode);
        Assert.Equal("None", (await ReadAsync(await s.Ada.Client.GetAsync($"{Videos}/{id}/transcript"))).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_transcript_that_cannot_be_read_is_refused_and_a_linked_video_can_have_one()
    {
        var s = await NewSetupAsync();
        var id = await ReadyVideoAsync(s);
        var refused = await PasteAsync(s.Teacher, id, "no times anywhere");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("No timed lines", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await PasteAsync(s.Teacher, Guid.NewGuid())).StatusCode);

        var external = await ReadAsync(await s.Teacher.Client.PostAsJsonAsync($"{Videos}/external", new { courseId = s.Course.Id, title = "Linked", url = "https://videos.example.org/a" }));
        Assert.Equal(HttpStatusCode.OK, (await PasteAsync(s.Teacher, external.GetProperty("id").GetGuid())).StatusCode);
    }

    // ---------- search ----------
    [Fact]
    public async Task Search_finds_the_moments_where_words_are_said_in_videos_the_person_may_watch()
    {
        var s = await NewSetupAsync();
        var id = await ReadyVideoAsync(s);
        await PasteAsync(s.Teacher, id);
        var other = await ReadyVideoAsync(s, "Poetry");
        await PasteAsync(s.Teacher, other, "WEBVTT\n\n00:00:07.000 --> 00:00:09.000\nA limerick has five lines.");

        async Task<List<JsonElement>> Search(Person who, string q) => (await ReadAsync(await who.Client.GetAsync($"{Videos}/search?q={Uri.EscapeDataString(q)}"))).EnumerateArray().ToList();
        var hits = await Search(s.Ada, "Difference QUOTIENT");                               // not case-sensitive; every word must be there
        var hit = Assert.Single(hits);
        Assert.Equal(id, hit.GetProperty("videoId").GetGuid());
        Assert.Equal("Limits", hit.GetProperty("videoTitle").GetString());
        Assert.Equal(s.Course.Title, hit.GetProperty("courseTitle").GetString());
        Assert.Equal(28.0, hit.GetProperty("startSeconds").GetDouble());
        Assert.Contains("difference quotient", hit.GetProperty("text").GetString());

        Assert.Equal(6, (await Search(s.Ada, "limit")).Count(item => item.GetProperty("videoId").GetGuid() == id));   // "limit" is in lines 
        var teachersView = (await Search(s.Teacher, "limit")).Where(item => item.GetProperty("videoId").GetGuid() == id).Select(item => item.GetProperty("startSeconds").GetDouble()).ToList();
        Assert.Equal([0.0, 5.0, 10.0, 16.0, 22.0, 28.0], teachersView);   // in the order they are said
        Assert.Empty(await Search(s.Ben, "limit"));                                             // not enrolled: nothing
        Assert.Empty(await Search(s.Ada, "zebra"));
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Ada.Client.GetAsync($"{Videos}/search?q=a")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Ada.Client.GetAsync($"{Videos}/search")).StatusCode);
    }

    [Fact]
    public async Task Search_stays_inside_the_organization()
    {
        var s = await NewSetupAsync();
        var id = await ReadyVideoAsync(s);
        await PasteAsync(s.Teacher, id);
        var other = await s.World.NewTenantAsync();
        var otherTeacher = await s.World.AddPersonAsync(other, "Olga", "TEACHER");
        Assert.Empty((await ReadAsync(await otherTeacher.Client.GetAsync($"{Videos}/search?q=limit"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await otherTeacher.Client.GetAsync($"{Videos}/{id}/transcript")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherTeacher.Client.GetAsync($"{Videos}/{id}/insights")).StatusCode);
    }

    // ---------- transcripts made by the service ----------
    [Fact]
    public async Task Making_a_transcript_needs_a_speech_service_conversion_and_an_uploaded_video()
    {
        var s = await NewSetupAsync();
        var id = await ReadyVideoAsync(s);
        var local = await s.Teacher.Client.PostAsync($"{Videos}/{id}/transcript/generate", null);
        Assert.Equal(HttpStatusCode.Conflict, local.StatusCode);
        Assert.Contains("No speech-to-text service", await local.Content.ReadAsStringAsync());

        await UseOpenAiAsync(s);
        s.Fake.Enabled = false;
        Assert.Equal(HttpStatusCode.Conflict, (await s.Teacher.Client.PostAsync($"{Videos}/{id}/transcript/generate", null)).StatusCode);   // no FFmpeg to take out the sound
        s.Fake.Enabled = true;
        var external = await ReadAsync(await s.Teacher.Client.PostAsJsonAsync($"{Videos}/external", new { courseId = s.Course.Id, title = "Linked", url = "https://videos.example.org/a" }));
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Teacher.Client.PostAsync($"{Videos}/{external.GetProperty("id").GetGuid()}/transcript/generate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Ada.Client.PostAsync($"{Videos}/{id}/transcript/generate", null)).StatusCode);
    }

    [Fact]
    public async Task A_queued_transcript_is_made_by_the_service_with_the_organizations_key_and_models()
    {
        var s = await NewSetupAsync();
        var id = await ReadyVideoAsync(s);
        await UseOpenAiAsync(s);
        var queued = await s.Teacher.Client.PostAsync($"{Videos}/{id}/transcript/generate", null);
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        Assert.Equal("Queued", (await ReadAsync(queued)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await s.Teacher.Client.PostAsync($"{Videos}/{id}/transcript/generate", null)).StatusCode);   // already queued
        Assert.Equal("None", (await ReadAsync(await s.Ada.Client.GetAsync($"{Videos}/{id}/transcript"))).GetProperty("status").GetString());   // learners see it only when ready

        Assert.Equal(1, await TranscribeAsync(s));
        var call = Assert.Single(s.Ai.Calls);
        Assert.Equal("/v1/audio/transcriptions", call.Path);
        Assert.Equal($"Bearer {Key}", call.Authorization);
        Assert.Contains("whisper-1", call.Body);
        Assert.Contains("verbose_json", call.Body);

        var transcript = await ReadAsync(await s.Ada.Client.GetAsync($"{Videos}/{id}/transcript"));
        Assert.Equal("Ready", transcript.GetProperty("status").GetString());
        Assert.Equal("Generated", transcript.GetProperty("source").GetString());
        Assert.Equal("OpenAiCompatible", transcript.GetProperty("provider").GetString());
        Assert.Equal("english", transcript.GetProperty("language").GetString());
        var lines = transcript.GetProperty("segments").EnumerateArray().ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal("Limits describe how a function behaves near a point.", lines[0].GetProperty("text").GetString());   // trimmed
        Assert.Equal(4.5, lines[1].GetProperty("startSeconds").GetDouble());
        Assert.Equal(0, await TranscribeAsync(s));
    }

    [Fact]
    public async Task When_the_service_refuses_the_transcript_fails_with_a_reason_staff_can_see_and_can_be_tried_again()
    {
        var s = await NewSetupAsync();
        var id = await ReadyVideoAsync(s);
        await UseOpenAiAsync(s);
        s.Ai.Status = HttpStatusCode.Unauthorized;
        await s.Teacher.Client.PostAsync($"{Videos}/{id}/transcript/generate", null);
        await TranscribeAsync(s);
        var failed = await ReadAsync(await s.Teacher.Client.GetAsync($"{Videos}/{id}/transcript"));
        Assert.Equal("Failed", failed.GetProperty("status").GetString());
        Assert.Contains("401", failed.GetProperty("statusMessage").GetString());
        Assert.Contains("Incorrect API key", failed.GetProperty("statusMessage").GetString());
        Assert.DoesNotContain(Key, failed.GetProperty("statusMessage").GetString());
        Assert.Equal("None", (await ReadAsync(await s.Ada.Client.GetAsync($"{Videos}/{id}/transcript"))).GetProperty("status").GetString());

        s.Ai.Status = HttpStatusCode.OK;
        Assert.Equal(HttpStatusCode.Accepted, (await s.Teacher.Client.PostAsync($"{Videos}/{id}/transcript/generate", null)).StatusCode);
        await TranscribeAsync(s);
        Assert.Equal("Ready", (await ReadAsync(await s.Teacher.Client.GetAsync($"{Videos}/{id}/transcript"))).GetProperty("status").GetString());
    }

    [Fact]
    public async Task With_auto_transcribe_a_transcript_is_queued_when_conversion_finishes()
    {
        var s = await NewSetupAsync();
        await UseOpenAiAsync(s, auto: true);
        var id = await ReadyVideoAsync(s);
        Assert.Equal("Queued", (await ReadAsync(await s.Teacher.Client.GetAsync($"{Videos}/{id}/transcript"))).GetProperty("status").GetString());
        await TranscribeAsync(s);
        Assert.Equal("Ready", (await ReadAsync(await s.Teacher.Client.GetAsync($"{Videos}/{id}/transcript"))).GetProperty("status").GetString());

        await s.Tenant.Admin.PutAsJsonAsync(SettingsUrl, new { provider = "Local" });           // switching away turns it off
        var next = await ReadyVideoAsync(s, "Next");
        Assert.Equal("None", (await ReadAsync(await s.Teacher.Client.GetAsync($"{Videos}/{next}/transcript"))).GetProperty("status").GetString());
    }

    // ---------- summaries and practice questions ----------
    [Fact]
    public async Task Summaries_and_questions_need_a_transcript_first()
    {
        var s = await NewSetupAsync();
        var id = await ReadyVideoAsync(s);
        var response = await s.Teacher.Client.PostAsJsonAsync($"{Videos}/{id}/insights", new { kind = "Summary" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Add a transcript first", await response.Content.ReadAsStringAsync());
        await PasteAsync(s.Teacher, id);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Teacher.Client.PostAsJsonAsync($"{Videos}/{id}/insights", new { kind = "Poem" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Ada.Client.PostAsJsonAsync($"{Videos}/{id}/insights", new { kind = "Summary" })).StatusCode);
    }

    [Fact]
    public async Task Without_an_outside_service_the_summary_and_questions_are_picked_from_the_transcript_and_stay_drafts_until_published()
    {
        var s = await NewSetupAsync();
        var id = await ReadyVideoAsync(s);
        await PasteAsync(s.Teacher, id);

        var summary = await ReadAsync(await s.Teacher.Client.PostAsJsonAsync($"{Videos}/{id}/insights", new { kind = "Summary" }));
        Assert.Equal("Summary", summary.GetProperty("kind").GetString());
        Assert.Equal("Local", summary.GetProperty("provider").GetString());
        Assert.False(summary.GetProperty("published").GetBoolean());
        var text = summary.GetProperty("content").GetString()!;
        Assert.Contains("Key points from “Limits”", text);
        var bullets = text.Split('\n').Where(line => line.StartsWith("- [")).ToList();
        Assert.InRange(bullets.Count, 1, 6);
        Assert.All(bullets, bullet => Assert.Matches(@"^- \[\d+:\d{2}\] \S", bullet));

        var questions = await ReadAsync(await s.Teacher.Client.PostAsJsonAsync($"{Videos}/{id}/insights", new { kind = "Questions", count = 3 }));
        var list = questions.GetProperty("questions").EnumerateArray().ToList();
        Assert.InRange(list.Count, 1, 3);
        foreach (var question in list)
        {
            Assert.Contains("_____", question.GetProperty("question").GetString());
            var options = question.GetProperty("options").EnumerateArray().Select(o => o.GetString()!).ToList();
            Assert.InRange(options.Count, 2, 4);
            Assert.Equal(options.Count, options.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            var answer = options[question.GetProperty("answerIndex").GetInt32()];
            Assert.Contains(answer, string.Join(" ", Vtt.Split('\n')), StringComparison.OrdinalIgnoreCase);   // the right answer is a word the lecturer said
            Assert.True(question.GetProperty("timestampSeconds").GetInt32() >= 0);
        }

        // Drafts are for staff only.
        Assert.Equal(2, (await ReadAsync(await s.Teacher.Client.GetAsync($"{Videos}/{id}/insights"))).GetArrayLength());
        Assert.Equal(0, (await ReadAsync(await s.Ada.Client.GetAsync($"{Videos}/{id}/insights"))).GetArrayLength());
        var summaryId = summary.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Ada.Client.PostAsJsonAsync($"{Videos}/{id}/insights/{summaryId}/publish", new { published = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Teacher.Client.PostAsJsonAsync($"{Videos}/{id}/insights/{summaryId}/publish", new { published = true })).StatusCode);
        var seen = (await ReadAsync(await s.Ada.Client.GetAsync($"{Videos}/{id}/insights"))).EnumerateArray().ToList();
        Assert.Equal("Summary", Assert.Single(seen).GetProperty("kind").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await s.Ben.Client.GetAsync($"{Videos}/{id}/insights")).StatusCode);
        await s.Teacher.Client.PostAsJsonAsync($"{Videos}/{id}/insights/{summaryId}/publish", new { published = false });
        Assert.Equal(0, (await ReadAsync(await s.Ada.Client.GetAsync($"{Videos}/{id}/insights"))).GetArrayLength());
    }

    [Fact]
    public async Task A_transcript_too_short_for_questions_gives_a_clear_message()
    {
        var s = await NewSetupAsync();
        var id = await ReadyVideoAsync(s);
        await PasteAsync(s.Teacher, id, "WEBVTT\n\n00:00:00.000 --> 00:00:02.000\nHello.");
        var response = await s.Teacher.Client.PostAsJsonAsync($"{Videos}/{id}/insights", new { kind = "Questions" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("too short", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task With_a_service_set_up_it_writes_the_summary_and_questions_from_the_transcript_and_treats_the_transcript_as_untrusted()
    {
        var s = await NewSetupAsync();
        var id = await ReadyVideoAsync(s);
        await PasteAsync(s.Teacher, id);
        await UseOpenAiAsync(s);

        var summary = await ReadAsync(await s.Teacher.Client.PostAsJsonAsync($"{Videos}/{id}/insights", new { kind = "Summary" }));
        Assert.Equal("OpenAiCompatible", summary.GetProperty("provider").GetString());
        Assert.Equal("gpt-4o-mini", summary.GetProperty("model").GetString());
        Assert.Contains("Limits describe behaviour", summary.GetProperty("content").GetString());
        var sent = s.Ai.Calls.Last().Body;
        Assert.Contains("untrusted", sent);                                   // the instruction not to obey the transcript
        Assert.Contains("Continuity means", sent);                             // the transcript is what it works from
        Assert.Contains("[0:16]", sent);                                       // with times, so it can cite moments

        var questions = await ReadAsync(await s.Teacher.Client.PostAsJsonAsync($"{Videos}/{id}/insights", new { kind = "Questions", count = 2 }));
        Assert.Equal(2, questions.GetProperty("questions").GetArrayLength());
        Assert.Contains("json_object", s.Ai.Calls.Last().Body);
        Assert.Equal(16, questions.GetProperty("questions")[1].GetProperty("timestampSeconds").GetInt32());
    }

    [Fact]
    public async Task A_service_failure_or_unusable_questions_are_reported_not_saved()
    {
        var s = await NewSetupAsync();
        var id = await ReadyVideoAsync(s);
        await PasteAsync(s.Teacher, id);
        await UseOpenAiAsync(s);

        s.Ai.Status = HttpStatusCode.Unauthorized;
        var denied = await s.Teacher.Client.PostAsJsonAsync($"{Videos}/{id}/insights", new { kind = "Summary" });
        Assert.Equal(HttpStatusCode.BadGateway, denied.StatusCode);
        Assert.Contains("Incorrect API key", await denied.Content.ReadAsStringAsync());

        s.Ai.Status = HttpStatusCode.OK;
        s.Ai.QuestionsJson = """{"questions":[{"question":"Only one answer?","options":["a"],"answerIndex":0}]}""";
        Assert.Equal(HttpStatusCode.BadGateway, (await s.Teacher.Client.PostAsJsonAsync($"{Videos}/{id}/insights", new { kind = "Questions" })).StatusCode);
        s.Ai.QuestionsJson = "this is not json";
        Assert.Equal(HttpStatusCode.BadGateway, (await s.Teacher.Client.PostAsJsonAsync($"{Videos}/{id}/insights", new { kind = "Questions" })).StatusCode);
        Assert.Equal(0, (await ReadAsync(await s.Teacher.Client.GetAsync($"{Videos}/{id}/insights"))).GetArrayLength());
    }

    [Fact]
    public async Task Staff_can_edit_a_summary_and_its_questions_which_are_checked_again_and_delete_them()
    {
        var s = await NewSetupAsync();
        var id = await ReadyVideoAsync(s);
        await PasteAsync(s.Teacher, id);
        var summary = await ReadAsync(await s.Teacher.Client.PostAsJsonAsync($"{Videos}/{id}/insights", new { kind = "Summary" }));
        var questions = await ReadAsync(await s.Teacher.Client.PostAsJsonAsync($"{Videos}/{id}/insights", new { kind = "Questions", count = 2 }));
        var summaryUrl = $"{Videos}/{id}/insights/{summary.GetProperty("id").GetGuid()}";
        var questionsUrl = $"{Videos}/{id}/insights/{questions.GetProperty("id").GetGuid()}";

        Assert.Equal(HttpStatusCode.BadRequest, (await s.Teacher.Client.PutAsJsonAsync(summaryUrl, new { content = "  " })).StatusCode);
        var edited = await ReadAsync(await s.Teacher.Client.PutAsJsonAsync(summaryUrl, new { content = "  My own summary.  " }));
        Assert.Equal("My own summary.", edited.GetProperty("content").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await s.Teacher.Client.PutAsJsonAsync(questionsUrl, new { questions = new[] { new { question = "Q?", options = new[] { "a", "b" }, answerIndex = 5 } } })).StatusCode);
        var fixedUp = await ReadAsync(await s.Teacher.Client.PutAsJsonAsync(questionsUrl, new { questions = new[] { new { question = " What is a limit? ", options = new[] { " a ", "b" }, answerIndex = 1, timestampSeconds = 3, explanation = "Because." } } }));
        var only = Assert.Single(fixedUp.GetProperty("questions").EnumerateArray());
        Assert.Equal("What is a limit?", only.GetProperty("question").GetString());
        Assert.Equal("a", only.GetProperty("options")[0].GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await s.Ada.Client.DeleteAsync(summaryUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Teacher.Client.DeleteAsync(summaryUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Teacher.Client.DeleteAsync(summaryUrl)).StatusCode);
        Assert.Equal(1, (await ReadAsync(await s.Teacher.Client.GetAsync($"{Videos}/{id}/insights"))).GetArrayLength());
    }

    [Fact]
    public async Task Deleting_a_video_removes_its_transcript_and_what_was_written_from_it()
    {
        var s = await NewSetupAsync();
        var id = await ReadyVideoAsync(s);
        await PasteAsync(s.Teacher, id);
        await s.Teacher.Client.PostAsJsonAsync($"{Videos}/{id}/insights", new { kind = "Summary" });
        Assert.Equal(HttpStatusCode.NoContent, (await s.Teacher.Client.DeleteAsync($"{Videos}/{id}")).StatusCode);
        await s.World.WithDbAsync(s.Tenant.Slug, async db =>
        {
            Assert.Empty(await db.VideoTranscripts.ToListAsync());
            Assert.Empty(await db.VideoTranscriptSegments.ToListAsync());
            Assert.Empty(await db.VideoInsights.ToListAsync());
        });
    }

    // ---------- graded quizzes ----------
    private async Task<(Setup S, Guid Video, string InsightUrl)> PublishedQuestionsAsync(bool publish = true)
    {
        var s = await NewSetupAsync();
        var id = await ReadyVideoAsync(s);
        await PasteAsync(s.Teacher, id);
        await UseOpenAiAsync(s);                                            // two questions, the second with three answers
        var questions = await ReadAsync(await s.Teacher.Client.PostAsJsonAsync($"{Videos}/{id}/insights", new { kind = "Questions", count = 2 }));
        var url = $"{Videos}/{id}/insights/{questions.GetProperty("id").GetGuid()}";
        if (publish) await s.Teacher.Client.PostAsJsonAsync($"{url}/publish", new { published = true });
        return (s, id, url);
    }

    [Fact]
    public async Task Only_checked_practice_questions_can_become_a_quiz_by_people_who_manage_assessments()
    {
        var (s, id, url) = await PublishedQuestionsAsync(publish: false);
        var draft = await s.Teacher.Client.PostAsJsonAsync($"{url}/quiz", new { });
        Assert.Equal(HttpStatusCode.Conflict, draft.StatusCode);
        Assert.Contains("Publish the questions first", await draft.Content.ReadAsStringAsync());

        var summary = await ReadAsync(await s.Teacher.Client.PostAsJsonAsync($"{Videos}/{id}/insights", new { kind = "Summary" }));
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Teacher.Client.PostAsJsonAsync($"{Videos}/{id}/insights/{summary.GetProperty("id").GetGuid()}/quiz", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Ada.Client.PostAsJsonAsync($"{url}/quiz", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Teacher.Client.PostAsJsonAsync($"{Videos}/{id}/insights/{Guid.NewGuid()}/quiz", new { })).StatusCode);

        await s.Teacher.Client.PostAsJsonAsync($"{url}/publish", new { published = true });
        foreach (var bad in new object[] { new { title = new string('x', 251) }, new { timeLimitMinutes = 0 }, new { attemptLimit = 21 }, new { pointsPerQuestion = 101 } })
            Assert.Equal(HttpStatusCode.BadRequest, (await s.Teacher.Client.PostAsJsonAsync($"{url}/quiz", bad)).StatusCode);
    }

    [Fact]
    public async Task A_quiz_made_from_the_questions_is_a_draft_with_the_same_questions_and_answers_and_only_one_is_made()
    {
        var (s, id, url) = await PublishedQuestionsAsync();
        var created = await s.Teacher.Client.PostAsJsonAsync($"{url}/quiz", new { timeLimitMinutes = 10, attemptLimit = 2, pointsPerQuestion = 3 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var quiz = await ReadAsync(created);
        Assert.Equal("Quiz: Limits", quiz.GetProperty("title").GetString());
        Assert.Equal("Draft", quiz.GetProperty("status").GetString());
        Assert.Equal(2, quiz.GetProperty("questions").GetInt32());
        Assert.Equal(6, quiz.GetProperty("totalPoints").GetInt32());
        var assessmentId = quiz.GetProperty("assessmentId").GetGuid();

        var detail = await ReadAsync(await s.Teacher.Client.GetAsync($"/api/v1/tenant/assessments/{assessmentId}"));
        var assessment = detail.GetProperty("assessment");
        Assert.Equal(10, assessment.GetProperty("timeLimitMinutes").GetInt32());
        Assert.Equal(2, assessment.GetProperty("attemptLimit").GetInt32());
        var questions = detail.GetProperty("questions").EnumerateArray().ToList();
        Assert.Equal(2, questions.Count);
        Assert.All(questions, question => { Assert.Equal("MultipleChoice", question.GetProperty("type").GetString()); Assert.Equal(3, question.GetProperty("points").GetInt32()); });
        Assert.Equal("What does a limit describe?", questions[0].GetProperty("prompt").GetString());
        Assert.Equal(["Behaviour near a point", "The slope", "The area", "The period"], questions[0].GetProperty("options").EnumerateArray().Select(o => o.GetString()));
        Assert.Equal(["Behaviour near a point"], questions[0].GetProperty("correctAnswers").EnumerateArray().Select(o => o.GetString()));
        Assert.Equal(["When the limit equals the value"], questions[1].GetProperty("correctAnswers").EnumerateArray().Select(o => o.GetString()));

        // A draft is not visible to learners; the video's set remembers its quiz and cannot make a second.
        Assert.Equal(HttpStatusCode.NotFound, (await s.Ada.Client.GetAsync($"/api/v1/tenant/assessments/{assessmentId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Ada.Client.PostAsync($"/api/v1/tenant/assessments/{assessmentId}/attempts", null)).StatusCode);
        var listed = (await ReadAsync(await s.Teacher.Client.GetAsync($"{Videos}/{id}/insights"))).EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "Questions");
        Assert.Equal(assessmentId, listed.GetProperty("quizAssessmentId").GetGuid());
        var again = await s.Teacher.Client.PostAsJsonAsync($"{url}/quiz", new { });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("already made", await again.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_published_quiz_is_taken_and_graded_automatically_like_any_other()
    {
        var (s, _, url) = await PublishedQuestionsAsync();
        var quiz = await ReadAsync(await s.Teacher.Client.PostAsJsonAsync($"{url}/quiz", new { title = "Limits check", pointsPerQuestion = 2, publish = true }));
        Assert.Equal("Published", quiz.GetProperty("status").GetString());
        Assert.Equal("Limits check", quiz.GetProperty("title").GetString());
        var assessmentId = quiz.GetProperty("assessmentId").GetGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await s.Ben.Client.PostAsync($"/api/v1/tenant/assessments/{assessmentId}/attempts", null)).StatusCode);   // not enrolled
        var started = await ReadAsync(await s.Ada.Client.PostAsync($"/api/v1/tenant/assessments/{assessmentId}/attempts", null));
        var attemptId = started.GetProperty("attempt").GetProperty("id").GetGuid();
        var questions = started.GetProperty("questions").EnumerateArray().ToList();
        Assert.All(questions, question => Assert.Equal(0, question.GetProperty("correctAnswers").GetArrayLength()));   // the answers are not given away
        string QuestionUrl(int index) => $"/api/v1/tenant/assessment-attempts/{attemptId}/answers/{questions[index].GetProperty("id").GetGuid()}";
        Assert.Equal(HttpStatusCode.OK, (await s.Ada.Client.PutAsJsonAsync(QuestionUrl(0), new { answers = new[] { "Behaviour near a point" } })).StatusCode);   // right
        Assert.Equal(HttpStatusCode.OK, (await s.Ada.Client.PutAsJsonAsync(QuestionUrl(1), new { answers = new[] { "Never" } })).StatusCode);                     // wrong

        var graded = await ReadAsync(await s.Ada.Client.PostAsync($"/api/v1/tenant/assessment-attempts/{attemptId}/submit", null));
        var attempt = graded.GetProperty("attempt");
        Assert.Equal("Graded", attempt.GetProperty("status").GetString());
        Assert.Equal(2, attempt.GetProperty("scorePoints").GetInt32());
        Assert.Equal(4, attempt.GetProperty("possiblePoints").GetInt32());
        Assert.Equal(50m, attempt.GetProperty("percentage").GetDecimal());
    }

    [Fact]
    public async Task A_quiz_can_be_published_only_with_a_published_course_and_nothing_is_made_when_it_cannot()
    {
        var s = await NewSetupAsync();
        var draftCourse = await s.World.NewCourseAsync(s.Tenant, "AI-DRAFT", publish: false);
        var form = new MultipartFormDataContent { { new StringContent(draftCourse.Id.ToString()), "courseId" }, { new StringContent("Draft video"), "title" } };
        var bytes = new byte[2048];
        new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'m', (byte)'p', (byte)'4', (byte)'2' }.CopyTo(bytes, 0);
        var file = new ByteArrayContent(bytes); file.Headers.ContentType = new MediaTypeHeaderValue("video/mp4"); form.Add(file, "file", "d.mp4");
        var id = (await ReadAsync(await s.Teacher.Client.PostAsync(Videos, form))).GetProperty("id").GetGuid();
        await s.Factory.Services.GetRequiredService<VideoProcessingService>().RunOnceAsync(CancellationToken.None);
        await PasteAsync(s.Teacher, id);
        var questions = await ReadAsync(await s.Teacher.Client.PostAsJsonAsync($"{Videos}/{id}/insights", new { kind = "Questions", count = 2 }));
        var url = $"{Videos}/{id}/insights/{questions.GetProperty("id").GetGuid()}";
        await s.Teacher.Client.PostAsJsonAsync($"{url}/publish", new { published = true });

        Assert.Equal(HttpStatusCode.Conflict, (await s.Teacher.Client.PostAsJsonAsync($"{url}/quiz", new { publish = true })).StatusCode);
        var asDraft = await s.Teacher.Client.PostAsJsonAsync($"{url}/quiz", new { });                    // a draft quiz is fine: it is published later with the course
        Assert.Equal(HttpStatusCode.Created, asDraft.StatusCode);
    }
}
