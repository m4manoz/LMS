using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

/// <summary>Editing a published course through a new draft version without disturbing learners.</summary>
public sealed class CourseVersioningTests : IClassFixture<LmsApiFactory>
{
    private readonly TestWorld _world;

    public CourseVersioningTests(LmsApiFactory factory) { _world = new TestWorld(factory); }

    private static string Url(CourseInfo course, string tail = "") => $"/api/v1/tenant/courses/{course.Id}{tail}";

    private static async Task<JsonElement> StartAsync(Tenant t, CourseInfo course, string? summary = "Add a lesson")
    {
        var response = await t.Admin.PostAsJsonAsync(Url(course, "/versions"), new { changeSummary = summary });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync(response);
    }

    private static async Task<JsonElement> DraftAsync(Tenant t, CourseInfo course) => await ReadAsync(await t.Admin.GetAsync(Url(course) + "?version=draft"));

    private static async Task PublishDraftAsync(Tenant t, CourseInfo course)
    {
        Assert.Equal(HttpStatusCode.OK, (await t.Admin.PostAsync(Url(course, "/submit-review"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await t.Admin.PostAsync(Url(course, "/publish"), null)).StatusCode);
    }

    private static async Task<JsonElement> PlayerAsync(Person person, CourseInfo course) => await ReadAsync(await person.Client.GetAsync(Url(course, "/learning")));

    private static IEnumerable<JsonElement> Lessons(JsonElement player) => player.GetProperty("modules").EnumerateArray().SelectMany(m => m.GetProperty("lessons").EnumerateArray());

    [Fact]
    public async Task A_new_version_is_a_copy_that_learners_do_not_see_until_it_is_published()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "VER-1", lessonsPerModule: 2);
        var ada = await _world.AddLearnerAsync(t, "Ada");
        await EnrollAsync(ada, course);

        var started = await StartAsync(t, course);
        Assert.True(started.GetProperty("viewingDraft").GetBoolean());
        Assert.Equal(2, started.GetProperty("draftVersion").GetProperty("versionNumber").GetInt32());
        Assert.Equal("Published", started.GetProperty("course").GetProperty("status").GetString());
        var draftLessons = started.GetProperty("modules")[0].GetProperty("lessons").EnumerateArray().ToList();
        Assert.Equal(2, draftLessons.Count);
        Assert.DoesNotContain(draftLessons, l => course.LessonIds[0].Contains(l.GetProperty("id").GetGuid())); // copies, not the originals

        // Edit the copy: rename a lesson and add one.
        var draftModule = started.GetProperty("modules")[0].GetProperty("id").GetGuid();
        var draftLesson = draftLessons[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await t.Admin.PutAsJsonAsync(Url(course, $"/lessons/{draftLesson}"), new { title = "Renamed", contentHtml = "New body" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await t.Admin.PostAsJsonAsync(Url(course, $"/modules/{draftModule}/lessons"), new { title = "Brand new", contentHtml = "x" })).StatusCode);

        // Learners still get the published version, untouched.
        var titles = Lessons(await PlayerAsync(ada, course)).Select(l => l.GetProperty("title").GetString()).ToList();
        Assert.Equal(["Lesson 1.1", "Lesson 1.2"], titles);
        var staffLive = await ReadAsync(await t.Admin.GetAsync(Url(course)));
        Assert.Equal(2, staffLive.GetProperty("modules")[0].GetProperty("lessons").GetArrayLength());
        Assert.Equal(3, (await DraftAsync(t, course)).GetProperty("modules")[0].GetProperty("lessons").GetArrayLength());
    }

    [Fact]
    public async Task Only_a_published_course_without_a_version_in_progress_can_start_one()
    {
        var t = await _world.NewTenantAsync();
        var draftCourse = await _world.NewCourseAsync(t, "VER-2D", publish: false);
        Assert.Equal(HttpStatusCode.Conflict, (await t.Admin.PostAsJsonAsync(Url(draftCourse, "/versions"), new { })).StatusCode);

        var course = await _world.NewCourseAsync(t, "VER-2");
        await StartAsync(t, course);
        Assert.Equal(HttpStatusCode.Conflict, (await t.Admin.PostAsJsonAsync(Url(course, "/versions"), new { })).StatusCode);

        var ada = await _world.AddLearnerAsync(t, "Ada");
        Assert.Equal(HttpStatusCode.Forbidden, (await ada.Client.PostAsJsonAsync(Url(course, "/versions"), new { })).StatusCode);
    }

    [Fact]
    public async Task Published_content_stays_locked_while_the_copy_is_edited()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "VER-3");
        // Without a version in progress the live lesson cannot be edited.
        Assert.Equal(HttpStatusCode.Conflict, (await t.Admin.PutAsJsonAsync(Url(course, $"/lessons/{course.LessonIds[0][0]}"), new { title = "Sneaky" })).StatusCode);
        await StartAsync(t, course);
        // And not once there is one either: only the copy can change.
        Assert.Equal(HttpStatusCode.Conflict, (await t.Admin.PutAsJsonAsync(Url(course, $"/lessons/{course.LessonIds[0][0]}"), new { title = "Sneaky" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await t.Admin.PostAsJsonAsync(Url(course, $"/modules/{course.ModuleIds[0]}/lessons"), new { title = "Sneaky" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await t.Admin.PutAsJsonAsync(Url(course, $"/modules/{course.ModuleIds[0]}"), new { title = "Sneaky" })).StatusCode);
    }

    [Fact]
    public async Task Progress_notes_and_bookmarks_follow_lessons_into_the_new_version()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "VER-4", lessonsPerModule: 2);
        var ada = await _world.AddLearnerAsync(t, "Ada");
        await EnrollAsync(ada, course);
        await CompleteLessonAsync(ada, course, course.LessonIds[0][0]);
        (await ada.Client.PostAsJsonAsync(Url(course, "/learning/notes"), new { lessonId = course.LessonIds[0][0], content = "Remember this" })).EnsureSuccessStatusCode();
        (await ada.Client.PostAsJsonAsync(Url(course, "/learning/bookmarks"), new { lessonId = course.LessonIds[0][1], title = "Come back" })).EnsureSuccessStatusCode();

        var started = await StartAsync(t, course);
        var module = started.GetProperty("modules")[0];
        await t.Admin.PostAsJsonAsync(Url(course, $"/modules/{module.GetProperty("id").GetGuid()}/lessons"), new { title = "Extra", contentHtml = "x" });
        var firstCopy = module.GetProperty("lessons")[0].GetProperty("id").GetGuid();
        await t.Admin.PutAsJsonAsync(Url(course, $"/lessons/{firstCopy}"), new { title = "Lesson 1.1 (revised)", contentHtml = "Better" });
        await PublishDraftAsync(t, course);

        var player = await PlayerAsync(ada, course);
        var lessons = Lessons(player).ToList();
        Assert.Equal(3, lessons.Count);
        var revised = lessons.Single(l => l.GetProperty("title").GetString() == "Lesson 1.1 (revised)");
        Assert.Equal("Completed", revised.GetProperty("status").GetString());           // progress moved to the renamed lesson
        Assert.Equal(firstCopy, revised.GetProperty("id").GetGuid());
        Assert.Equal(33, player.GetProperty("enrollment").GetProperty("progressPercent").GetInt32()); // 1 of 3 lessons now
        Assert.DoesNotContain(lessons, l => course.LessonIds[0].Contains(l.GetProperty("id").GetGuid()));

        var notes = await ReadAsync(await ada.Client.GetAsync(Url(course, "/learning/notes")));
        Assert.Equal(firstCopy, notes[0].GetProperty("lessonId").GetGuid());
        var bookmarks = await ReadAsync(await ada.Client.GetAsync(Url(course, "/learning/bookmarks")));
        Assert.Equal(lessons.Single(l => l.GetProperty("title").GetString() == "Lesson 1.2").GetProperty("id").GetGuid(), bookmarks[0].GetProperty("lessonId").GetGuid());

        // The learner carries on in the new version and can finish it.
        foreach (var lesson in lessons.Where(l => l.GetProperty("status").GetString() != "Completed"))
            await CompleteLessonAsync(ada, course, lesson.GetProperty("id").GetGuid());
        Assert.Equal("Completed", await EnrollmentStatusAsync(ada, course));
    }

    [Fact]
    public async Task A_learner_who_already_completed_the_course_stays_completed()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "VER-5");
        var ada = await _world.AddLearnerAsync(t, "Ada");
        await EnrollAsync(ada, course);
        await CompleteCourseAsync(ada, course);
        Assert.Equal("Completed", await EnrollmentStatusAsync(ada, course));

        var started = await StartAsync(t, course);
        await t.Admin.PostAsJsonAsync(Url(course, $"/modules/{started.GetProperty("modules")[0].GetProperty("id").GetGuid()}/lessons"), new { title = "Bonus", contentHtml = "x" });
        await PublishDraftAsync(t, course);

        Assert.Equal("Completed", await EnrollmentStatusAsync(ada, course));
        var player = await PlayerAsync(ada, course);
        Assert.Equal(100, player.GetProperty("enrollment").GetProperty("progressPercent").GetInt32());
        Assert.Equal(2, Lessons(player).Count());
    }

    [Fact]
    public async Task Rules_and_blocks_are_copied_when_a_version_is_started()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "VER-7", modules: 2, publish: false);
        (await t.Admin.PostAsJsonAsync(Url(course, $"/lessons/{course.LessonIds[0][0]}/blocks"), new { type = "Text", text = "Hello" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await t.Admin.PutAsJsonAsync(Url(course, $"/modules/{course.ModuleIds[1]}/access"), new { requiresModuleId = course.ModuleIds[0] })).StatusCode);
        await t.Admin.PostAsync(Url(course, "/submit-review"), null);
        await t.Admin.PostAsync(Url(course, "/publish"), null);
        var ada = await _world.AddLearnerAsync(t, "Ada");
        await EnrollAsync(ada, course);

        var started = await StartAsync(t, course);
        var copyLesson = started.GetProperty("modules")[0].GetProperty("lessons")[0].GetProperty("id").GetGuid();
        var blocks = await ReadAsync(await t.Admin.GetAsync(Url(course, $"/lessons/{copyLesson}/blocks")));
        Assert.Equal("Hello", blocks[0].GetProperty("text").GetString());
        // Learners cannot read the copy's lessons.
        Assert.Equal(HttpStatusCode.NotFound, (await ada.Client.GetAsync(Url(course, $"/lessons/{copyLesson}/blocks"))).StatusCode);

        // Edit blocks in the copy; the live lesson's blocks are untouched.
        Assert.Equal(HttpStatusCode.Created, (await t.Admin.PostAsJsonAsync(Url(course, $"/lessons/{copyLesson}/blocks"), new { type = "Text", text = "Second" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await t.Admin.PostAsJsonAsync(Url(course, $"/lessons/{course.LessonIds[0][0]}/blocks"), new { type = "Text", text = "Nope" })).StatusCode);

        await PublishDraftAsync(t, course);
        var live = await ReadAsync(await ada.Client.GetAsync(Url(course, $"/lessons/{copyLesson}/blocks")));
        Assert.Equal(2, live.GetArrayLength());

        // The "finish module 1 first" rule now points at the new module 1.
        var player = await PlayerAsync(ada, course);
        var second = player.GetProperty("modules")[1];
        Assert.True(second.GetProperty("locked").GetBoolean());
        var newFirst = started.GetProperty("modules")[0].GetProperty("lessons")[0].GetProperty("id").GetGuid();
        await CompleteLessonAsync(ada, course, newFirst);
        Assert.False((await PlayerAsync(ada, course)).GetProperty("modules")[1].GetProperty("locked").GetBoolean());
    }

    [Fact]
    public async Task Discarding_a_version_leaves_the_course_and_learners_alone()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "VER-8");
        var ada = await _world.AddLearnerAsync(t, "Ada");
        await EnrollAsync(ada, course);
        await CompleteLessonAsync(ada, course, course.LessonIds[0][0]);
        await StartAsync(t, course);

        Assert.Equal(HttpStatusCode.OK, (await t.Admin.DeleteAsync(Url(course, "/versions/draft"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t.Admin.GetAsync(Url(course) + "?version=draft")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t.Admin.DeleteAsync(Url(course, "/versions/draft"))).StatusCode);
        Assert.Equal("Completed", (await PlayerAsync(ada, course)).GetProperty("modules")[0].GetProperty("lessons")[0].GetProperty("status").GetString());

        // A fresh version can be started afterwards; the discarded number is free to reuse.
        var again = await StartAsync(t, course);
        Assert.Equal(2, again.GetProperty("draftVersion").GetProperty("versionNumber").GetInt32());
    }

    [Fact]
    public async Task A_version_goes_through_review_and_cannot_be_edited_while_in_review()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "VER-9");
        var started = await StartAsync(t, course);
        var module = started.GetProperty("modules")[0].GetProperty("id").GetGuid();

        // Publishing straight from draft is refused: it must be reviewed first.
        Assert.Equal(HttpStatusCode.Conflict, (await t.Admin.PostAsync(Url(course, "/publish"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await t.Admin.PostAsync(Url(course, "/submit-review"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await t.Admin.PostAsync(Url(course, "/submit-review"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await t.Admin.PostAsJsonAsync(Url(course, $"/modules/{module}/lessons"), new { title = "Late" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await t.Admin.PostAsync(Url(course, "/publish"), null)).StatusCode);

        var detail = await ReadAsync(await t.Admin.GetAsync(Url(course)));
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("draftVersion").ValueKind);
        Assert.Equal(2, detail.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32());
        Assert.Equal("Published", detail.GetProperty("currentVersion").GetProperty("status").GetString());
        var events = detail.GetProperty("workflow").EnumerateArray().Select(e => e.GetProperty("eventType").GetString()).ToList();
        Assert.Contains("version_started", events);
        Assert.Contains("version_published", events);
    }

    [Fact]
    public async Task Learners_never_see_that_a_new_version_exists()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "VER-10");
        var ada = await _world.AddLearnerAsync(t, "Ada");
        await StartAsync(t, course);
        var seen = await ReadAsync(await ada.Client.GetAsync(Url(course) + "?version=draft"));
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("draftVersion").ValueKind);
        Assert.False(seen.GetProperty("viewingDraft").GetBoolean());
    }
}
