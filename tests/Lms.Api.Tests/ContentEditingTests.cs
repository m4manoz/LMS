using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

/// <summary>Removing and ordering modules and lessons, and what publishing a version does for learners.</summary>
public sealed class ContentEditingTests : IClassFixture<LmsApiFactory>
{
    private readonly TestWorld _world;

    public ContentEditingTests(LmsApiFactory factory) { _world = new TestWorld(factory); }

    private static string Url(CourseInfo course, string tail = "") => $"/api/v1/tenant/courses/{course.Id}{tail}";

    private static async Task<JsonElement> DetailAsync(Tenant t, CourseInfo course, bool draft = false) => await ReadAsync(await t.Admin.GetAsync(Url(course) + (draft ? "?version=draft" : "")));

    private static async Task<JsonElement> StartAsync(Tenant t, CourseInfo course, string summary = "Trim the course")
    {
        var response = await t.Admin.PostAsJsonAsync(Url(course, "/versions"), new { changeSummary = summary });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync(response);
    }

    private static IEnumerable<Guid> LessonIds(JsonElement detail, int module) => detail.GetProperty("modules")[module].GetProperty("lessons").EnumerateArray().Select(l => l.GetProperty("id").GetGuid());

    // ---------- draft courses ----------
    [Fact]
    public async Task Lessons_can_be_deleted_and_the_rest_are_renumbered()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "ED-1", lessonsPerModule: 3, publish: false);
        (await t.Admin.PostAsJsonAsync(Url(course, $"/lessons/{course.LessonIds[0][1]}/blocks"), new { type = "Text", text = "bye" })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NoContent, (await t.Admin.DeleteAsync(Url(course, $"/lessons/{course.LessonIds[0][1]}"))).StatusCode);
        var lessons = (await DetailAsync(t, course)).GetProperty("modules")[0].GetProperty("lessons").EnumerateArray().ToList();
        Assert.Equal(["Lesson 1.1", "Lesson 1.3"], lessons.Select(l => l.GetProperty("title").GetString()));
        Assert.Equal([1, 2], lessons.Select(l => l.GetProperty("displayOrder").GetInt32()));
        Assert.Equal(HttpStatusCode.NotFound, (await t.Admin.DeleteAsync(Url(course, $"/lessons/{course.LessonIds[0][1]}"))).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_module_removes_its_lessons_and_releases_modules_that_waited_for_it()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "ED-2", modules: 3, publish: false);
        Assert.Equal(HttpStatusCode.OK, (await t.Admin.PutAsJsonAsync(Url(course, $"/modules/{course.ModuleIds[2]}/access"), new { requiresModuleId = course.ModuleIds[1] })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await t.Admin.DeleteAsync(Url(course, $"/modules/{course.ModuleIds[1]}"))).StatusCode);
        var detail = await DetailAsync(t, course);
        Assert.Equal(["Module 1", "Module 3"], detail.GetProperty("modules").EnumerateArray().Select(m => m.GetProperty("title").GetString()));
        Assert.Equal([1, 2], detail.GetProperty("modules").EnumerateArray().Select(m => m.GetProperty("displayOrder").GetInt32()));
        var rules = await ReadAsync(await t.Admin.GetAsync(Url(course, "/access-rules")));
        Assert.All(rules.GetProperty("modules").EnumerateArray(), m => Assert.Equal(JsonValueKind.Null, m.GetProperty("requiresModuleId").ValueKind));
    }

    [Fact]
    public async Task Modules_and_lessons_can_be_put_in_a_new_order_but_only_with_every_item_once()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "ED-3", modules: 2, lessonsPerModule: 3, publish: false);

        var moduleOrder = new[] { course.ModuleIds[1], course.ModuleIds[0] };
        Assert.Equal(HttpStatusCode.NoContent, (await t.Admin.PutAsJsonAsync(Url(course, "/modules/order"), new { ids = moduleOrder })).StatusCode);
        Assert.Equal(["Module 2", "Module 1"], (await DetailAsync(t, course)).GetProperty("modules").EnumerateArray().Select(m => m.GetProperty("title").GetString()));

        var l = course.LessonIds[0];
        Assert.Equal(HttpStatusCode.NoContent, (await t.Admin.PutAsJsonAsync(Url(course, $"/modules/{course.ModuleIds[0]}/lessons/order"), new { ids = new[] { l[2], l[0], l[1] } })).StatusCode);
        var detail = await DetailAsync(t, course);
        Assert.Equal(["Lesson 1.3", "Lesson 1.1", "Lesson 1.2"], detail.GetProperty("modules")[1].GetProperty("lessons").EnumerateArray().Select(x => x.GetProperty("title").GetString()));

        Assert.Equal(HttpStatusCode.BadRequest, (await t.Admin.PutAsJsonAsync(Url(course, "/modules/order"), new { ids = new[] { course.ModuleIds[0] } })).StatusCode);                          // missing one
        Assert.Equal(HttpStatusCode.BadRequest, (await t.Admin.PutAsJsonAsync(Url(course, "/modules/order"), new { ids = new[] { course.ModuleIds[0], course.ModuleIds[0] } })).StatusCode);   // repeated
        Assert.Equal(HttpStatusCode.BadRequest, (await t.Admin.PutAsJsonAsync(Url(course, $"/modules/{course.ModuleIds[0]}/lessons/order"), new { ids = new[] { l[0], l[1], Guid.NewGuid() } })).StatusCode); // stranger
    }

    // ---------- published courses ----------
    [Fact]
    public async Task A_published_course_cannot_be_trimmed_or_reordered_until_a_new_version_exists()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "ED-4", modules: 2, lessonsPerModule: 2);
        Assert.Equal(HttpStatusCode.Conflict, (await t.Admin.DeleteAsync(Url(course, $"/lessons/{course.LessonIds[0][0]}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await t.Admin.DeleteAsync(Url(course, $"/modules/{course.ModuleIds[0]}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await t.Admin.PutAsJsonAsync(Url(course, "/modules/order"), new { ids = course.ModuleIds.Reverse() })).StatusCode);

        var started = await StartAsync(t, course);
        // The live ids still cannot be touched; the copy's can.
        Assert.Equal(HttpStatusCode.Conflict, (await t.Admin.DeleteAsync(Url(course, $"/lessons/{course.LessonIds[0][0]}"))).StatusCode);
        var copyLesson = LessonIds(started, 0).First();
        var copyModule = started.GetProperty("modules")[1].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await t.Admin.DeleteAsync(Url(course, $"/lessons/{copyLesson}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await t.Admin.DeleteAsync(Url(course, $"/modules/{copyModule}"))).StatusCode);

        var live = await DetailAsync(t, course);
        Assert.Equal(2, live.GetProperty("modules").GetArrayLength());
        Assert.Equal(2, live.GetProperty("modules")[0].GetProperty("lessons").GetArrayLength());
        var draft = await DetailAsync(t, course, draft: true);
        Assert.Equal(1, draft.GetProperty("modules").GetArrayLength());
        Assert.Equal(1, draft.GetProperty("modules")[0].GetProperty("lessons").GetArrayLength());
    }

    [Fact]
    public async Task Learners_who_have_finished_everything_that_is_left_complete_when_the_version_is_published()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "ED-5", lessonsPerModule: 2);
        var ada = await _world.AddLearnerAsync(t, "Ada");   // done with lesson 1 only
        var ben = await _world.AddLearnerAsync(t, "Ben");   // has not started
        await EnrollAsync(ada, course); await EnrollAsync(ben, course);
        await CompleteLessonAsync(ada, course, course.LessonIds[0][0]);
        Assert.Equal("Active", await EnrollmentStatusAsync(ada, course));

        var started = await StartAsync(t, course, "Dropped the second lesson");
        var second = LessonIds(started, 0).Last();
        Assert.Equal(HttpStatusCode.NoContent, (await t.Admin.DeleteAsync(Url(course, $"/lessons/{second}"))).StatusCode);
        await t.Admin.PostAsync(Url(course, "/submit-review"), null);
        Assert.Equal(HttpStatusCode.OK, (await t.Admin.PostAsync(Url(course, "/publish"), null)).StatusCode);

        Assert.Equal("Completed", await EnrollmentStatusAsync(ada, course));
        Assert.Equal("Active", await EnrollmentStatusAsync(ben, course));
        Assert.NotEmpty((await InboxAsync(ada, "COURSE_COMPLETED")).EnumerateArray());
        Assert.Empty((await InboxAsync(ada, "COURSE_UPDATED")).EnumerateArray()); // she already got the completion message
        var player = await ReadAsync(await ada.Client.GetAsync(Url(course, "/learning")));
        Assert.Equal(100, player.GetProperty("enrollment").GetProperty("progressPercent").GetInt32());
        Assert.Equal(1, player.GetProperty("modules")[0].GetProperty("lessons").GetArrayLength());
    }

    // ---------- telling learners ----------
    [Fact]
    public async Task Publishing_a_version_tells_enrolled_learners_what_changed()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "ED-6");
        var ada = await _world.AddLearnerAsync(t, "Ada");      // learning
        var ben = await _world.AddLearnerAsync(t, "Ben");      // finished earlier
        var cleo = await _world.AddLearnerAsync(t, "Cleo");    // not enrolled
        await EnrollAsync(ada, course); await EnrollAsync(ben, course);
        await CompleteCourseAsync(ben, course);

        var started = await StartAsync(t, course, "Added a week 5 lab");
        await t.Admin.PostAsJsonAsync(Url(course, $"/modules/{started.GetProperty("modules")[0].GetProperty("id").GetGuid()}/lessons"), new { title = "Week 5 lab", contentHtml = "x" });
        // Nobody hears about a version that is still being written.
        Assert.Empty((await InboxAsync(ada, "COURSE_UPDATED")).EnumerateArray());
        await t.Admin.PostAsync(Url(course, "/submit-review"), null);
        await t.Admin.PostAsync(Url(course, "/publish"), null);

        var notice = Assert.Single((await InboxAsync(ada, "COURSE_UPDATED")).EnumerateArray());
        Assert.Contains("Added a week 5 lab", notice.GetProperty("body").GetString());
        Assert.Contains(course.Title, notice.GetProperty("subject").GetString());
        Assert.Single((await InboxAsync(ben, "COURSE_UPDATED")).EnumerateArray());
        Assert.Empty((await InboxAsync(cleo, "COURSE_UPDATED")).EnumerateArray());
    }
}
