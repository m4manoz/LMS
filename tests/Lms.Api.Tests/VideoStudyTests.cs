using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Lms.Api.Domain.Courses;
using Microsoft.EntityFrameworkCore;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

/// <summary>Chapters for finding one's way in a video, learners' own notes, and lessons that complete when their videos have been watched.</summary>
public sealed class VideoStudyTests
{
    private const string Videos = "/api/v1/tenant/videos";
    private static readonly byte[] Mp4 = [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'m', (byte)'p', (byte)'4', (byte)'2', 1, 2, 3, 4, 5, 6, 7, 8];

    private sealed record Setup(TestWorld World, Tenant Tenant, Person Teacher, Person Ada, Person Ben, Person Outsider, CourseInfo Course);

    private static async Task<Setup> NewSetupAsync(int lessons = 2)
    {
        var factory = new LmsApiFactory { Transcoder = new FakeVideoTranscoder { Enabled = false } };
        var world = new TestWorld(factory);
        var t = await world.NewTenantAsync();
        var teacher = await world.AddPersonAsync(t, "Tara", "TEACHER");
        var ada = await world.AddLearnerAsync(t, "Ada");
        var ben = await world.AddLearnerAsync(t, "Ben");
        var outsider = await world.AddLearnerAsync(t, "Olga");
        var course = await world.NewCourseAsync(t, "STUDY-1", lessonsPerModule: lessons);
        Assert.True((await EnrollAsync(ada, course)).IsSuccessStatusCode);
        Assert.True((await EnrollAsync(ben, course)).IsSuccessStatusCode);
        return new Setup(world, t, teacher, ada, ben, outsider, course);
    }

    private static async Task<Guid> UploadAsync(Setup s, string title, int? seconds = 120)
    {
        var form = new MultipartFormDataContent { { new StringContent(s.Course.Id.ToString()), "courseId" }, { new StringContent(title), "title" }, { new StringContent((seconds ?? 0).ToString()), "durationSeconds" } };
        var file = new ByteArrayContent(Mp4); file.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        form.Add(file, "file", $"{title}.mp4");
        var response = await s.Teacher.Client.PostAsync(Videos, form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> SaveChaptersAsync(Person who, Guid id, params (int Start, string Title)[] chapters)
        => who.Client.PutAsJsonAsync($"{Videos}/{id}/chapters", new { chapters = chapters.Select(item => new { startSeconds = item.Start, title = item.Title }).ToArray() });

    // ---------- chapters ----------
    [Fact]
    public async Task Staff_set_the_outline_and_everyone_who_can_watch_the_video_sees_it_in_order()
    {
        var s = await NewSetupAsync();
        var id = await UploadAsync(s, "Limits");
        var saved = await SaveChaptersAsync(s.Teacher, id, (60, "Epsilon and delta"), (0, "Introduction"), (100, "Summary"));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(["Introduction", "Epsilon and delta", "Summary"], (await ReadAsync(saved)).EnumerateArray().Select(item => item.GetProperty("title").GetString()).ToArray());

        var seen = await ReadAsync(await s.Ada.Client.GetAsync($"{Videos}/{id}/chapters"));
        Assert.Equal([0, 60, 100], seen.EnumerateArray().Select(item => item.GetProperty("startSeconds").GetInt32()).ToArray());
        Assert.Equal(HttpStatusCode.NotFound, (await s.Outsider.Client.GetAsync($"{Videos}/{id}/chapters")).StatusCode);   // not enrolled

        await SaveChaptersAsync(s.Teacher, id, (0, "Only part"));                                                         // saving again replaces the outline
        Assert.Single((await ReadAsync(await s.Ada.Client.GetAsync($"{Videos}/{id}/chapters"))).EnumerateArray());
        await SaveChaptersAsync(s.Teacher, id);
        Assert.Empty((await ReadAsync(await s.Ada.Client.GetAsync($"{Videos}/{id}/chapters"))).EnumerateArray());      // and an empty list clears it
    }

    [Fact]
    public async Task Only_staff_can_set_chapters_and_a_bad_outline_is_refused_without_changing_the_old_one()
    {
        var s = await NewSetupAsync();
        var id = await UploadAsync(s, "Limits");
        await SaveChaptersAsync(s.Teacher, id, (0, "Intro"));
        Assert.Equal(HttpStatusCode.Forbidden, (await SaveChaptersAsync(s.Ada, id, (0, "Mine"))).StatusCode);

        foreach (var bad in new (int, string)[][] { [(5, "Late start")], [(0, "A"), (0, "B")], [(0, " ")], [(0, "A"), (500, "Past the end")], [(-1, "Before")], [(0, new string('x', 121))] })
            Assert.Equal(HttpStatusCode.BadRequest, (await SaveChaptersAsync(s.Teacher, id, bad)).StatusCode);
        var tooMany = Enumerable.Range(0, 61).Select(index => (index, $"Part {index}")).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest, (await SaveChaptersAsync(s.Teacher, id, tooMany)).StatusCode);
        Assert.Equal("Intro", (await ReadAsync(await s.Ada.Client.GetAsync($"{Videos}/{id}/chapters"))).EnumerateArray().Single().GetProperty("title").GetString());

        var external = await ReadAsync(await s.Teacher.Client.PostAsJsonAsync($"{Videos}/external", new { courseId = s.Course.Id, title = "Khan", url = "https://www.youtube.com/watch?v=abc" }));
        Assert.Equal(HttpStatusCode.BadRequest, (await SaveChaptersAsync(s.Teacher, external.GetProperty("id").GetGuid(), (0, "Intro"))).StatusCode);
    }

    // ---------- notes ----------
    [Fact]
    public async Task A_learner_keeps_notes_at_moments_of_the_video_that_only_they_can_see()
    {
        var s = await NewSetupAsync();
        var id = await UploadAsync(s, "Limits");
        var late = await s.Ada.Client.PostAsJsonAsync($"{Videos}/{id}/notes", new { positionSeconds = 90, text = "Check the second example" });
        Assert.Equal(HttpStatusCode.Created, late.StatusCode);
        var bookmark = await s.Ada.Client.PostAsJsonAsync($"{Videos}/{id}/notes", new { positionSeconds = 10 });                // no words: a plain bookmark
        Assert.Equal(HttpStatusCode.Created, bookmark.StatusCode);
        await s.Ben.Client.PostAsJsonAsync($"{Videos}/{id}/notes", new { positionSeconds = 5, text = "Ben's" });

        var mine = (await ReadAsync(await s.Ada.Client.GetAsync($"{Videos}/{id}/notes"))).EnumerateArray().ToList();
        Assert.Equal([10, 90], mine.Select(item => item.GetProperty("positionSeconds").GetInt32()).ToArray());                // in order of the moment
        Assert.Equal(["", "Check the second example"], mine.Select(item => item.GetProperty("text").GetString()).ToArray());
        Assert.Equal("Ben's", (await ReadAsync(await s.Ben.Client.GetAsync($"{Videos}/{id}/notes"))).EnumerateArray().Single().GetProperty("text").GetString());
        Assert.Empty((await ReadAsync(await s.Teacher.Client.GetAsync($"{Videos}/{id}/notes"))).EnumerateArray());            // not even the teacher reads a learner's notes
        Assert.Equal(HttpStatusCode.NotFound, (await s.Outsider.Client.GetAsync($"{Videos}/{id}/notes")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Outsider.Client.PostAsJsonAsync($"{Videos}/{id}/notes", new { positionSeconds = 1, text = "x" })).StatusCode);
    }

    [Fact]
    public async Task A_note_can_be_changed_and_deleted_only_by_its_owner_and_is_checked()
    {
        var s = await NewSetupAsync();
        var id = await UploadAsync(s, "Limits");
        var note = (await ReadAsync(await s.Ada.Client.PostAsJsonAsync($"{Videos}/{id}/notes", new { positionSeconds = 30, text = "first" }))).GetProperty("id").GetGuid();

        var changed = await s.Ada.Client.PutAsJsonAsync($"{Videos}/{id}/notes/{note}", new { positionSeconds = 31, text = "  second  " });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var body = await ReadAsync(changed);
        Assert.Equal((31, "second"), (body.GetProperty("positionSeconds").GetInt32(), body.GetProperty("text").GetString()));

        Assert.Equal(HttpStatusCode.NotFound, (await s.Ben.Client.PutAsJsonAsync($"{Videos}/{id}/notes/{note}", new { positionSeconds = 1, text = "stolen" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Ben.Client.DeleteAsync($"{Videos}/{id}/notes/{note}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Ada.Client.PostAsJsonAsync($"{Videos}/{id}/notes", new { positionSeconds = 121, text = "after the end" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Ada.Client.PostAsJsonAsync($"{Videos}/{id}/notes", new { positionSeconds = -1, text = "before" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Ada.Client.PostAsJsonAsync($"{Videos}/{id}/notes", new { positionSeconds = 1, text = new string('x', 1001) })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await s.Ada.Client.DeleteAsync($"{Videos}/{id}/notes/{note}")).StatusCode);
        Assert.Empty((await ReadAsync(await s.Ada.Client.GetAsync($"{Videos}/{id}/notes"))).EnumerateArray());
    }

    [Fact]
    public async Task Notes_and_chapters_go_when_the_video_is_deleted()
    {
        var s = await NewSetupAsync();
        var id = await UploadAsync(s, "Limits");
        await SaveChaptersAsync(s.Teacher, id, (0, "Intro"));
        await s.Ada.Client.PostAsJsonAsync($"{Videos}/{id}/notes", new { positionSeconds = 1, text = "x" });
        Assert.Equal(HttpStatusCode.NoContent, (await s.Teacher.Client.DeleteAsync($"{Videos}/{id}")).StatusCode);
        await s.World.WithDbAsync(s.Tenant.Slug, async db => { Assert.Empty(await db.VideoChapters.ToListAsync()); Assert.Empty(await db.VideoNotes.ToListAsync()); });
    }

    // ---------- lessons that complete when their videos are watched ----------
    private static async Task<(Guid Asset, Guid Video)> AddVideoToLessonAsync(Setup s, Guid lessonId, string title, int order)
    {
        var video = await UploadAsync(s, title);
        Guid asset = Guid.Empty;
        await s.World.WithDbAsync(s.Tenant.Slug, async db =>
        {
            asset = (await db.Videos.SingleAsync(item => item.Id == video)).ContentAssetId!.Value;
            var now = DateTimeOffset.UtcNow;
            db.LessonBlocks.Add(new LessonBlock { Id = Guid.NewGuid(), TenantId = (await db.Courses.SingleAsync(item => item.Id == s.Course.Id)).TenantId, CourseLessonId = lessonId, Type = BlockType.Video, DisplayOrder = order, Title = title, ContentAssetId = asset, CreatedAtUtc = now, UpdatedAtUtc = now });
            await db.SaveChangesAsync();
        });
        return (asset, video);
    }

    private static Task SetRuleInDbAsync(Setup s, Guid lessonId, bool value)
        => s.World.WithDbAsync(s.Tenant.Slug, async db => { (await db.CourseLessons.SingleAsync(item => item.Id == lessonId)).CompleteWhenVideosWatched = value; await db.SaveChangesAsync(); });

    private static Task<HttpResponseMessage> WatchedAsync(Person who, Guid videoId, bool finished = true)
        => who.Client.PostAsJsonAsync($"{Videos}/{videoId}/progress", new { positionSeconds = finished ? 118 : 20, durationSeconds = 120, watchedSecondsDelta = 20, started = true, completed = finished ? true : (bool?)null });

    private static async Task<string> LessonStatusAsync(Person who, CourseInfo course, Guid lessonId)
    {
        var player = await ReadAsync(await who.Client.GetAsync($"/api/v1/tenant/courses/{course.Id}/learning"));
        return player.GetProperty("modules").EnumerateArray().SelectMany(m => m.GetProperty("lessons").EnumerateArray()).Single(l => l.GetProperty("id").GetGuid() == lessonId).GetProperty("status").GetString()!;
    }

    [Fact]
    public async Task A_lesson_set_to_complete_by_watching_completes_when_the_last_of_its_videos_is_finished()
    {
        var s = await NewSetupAsync();
        var lesson = s.Course.LessonIds[0][0];
        var first = await AddVideoToLessonAsync(s, lesson, "Part one", 1);
        var second = await AddVideoToLessonAsync(s, lesson, "Part two", 2);
        await SetRuleInDbAsync(s, lesson, true);

        Assert.Equal(HttpStatusCode.OK, (await WatchedAsync(s.Ada, first.Video, finished: false)).StatusCode);
        Assert.Equal("NotStarted", await LessonStatusAsync(s.Ada, s.Course, lesson));                                           // watching a little changes nothing
        await WatchedAsync(s.Ada, first.Video);
        Assert.Equal("NotStarted", await LessonStatusAsync(s.Ada, s.Course, lesson));                                           // one of two
        await WatchedAsync(s.Ada, second.Video);
        Assert.Equal("Completed", await LessonStatusAsync(s.Ada, s.Course, lesson));
        Assert.Equal("NotStarted", await LessonStatusAsync(s.Ben, s.Course, lesson));                                           // only for the person who watched

        var enrollment = (await ReadAsync(await s.Ada.Client.GetAsync("/api/v1/tenant/enrollments"))).EnumerateArray().Single();
        Assert.Equal(50, enrollment.GetProperty("progressPercent").GetInt32());                                                  // one of the course's two lessons
        await s.World.WithDbAsync(s.Tenant.Slug, async db => Assert.Single(await db.LearningProgressEvents.Where(item => item.LessonId == lesson && item.LearnerUserId == s.Ada.Id && item.EventType == Lms.Api.Domain.Learning.LearningProgressEventType.LessonCompleted).ToListAsync()));

        await WatchedAsync(s.Ada, second.Video);                                                                                 // watching again does not complete it twice
        await s.World.WithDbAsync(s.Tenant.Slug, async db => Assert.Single(await db.LearningProgressEvents.Where(item => item.LessonId == lesson && item.LearnerUserId == s.Ada.Id && item.EventType == Lms.Api.Domain.Learning.LearningProgressEventType.LessonCompleted).ToListAsync()));
    }

    [Fact]
    public async Task Finishing_the_only_video_of_a_lesson_that_completes_by_watching_completes_it_and_the_course_when_it_was_the_last()
    {
        var s = await NewSetupAsync(lessons: 1);
        var lesson = s.Course.LessonIds[0][0];
        var only = await AddVideoToLessonAsync(s, lesson, "Only part", 1);
        await SetRuleInDbAsync(s, lesson, true);
        await WatchedAsync(s.Ada, only.Video);
        Assert.Equal("Completed", await LessonStatusAsync(s.Ada, s.Course, lesson));
        Assert.Equal("Completed", await EnrollmentStatusAsync(s.Ada, s.Course));
    }

    [Fact]
    public async Task A_lesson_not_set_to_complete_by_watching_is_left_alone_and_so_is_a_person_who_is_not_a_learner()
    {
        var s = await NewSetupAsync();
        var lesson = s.Course.LessonIds[0][0];
        var video = await AddVideoToLessonAsync(s, lesson, "Part one", 1);
        await WatchedAsync(s.Ada, video.Video);
        Assert.Equal("NotStarted", await LessonStatusAsync(s.Ada, s.Course, lesson));                                           // the rule is off by default

        await SetRuleInDbAsync(s, lesson, true);
        Assert.Equal(HttpStatusCode.OK, (await WatchedAsync(s.Teacher, video.Video)).StatusCode);                                // a teacher watching is not a learner: no error, no progress
        await s.World.WithDbAsync(s.Tenant.Slug, async db => Assert.Empty(await db.LessonProgress.Where(item => item.LearnerUserId == s.Teacher.Id).ToListAsync()));
    }

    [Fact]
    public async Task The_rule_is_set_on_a_lesson_of_the_version_being_edited_and_carries_into_the_next_versions()
    {
        var s = await NewSetupAsync();
        var liveLesson = s.Course.LessonIds[0][0];
        var live = await s.Tenant.Admin.PutAsJsonAsync($"/api/v1/tenant/courses/{s.Course.Id}/lessons/{liveLesson}/completion-rule", new { completeWhenVideosWatched = true });
        Assert.Equal(HttpStatusCode.Conflict, live.StatusCode);                                                                  // a published version cannot be changed

        var started = await ReadAsync(await s.Tenant.Admin.PostAsJsonAsync($"/api/v1/tenant/courses/{s.Course.Id}/versions", new { changeSummary = "Watch to complete" }));
        var draftLesson = started.GetProperty("modules")[0].GetProperty("lessons")[0].GetProperty("id").GetGuid();
        Assert.False(started.GetProperty("modules")[0].GetProperty("lessons")[0].GetProperty("completeWhenVideosWatched").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Ada.Client.PutAsJsonAsync($"/api/v1/tenant/courses/{s.Course.Id}/lessons/{draftLesson}/completion-rule", new { completeWhenVideosWatched = true })).StatusCode);
        var set = await s.Tenant.Admin.PutAsJsonAsync($"/api/v1/tenant/courses/{s.Course.Id}/lessons/{draftLesson}/completion-rule", new { completeWhenVideosWatched = true });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Tenant.Admin.PutAsJsonAsync($"/api/v1/tenant/courses/{s.Course.Id}/lessons/{Guid.NewGuid()}/completion-rule", new { completeWhenVideosWatched = true })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await s.Tenant.Admin.PostAsync($"/api/v1/tenant/courses/{s.Course.Id}/submit-review", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Tenant.Admin.PostAsync($"/api/v1/tenant/courses/{s.Course.Id}/publish", null)).StatusCode);
        var next = await ReadAsync(await s.Tenant.Admin.PostAsJsonAsync($"/api/v1/tenant/courses/{s.Course.Id}/versions", new { changeSummary = "Another" }));
        Assert.True(next.GetProperty("modules")[0].GetProperty("lessons")[0].GetProperty("completeWhenVideosWatched").GetBoolean());   // the copy keeps the rule
    }

    [Fact]
    public async Task A_video_block_of_a_library_video_tells_the_lesson_page_which_video_to_play()
    {
        var s = await NewSetupAsync();
        var lesson = s.Course.LessonIds[0][0];
        var video = await AddVideoToLessonAsync(s, lesson, "Part one", 1);
        var blocks = (await ReadAsync(await s.Ada.Client.GetAsync($"/api/v1/tenant/courses/{s.Course.Id}/lessons/{lesson}/blocks"))).EnumerateArray().ToList();
        Assert.Equal(video.Video, blocks.Single().GetProperty("videoId").GetGuid());
    }
}
