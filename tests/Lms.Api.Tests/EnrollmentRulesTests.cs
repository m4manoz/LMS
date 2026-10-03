using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lms.Api.Domain.Learning;
using Microsoft.EntityFrameworkCore;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

/// <summary>Capacity and the waitlist, prerequisites, and drip rules.</summary>
public sealed class EnrollmentRulesTests : IClassFixture<LmsApiFactory>
{
    private readonly LmsApiFactory _factory;
    private readonly TestWorld _world;

    public EnrollmentRulesTests(LmsApiFactory factory) { _factory = factory; _world = new TestWorld(factory); }

    private static Task<HttpResponseMessage> Withdraw(Person person, Guid enrollmentId) => person.Client.PostAsync($"/api/v1/tenant/enrollments/{enrollmentId}/withdraw", null);

    private static async Task<Guid> EnrollmentIdAsync(Person person, CourseInfo course)
    {
        var list = await ReadAsync(await person.Client.GetAsync("/api/v1/tenant/enrollments"));
        return list.EnumerateArray().Single(item => item.GetProperty("courseId").GetGuid() == course.Id).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> SummaryAsync(Tenant tenant, CourseInfo course)
        => await ReadAsync(await tenant.Admin.GetAsync($"/api/v1/tenant/courses/{course.Id}/enrollment-summary"));

    // ---------- capacity and the waitlist ----------
    [Fact]
    public async Task A_full_course_waitlists_learners_in_the_order_they_joined()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "CAP-1", capacity: 1);
        var first = await _world.AddLearnerAsync(t, "Ada");
        var second = await _world.AddLearnerAsync(t, "Ben");
        var third = await _world.AddLearnerAsync(t, "Cleo");

        Assert.Equal(HttpStatusCode.Created, (await EnrollAsync(first, course)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await EnrollAsync(second, course)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await EnrollAsync(third, course)).StatusCode);

        var summary = await SummaryAsync(t, course);
        Assert.Equal(1, summary.GetProperty("active").GetInt32());
        Assert.Equal(0, summary.GetProperty("freeSeats").GetInt32());
        var waitlist = summary.GetProperty("waitlist");
        Assert.Equal(new[] { "Ben", "Cleo" }, waitlist.EnumerateArray().Select(item => item.GetProperty("name").GetString()).ToArray());
        Assert.Equal(new[] { 1, 2 }, waitlist.EnumerateArray().Select(item => item.GetProperty("position").GetInt32()).ToArray());
    }

    [Fact]
    public async Task Withdrawing_frees_the_seat_for_the_longest_waiting_learner_who_is_told_about_it()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "CAP-2", capacity: 1);
        var holder = await _world.AddLearnerAsync(t, "Ada");
        var next = await _world.AddLearnerAsync(t, "Ben");
        var last = await _world.AddLearnerAsync(t, "Cleo");
        await EnrollAsync(holder, course); await EnrollAsync(next, course); await EnrollAsync(last, course);

        var withdrawn = await ReadAsync(await Withdraw(holder, await EnrollmentIdAsync(holder, course)));
        Assert.Equal("Withdrawn", withdrawn.GetProperty("status").GetString());
        Assert.Equal(1, withdrawn.GetProperty("promoted").GetInt32());

        Assert.Equal("Active", await EnrollmentStatusAsync(next, course));
        Assert.Equal("Waitlisted", await EnrollmentStatusAsync(last, course));
        Assert.Single((await InboxAsync(next, "ENROLLMENT_PROMOTED")).EnumerateArray());
        Assert.Empty((await InboxAsync(last, "ENROLLMENT_PROMOTED")).EnumerateArray());
    }

    [Fact]
    public async Task Raising_the_capacity_promotes_as_many_people_as_now_fit_in_order()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "CAP-3", capacity: 1);
        var people = new List<Person>();
        foreach (var name in new[] { "Ada", "Ben", "Cleo", "Dev" }) { var p = await _world.AddLearnerAsync(t, name); people.Add(p); await EnrollAsync(p, course); }

        var changed = await ReadAsync(await t.Admin.PutAsJsonAsync($"/api/v1/tenant/courses/{course.Id}/capacity", new { capacity = 3 }));
        Assert.Equal(2, changed.GetProperty("justPromoted").GetInt32());
        Assert.Equal(new[] { "Active", "Active", "Active", "Waitlisted" }, new[] { await EnrollmentStatusAsync(people[0], course), await EnrollmentStatusAsync(people[1], course), await EnrollmentStatusAsync(people[2], course), await EnrollmentStatusAsync(people[3], course) });
        Assert.Equal(1, changed.GetProperty("waitlist").GetArrayLength());
    }

    [Fact]
    public async Task Lowering_the_capacity_never_removes_anyone_already_in()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "CAP-4", capacity: 3);
        var a = await _world.AddLearnerAsync(t, "Ada"); var b = await _world.AddLearnerAsync(t, "Ben");
        await EnrollAsync(a, course); await EnrollAsync(b, course);
        var changed = await ReadAsync(await t.Admin.PutAsJsonAsync($"/api/v1/tenant/courses/{course.Id}/capacity", new { capacity = 1 }));
        Assert.Equal(2, changed.GetProperty("active").GetInt32());
        Assert.Equal(0, changed.GetProperty("freeSeats").GetInt32());
        Assert.Equal("Active", await EnrollmentStatusAsync(b, course));
    }

    [Fact]
    public async Task Unlimited_capacity_promotes_everyone_waiting()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "CAP-5", capacity: 1);
        var a = await _world.AddLearnerAsync(t, "Ada"); var b = await _world.AddLearnerAsync(t, "Ben"); var c = await _world.AddLearnerAsync(t, "Cleo");
        await EnrollAsync(a, course); await EnrollAsync(b, course); await EnrollAsync(c, course);
        var changed = await ReadAsync(await t.Admin.PutAsJsonAsync($"/api/v1/tenant/courses/{course.Id}/capacity", new { capacity = (int?)null }));
        Assert.Equal(2, changed.GetProperty("justPromoted").GetInt32());
        Assert.Equal(JsonValueKind.Null, changed.GetProperty("capacity").ValueKind);
    }

    [Fact]
    public async Task Staff_can_promote_a_chosen_number_from_the_waitlist()
    {
        // Staff can place people on the waitlist directly even when seats are free, which is when manual promotion matters.
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "CAP-6", capacity: 5);
        var people = new List<Person>();
        foreach (var name in new[] { "Ada", "Ben", "Cleo" })
        {
            var person = await _world.AddLearnerAsync(t, name);
            people.Add(person);
            (await t.Admin.PostAsJsonAsync("/api/v1/tenant/enrollments", new { courseId = course.Id, learnerUserId = person.Id, status = "Waitlisted" })).EnsureSuccessStatusCode();
            await Task.Delay(5); // a clear joining order
        }

        var one = await ReadAsync(await t.Admin.PostAsJsonAsync($"/api/v1/tenant/courses/{course.Id}/waitlist/promote", new { count = 1 }));
        Assert.Equal(1, one.GetProperty("justPromoted").GetInt32());
        Assert.Equal("Active", await EnrollmentStatusAsync(people[0], course)); // longest waiting goes first
        Assert.Equal("Waitlisted", await EnrollmentStatusAsync(people[1], course));

        var rest = await ReadAsync(await t.Admin.PostAsync($"/api/v1/tenant/courses/{course.Id}/waitlist/promote", null));
        Assert.Equal(2, rest.GetProperty("justPromoted").GetInt32());
        Assert.Equal(0, rest.GetProperty("waitlist").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await t.Admin.PostAsJsonAsync($"/api/v1/tenant/courses/{course.Id}/waitlist/promote", new { count = 0 })).StatusCode);
    }

    [Fact]
    public async Task Promotion_never_exceeds_the_free_seats()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "CAP-6B", capacity: 1);
        var holder = await _world.AddLearnerAsync(t, "Ada");
        await EnrollAsync(holder, course);
        var waiting = await _world.AddLearnerAsync(t, "Ben");
        (await t.Admin.PostAsJsonAsync("/api/v1/tenant/enrollments", new { courseId = course.Id, learnerUserId = waiting.Id, status = "Waitlisted" })).EnsureSuccessStatusCode();

        var none = await ReadAsync(await t.Admin.PostAsJsonAsync($"/api/v1/tenant/courses/{course.Id}/waitlist/promote", new { count = 5 }));
        Assert.Equal(0, none.GetProperty("justPromoted").GetInt32());
        Assert.Equal("Waitlisted", await EnrollmentStatusAsync(waiting, course));
    }

    [Fact]
    public async Task Promotion_restarts_the_enrollment_date_so_drip_content_counts_from_getting_in()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "CAP-7", capacity: 1);
        var holder = await _world.AddLearnerAsync(t, "Ada"); var waiting = await _world.AddLearnerAsync(t, "Ben");
        await EnrollAsync(holder, course); await EnrollAsync(waiting, course);
        var waitingId = await EnrollmentIdAsync(waiting, course);
        await _world.WithDbAsync(t.Slug, async db =>
        {
            var enrollment = await db.Enrollments.SingleAsync(item => item.Id == waitingId);
            enrollment.EnrolledAtUtc = DateTimeOffset.UtcNow.AddDays(-10); // joined the waitlist long ago
            await db.SaveChangesAsync();
        });

        await Withdraw(holder, await EnrollmentIdAsync(holder, course));
        var promoted = (await ReadAsync(await waiting.Client.GetAsync("/api/v1/tenant/enrollments"))).EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == waitingId);
        Assert.True(promoted.GetProperty("enrolledAtUtc").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task Only_the_learner_or_staff_can_withdraw_and_completed_courses_cannot_be_left()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "WD-1");
        var ada = await _world.AddLearnerAsync(t, "Ada"); var ben = await _world.AddLearnerAsync(t, "Ben");
        await EnrollAsync(ada, course); await EnrollAsync(ben, course);
        var adaId = await EnrollmentIdAsync(ada, course);

        Assert.Equal(HttpStatusCode.NotFound, (await Withdraw(ben, adaId)).StatusCode); // someone else's enrollment
        Assert.Equal(HttpStatusCode.OK, (await t.Admin.PostAsync($"/api/v1/tenant/enrollments/{adaId}/withdraw", null)).StatusCode);
        Assert.Equal("Withdrawn", await EnrollmentStatusAsync(ada, course));
        Assert.Equal(HttpStatusCode.OK, (await Withdraw(ada, adaId)).StatusCode); // repeating is harmless

        await CompleteCourseAsync(ben, course);
        Assert.Equal(HttpStatusCode.Conflict, (await Withdraw(ben, await EnrollmentIdAsync(ben, course))).StatusCode);
    }

    [Fact]
    public async Task Leaving_the_waitlist_does_not_free_a_seat_and_a_withdrawn_learner_can_rejoin()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "WD-2", capacity: 1);
        var holder = await _world.AddLearnerAsync(t, "Ada"); var waiting = await _world.AddLearnerAsync(t, "Ben"); var other = await _world.AddLearnerAsync(t, "Cleo");
        await EnrollAsync(holder, course); await EnrollAsync(waiting, course); await EnrollAsync(other, course);

        var left = await ReadAsync(await Withdraw(waiting, await EnrollmentIdAsync(waiting, course)));
        Assert.Equal(0, left.GetProperty("promoted").GetInt32());
        Assert.Equal("Active", await EnrollmentStatusAsync(holder, course));

        await Withdraw(holder, await EnrollmentIdAsync(holder, course));
        Assert.Equal("Active", await EnrollmentStatusAsync(other, course)); // next in line, not the one who left
        Assert.Equal(HttpStatusCode.Accepted, (await EnrollAsync(waiting, course)).StatusCode); // rejoining goes to the back of a full course
        Assert.Equal("Waitlisted", await EnrollmentStatusAsync(waiting, course));
    }

    [Fact]
    public async Task Capacity_and_promotion_controls_are_for_staff_and_validated()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "CAP-8", capacity: 2);
        var learner = await _world.AddLearnerAsync(t, "Ada");
        Assert.Equal(HttpStatusCode.Forbidden, (await learner.Client.PutAsJsonAsync($"/api/v1/tenant/courses/{course.Id}/capacity", new { capacity = 9 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await learner.Client.GetAsync($"/api/v1/tenant/courses/{course.Id}/enrollment-summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await learner.Client.PostAsJsonAsync($"/api/v1/tenant/courses/{course.Id}/waitlist/promote", new { count = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await t.Admin.PutAsJsonAsync($"/api/v1/tenant/courses/{course.Id}/capacity", new { capacity = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await t.Admin.PutAsJsonAsync($"/api/v1/tenant/courses/{course.Id}/capacity", new { capacity = 2_000_000 })).StatusCode);
    }

    // ---------- prerequisites ----------
    [Fact]
    public async Task Prerequisite_changes_are_validated_and_loops_are_refused()
    {
        var t = await _world.NewTenantAsync();
        var a = await _world.NewCourseAsync(t, "PRE-A"); var b = await _world.NewCourseAsync(t, "PRE-B"); var c = await _world.NewCourseAsync(t, "PRE-C");
        Task<HttpResponseMessage> Set(CourseInfo course, params Guid[] required) => t.Admin.PutAsJsonAsync($"/api/v1/tenant/courses/{course.Id}/prerequisites", new { courseIds = required });

        Assert.Equal(HttpStatusCode.BadRequest, (await Set(a, a.Id)).StatusCode);            // itself
        Assert.Equal(HttpStatusCode.BadRequest, (await Set(a, Guid.NewGuid())).StatusCode);  // unknown
        Assert.Equal(HttpStatusCode.OK, (await Set(b, a.Id)).StatusCode);                    // B needs A
        Assert.Equal(HttpStatusCode.OK, (await Set(c, b.Id)).StatusCode);                    // C needs B
        var loop = await Set(a, c.Id);                                                       // A needs C would close the loop
        Assert.Equal(HttpStatusCode.BadRequest, loop.StatusCode);
        Assert.Contains("loop", (await ReadAsync(loop)).GetProperty("message").GetString());

        var rules = await ReadAsync(await t.Admin.GetAsync($"/api/v1/tenant/courses/{c.Id}/access-rules"));
        Assert.Equal("PRE-B", rules.GetProperty("prerequisites")[0].GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await Set(c)).StatusCode); // clearing the list is allowed
        Assert.Equal(0, (await ReadAsync(await t.Admin.GetAsync($"/api/v1/tenant/courses/{c.Id}/access-rules"))).GetProperty("prerequisites").GetArrayLength());
    }

    [Fact]
    public async Task Enrolling_is_blocked_until_the_prerequisite_course_is_completed()
    {
        var t = await _world.NewTenantAsync();
        var basics = await _world.NewCourseAsync(t, "BAS-1");
        var advanced = await _world.NewCourseAsync(t, "ADV-1");
        await t.Admin.PutAsJsonAsync($"/api/v1/tenant/courses/{advanced.Id}/prerequisites", new { courseIds = new[] { basics.Id } });
        var learner = await _world.AddLearnerAsync(t, "Ada");

        var blocked = await EnrollAsync(learner, advanced);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        var body = await ReadAsync(blocked);
        Assert.Contains("Course BAS-1", body.GetProperty("message").GetString());
        Assert.Equal("Course BAS-1", body.GetProperty("missingPrerequisites")[0].GetString());

        // Merely being enrolled in the prerequisite is not enough; it must be completed.
        await EnrollAsync(learner, basics);
        Assert.Equal(HttpStatusCode.Conflict, (await EnrollAsync(learner, advanced)).StatusCode);
        await CompleteCourseAsync(learner, basics);
        Assert.Equal(HttpStatusCode.Created, (await EnrollAsync(learner, advanced)).StatusCode);
    }

    [Fact]
    public async Task Staff_enrollment_honours_prerequisites_unless_explicitly_overridden()
    {
        var t = await _world.NewTenantAsync();
        var basics = await _world.NewCourseAsync(t, "BAS-2"); var advanced = await _world.NewCourseAsync(t, "ADV-2");
        await t.Admin.PutAsJsonAsync($"/api/v1/tenant/courses/{advanced.Id}/prerequisites", new { courseIds = new[] { basics.Id } });
        var learner = await _world.AddLearnerAsync(t, "Ada");

        var blocked = await t.Admin.PostAsJsonAsync("/api/v1/tenant/enrollments", new { courseId = advanced.Id, learnerUserId = learner.Id });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("overridePrerequisites", (await ReadAsync(blocked)).GetProperty("message").GetString());
        Assert.Equal(HttpStatusCode.Created, (await t.Admin.PostAsJsonAsync("/api/v1/tenant/enrollments", new { courseId = advanced.Id, learnerUserId = learner.Id, overridePrerequisites = true })).StatusCode);
    }

    [Fact]
    public async Task Prerequisite_rules_are_for_staff_only()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "PRE-X");
        var learner = await _world.AddLearnerAsync(t, "Ada");
        Assert.Equal(HttpStatusCode.Forbidden, (await learner.Client.GetAsync($"/api/v1/tenant/courses/{course.Id}/access-rules")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await learner.Client.PutAsJsonAsync($"/api/v1/tenant/courses/{course.Id}/prerequisites", new { courseIds = Array.Empty<Guid>() })).StatusCode);
    }

    // ---------- drip and "finish this first" ----------
    private async Task<(Tenant Tenant, CourseInfo Course, Person Learner)> DripSetupAsync()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, $"DRP-{Guid.NewGuid().ToString("N")[..4]}", modules: 3, lessonsPerModule: 2);
        var learner = await _world.AddLearnerAsync(t, "Ada");
        await EnrollAsync(learner, course);
        return (t, course, learner);
    }

    private static Task<HttpResponseMessage> SetAccess(Tenant t, CourseInfo course, int moduleIndex, object body) => t.Admin.PutAsJsonAsync($"/api/v1/tenant/courses/{course.Id}/modules/{course.ModuleIds[moduleIndex]}/access", body);

    private static async Task<JsonElement> PlayerAsync(Person learner, CourseInfo course) => await ReadAsync(await learner.Client.GetAsync($"/api/v1/tenant/courses/{course.Id}/learning"));

    private static JsonElement ModuleOf(JsonElement player, int index) => player.GetProperty("modules")[index];

    [Fact]
    public async Task A_module_that_opens_days_after_enrollment_is_locked_with_its_content_withheld()
    {
        var (t, course, learner) = await DripSetupAsync();
        Assert.Equal(HttpStatusCode.OK, (await SetAccess(t, course, 1, new { releaseAfterDays = 7 })).StatusCode);

        var player = await PlayerAsync(learner, course);
        Assert.False(ModuleOf(player, 0).GetProperty("locked").GetBoolean());
        var closed = ModuleOf(player, 1);
        Assert.True(closed.GetProperty("locked").GetBoolean());
        Assert.StartsWith("Opens on", closed.GetProperty("lockReason").GetString());
        Assert.NotEqual(JsonValueKind.Null, closed.GetProperty("unlocksAtUtc").ValueKind);
        Assert.Equal(2, closed.GetProperty("lessons").GetArrayLength());                                   // titles are listed ...
        Assert.Equal(JsonValueKind.Null, closed.GetProperty("lessons")[0].GetProperty("contentHtml").ValueKind); // ... content is not
        Assert.Equal("Body 1.1", ModuleOf(player, 0).GetProperty("lessons")[0].GetProperty("contentHtml").GetString());
    }

    [Fact]
    public async Task A_locked_module_cannot_be_worked_on_or_read_even_by_someone_who_knows_the_address()
    {
        var (t, course, learner) = await DripSetupAsync();
        await SetAccess(t, course, 1, new { releaseAfterDays = 7 });
        var lesson = course.LessonIds[1][0];

        var progress = await learner.Client.PostAsJsonAsync($"/api/v1/tenant/courses/{course.Id}/learning/lessons/{lesson}/progress", new { status = "Completed" });
        Assert.Equal(HttpStatusCode.Conflict, progress.StatusCode);
        Assert.True((await ReadAsync(progress)).GetProperty("locked").GetBoolean());

        Assert.Equal(HttpStatusCode.Conflict, (await learner.Client.GetAsync($"/api/v1/tenant/courses/{course.Id}/lessons/{lesson}/blocks")).StatusCode);
        // Staff are never locked out of their own course.
        Assert.Equal(HttpStatusCode.OK, (await t.Admin.GetAsync($"/api/v1/tenant/courses/{course.Id}/lessons/{lesson}/blocks")).StatusCode);
    }

    [Fact]
    public async Task A_module_opens_once_enough_days_have_passed()
    {
        var (t, course, learner) = await DripSetupAsync();
        await SetAccess(t, course, 1, new { releaseAfterDays = 3 });
        Assert.True(ModuleOf(await PlayerAsync(learner, course), 1).GetProperty("locked").GetBoolean());

        var enrollmentId = await EnrollmentIdAsync(learner, course);
        await _world.WithDbAsync(t.Slug, async db =>
        {
            var enrollment = await db.Enrollments.SingleAsync(item => item.Id == enrollmentId);
            enrollment.EnrolledAtUtc = DateTimeOffset.UtcNow.AddDays(-4);
            await db.SaveChangesAsync();
        });
        Assert.False(ModuleOf(await PlayerAsync(learner, course), 1).GetProperty("locked").GetBoolean());
        await CompleteLessonAsync(learner, course, course.LessonIds[1][0]);
    }

    [Fact]
    public async Task A_calendar_release_date_locks_until_it_arrives()
    {
        var (t, course, learner) = await DripSetupAsync();
        await SetAccess(t, course, 1, new { releaseOnUtc = DateTimeOffset.UtcNow.AddDays(2) });
        Assert.True(ModuleOf(await PlayerAsync(learner, course), 1).GetProperty("locked").GetBoolean());
        await SetAccess(t, course, 1, new { releaseOnUtc = DateTimeOffset.UtcNow.AddDays(-1) });
        Assert.False(ModuleOf(await PlayerAsync(learner, course), 1).GetProperty("locked").GetBoolean());
    }

    [Fact]
    public async Task When_both_a_delay_and_a_date_are_set_the_later_one_wins()
    {
        var (t, course, learner) = await DripSetupAsync();
        await SetAccess(t, course, 1, new { releaseAfterDays = 1, releaseOnUtc = DateTimeOffset.UtcNow.AddDays(10) });
        var unlocks = ModuleOf(await PlayerAsync(learner, course), 1).GetProperty("unlocksAtUtc").GetDateTimeOffset();
        Assert.True(unlocks > DateTimeOffset.UtcNow.AddDays(9));
    }

    [Fact]
    public async Task A_module_can_require_the_previous_one_to_be_finished_first()
    {
        var (t, course, learner) = await DripSetupAsync();
        Assert.Equal(HttpStatusCode.OK, (await SetAccess(t, course, 1, new { requiresModuleId = course.ModuleIds[0] })).StatusCode);

        var locked = ModuleOf(await PlayerAsync(learner, course), 1);
        Assert.True(locked.GetProperty("locked").GetBoolean());
        Assert.Equal("Finish “Module 1” first.", locked.GetProperty("lockReason").GetString());
        Assert.Equal(JsonValueKind.Null, locked.GetProperty("unlocksAtUtc").ValueKind);

        await CompleteLessonAsync(learner, course, course.LessonIds[0][0]);
        Assert.True(ModuleOf(await PlayerAsync(learner, course), 1).GetProperty("locked").GetBoolean()); // one of two lessons is not enough
        await CompleteLessonAsync(learner, course, course.LessonIds[0][1]);
        Assert.False(ModuleOf(await PlayerAsync(learner, course), 1).GetProperty("locked").GetBoolean());
    }

    [Fact]
    public async Task Modules_can_be_chained_and_a_rule_can_be_removed()
    {
        var (t, course, learner) = await DripSetupAsync();
        await SetAccess(t, course, 1, new { requiresModuleId = course.ModuleIds[0] });
        await SetAccess(t, course, 2, new { requiresModuleId = course.ModuleIds[1] });
        var player = await PlayerAsync(learner, course);
        Assert.True(ModuleOf(player, 1).GetProperty("locked").GetBoolean());
        Assert.True(ModuleOf(player, 2).GetProperty("locked").GetBoolean());

        // An empty rule removes it.
        var cleared = await ReadAsync(await SetAccess(t, course, 1, new { }));
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("modules")[1].GetProperty("requiresModuleId").ValueKind);
        Assert.False(ModuleOf(await PlayerAsync(learner, course), 1).GetProperty("locked").GetBoolean());
    }

    [Fact]
    public async Task Access_rules_are_validated()
    {
        var (t, course, _) = await DripSetupAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await SetAccess(t, course, 1, new { releaseAfterDays = -1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetAccess(t, course, 1, new { releaseAfterDays = 5000 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetAccess(t, course, 1, new { requiresModuleId = course.ModuleIds[1] })).StatusCode); // itself
        Assert.Equal(HttpStatusCode.BadRequest, (await SetAccess(t, course, 0, new { requiresModuleId = course.ModuleIds[2] })).StatusCode); // a later module
        Assert.Equal(HttpStatusCode.BadRequest, (await SetAccess(t, course, 1, new { requiresModuleId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t.Admin.PutAsJsonAsync($"/api/v1/tenant/courses/{course.Id}/modules/{Guid.NewGuid()}/access", new { releaseAfterDays = 1 })).StatusCode);

        var other = await _world.NewCourseAsync(t, "DRP-OTHER", modules: 1);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetAccess(t, course, 1, new { requiresModuleId = other.ModuleIds[0] })).StatusCode); // another course's module
    }

    [Fact]
    public async Task Access_rules_are_for_staff_and_isolated_between_tenants()
    {
        var (t, course, learner) = await DripSetupAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await learner.Client.PutAsJsonAsync($"/api/v1/tenant/courses/{course.Id}/modules/{course.ModuleIds[1]}/access", new { releaseAfterDays = 1 })).StatusCode);
        var other = await _world.NewTenantAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.PutAsJsonAsync($"/api/v1/tenant/courses/{course.Id}/modules/{course.ModuleIds[1]}/access", new { releaseAfterDays = 1 })).StatusCode);
    }

    [Fact]
    public async Task Without_any_rules_every_module_is_open()
    {
        var (_, course, learner) = await DripSetupAsync();
        var player = await PlayerAsync(learner, course);
        Assert.All(player.GetProperty("modules").EnumerateArray(), module => Assert.False(module.GetProperty("locked").GetBoolean()));
    }
}
