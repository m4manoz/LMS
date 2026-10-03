using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lms.Api.Domain.Learning;
using Microsoft.EntityFrameworkCore;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

/// <summary>Cohorts (groups of learners) and course invitations.</summary>
public sealed class CohortInvitationTests : IClassFixture<LmsApiFactory>
{
    private readonly LmsApiFactory _factory;
    private readonly TestWorld _world;

    public CohortInvitationTests(LmsApiFactory factory) { _factory = factory; _world = new TestWorld(factory); }

    // ---------- helpers ----------
    private static async Task<Guid> NewCohortAsync(Tenant t, string name, params Person[] members)
    {
        var created = await ReadAsync(await t.Admin.PostAsJsonAsync("/api/v1/tenant/cohorts", new { name }));
        var id = created.GetProperty("id").GetGuid();
        if (members.Length > 0) (await t.Admin.PostAsJsonAsync($"/api/v1/tenant/cohorts/{id}/members", new { userIds = members.Select(m => m.Id).ToArray() })).EnsureSuccessStatusCode();
        return id;
    }

    private static Task<HttpResponseMessage> Invite(Tenant t, CourseInfo course, string email, object? extra = null)
        => t.Admin.PostAsJsonAsync("/api/v1/tenant/invitations", extra ?? new { courseId = course.Id, email });

    private static async Task<(Guid Id, string Token)> InviteOkAsync(Tenant t, CourseInfo course, string email)
    {
        var response = await Invite(t, course, email);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadAsync(response);
        return (body.GetProperty("id").GetGuid(), body.GetProperty("token").GetString()!);
    }

    private static async Task<JsonElement> MineAsync(Person person) => await ReadAsync(await person.Client.GetAsync("/api/v1/tenant/invitations/mine"));

    // ---------- cohorts ----------
    [Fact]
    public async Task Cohorts_can_be_created_updated_listed_and_validated()
    {
        var t = await _world.NewTenantAsync();
        var ada = await _world.AddLearnerAsync(t, "Ada");
        var id = await NewCohortAsync(t, "Batch 2026", ada);

        Assert.Equal(HttpStatusCode.BadRequest, (await t.Admin.PostAsJsonAsync("/api/v1/tenant/cohorts", new { name = "x" })).StatusCode);          // too short
        Assert.Equal(HttpStatusCode.BadRequest, (await t.Admin.PostAsJsonAsync("/api/v1/tenant/cohorts", new { name = "batch 2026" })).StatusCode);  // duplicate, ignoring case
        Assert.Equal(HttpStatusCode.BadRequest, (await t.Admin.PostAsJsonAsync("/api/v1/tenant/cohorts", new { name = "Backwards", startDateAd = "2026-05-01", endDateAd = "2026-04-01" })).StatusCode);

        var updated = await ReadAsync(await t.Admin.PutAsJsonAsync($"/api/v1/tenant/cohorts/{id}", new { name = "Batch 2026 (evening)", description = "Evening class", startDateAd = "2026-04-01", endDateAd = "2026-09-01" }));
        Assert.Equal("Evening class", updated.GetProperty("description").GetString());

        var list = await ReadAsync(await t.Admin.GetAsync("/api/v1/tenant/cohorts"));
        Assert.Equal("Batch 2026 (evening)", list[0].GetProperty("name").GetString());
        Assert.Equal(1, list[0].GetProperty("memberCount").GetInt32());
    }

    [Fact]
    public async Task Members_are_deduplicated_and_only_active_people_of_this_organization_can_join()
    {
        var t = await _world.NewTenantAsync();
        var other = await _world.NewTenantAsync();
        var ada = await _world.AddLearnerAsync(t, "Ada");
        var stranger = await _world.AddLearnerAsync(other, "Zed");
        var id = await NewCohortAsync(t, "Mixed", ada);

        var added = await ReadAsync(await t.Admin.PostAsJsonAsync($"/api/v1/tenant/cohorts/{id}/members", new { userIds = new[] { ada.Id, stranger.Id, Guid.NewGuid() } }));
        Assert.Equal(1, added.GetProperty("members").GetArrayLength()); // Ada once; the others do not belong to this organization
        Assert.Equal(2, added.GetProperty("skipped").GetInt32());

        Assert.Equal(HttpStatusCode.BadRequest, (await t.Admin.PostAsJsonAsync($"/api/v1/tenant/cohorts/{id}/members", new { userIds = Array.Empty<Guid>() })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await t.Admin.DeleteAsync($"/api/v1/tenant/cohorts/{id}/members/{ada.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t.Admin.DeleteAsync($"/api/v1/tenant/cohorts/{id}/members/{ada.Id}")).StatusCode);
    }

    [Fact]
    public async Task Cohorts_are_for_staff_only_and_isolated_between_organizations()
    {
        var t = await _world.NewTenantAsync();
        var ada = await _world.AddLearnerAsync(t, "Ada");
        var id = await NewCohortAsync(t, "Private group", ada);
        Assert.Equal(HttpStatusCode.Forbidden, (await ada.Client.GetAsync("/api/v1/tenant/cohorts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ada.Client.GetAsync($"/api/v1/tenant/cohorts/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ada.Client.PostAsJsonAsync("/api/v1/tenant/cohorts", new { name = "Mine" })).StatusCode);

        var other = await _world.NewTenantAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.GetAsync($"/api/v1/tenant/cohorts/{id}")).StatusCode);
        Assert.Equal(0, (await ReadAsync(await other.Admin.GetAsync("/api/v1/tenant/cohorts"))).GetArrayLength());
    }

    [Fact]
    public async Task Enrolling_a_cohort_reports_what_happened_to_each_person()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "COH-1", capacity: 2);
        var ada = await _world.AddLearnerAsync(t, "Ada"); var ben = await _world.AddLearnerAsync(t, "Ben"); var cleo = await _world.AddLearnerAsync(t, "Cleo");
        await EnrollAsync(ada, course); // already in
        var cohort = await NewCohortAsync(t, "Class A", ada, ben, cleo);

        var result = await ReadAsync(await t.Admin.PostAsJsonAsync($"/api/v1/tenant/cohorts/{cohort}/enroll", new { courseId = course.Id }));
        var outcomes = result.GetProperty("results").EnumerateArray().ToDictionary(r => r.GetProperty("name").GetString()!, r => r.GetProperty("outcome").GetString());
        Assert.Equal("AlreadyEnrolled", outcomes["Ada"]);
        Assert.Equal("Enrolled", outcomes["Ben"]);
        Assert.Equal("Waitlisted", outcomes["Cleo"]); // two seats: Ada and Ben took them
        Assert.Equal(1, result.GetProperty("succeeded").GetInt32());
        Assert.Equal(1, result.GetProperty("waitlisted").GetInt32());
        Assert.Equal(1, result.GetProperty("alreadyIn").GetInt32());
        Assert.Equal("Active", await EnrollmentStatusAsync(ben, course));

        // Running it again changes nothing.
        var again = await ReadAsync(await t.Admin.PostAsJsonAsync($"/api/v1/tenant/cohorts/{cohort}/enroll", new { courseId = course.Id }));
        Assert.Equal(3, again.GetProperty("alreadyIn").GetInt32());
    }

    [Fact]
    public async Task Cohort_enrollment_respects_prerequisites_unless_overridden()
    {
        var t = await _world.NewTenantAsync();
        var basics = await _world.NewCourseAsync(t, "COH-B"); var advanced = await _world.NewCourseAsync(t, "COH-A");
        await t.Admin.PutAsJsonAsync($"/api/v1/tenant/courses/{advanced.Id}/prerequisites", new { courseIds = new[] { basics.Id } });
        var ada = await _world.AddLearnerAsync(t, "Ada"); var ben = await _world.AddLearnerAsync(t, "Ben");
        await EnrollAsync(ada, basics); await CompleteCourseAsync(ada, basics);
        var cohort = await NewCohortAsync(t, "Mixed readiness", ada, ben);

        var result = await ReadAsync(await t.Admin.PostAsJsonAsync($"/api/v1/tenant/cohorts/{cohort}/enroll", new { courseId = advanced.Id }));
        var byName = result.GetProperty("results").EnumerateArray().ToDictionary(r => r.GetProperty("name").GetString()!);
        Assert.Equal("Enrolled", byName["Ada"].GetProperty("outcome").GetString());
        Assert.Equal("MissingPrerequisites", byName["Ben"].GetProperty("outcome").GetString());
        Assert.Equal("Course COH-B", byName["Ben"].GetProperty("missingPrerequisites")[0].GetString());
        Assert.Equal(1, result.GetProperty("blocked").GetInt32());

        var forced = await ReadAsync(await t.Admin.PostAsJsonAsync($"/api/v1/tenant/cohorts/{cohort}/enroll", new { courseId = advanced.Id, overridePrerequisites = true }));
        Assert.Equal(1, forced.GetProperty("succeeded").GetInt32());
        Assert.Equal("Active", await EnrollmentStatusAsync(ben, advanced));
    }

    [Fact]
    public async Task Enrolling_needs_a_published_course_and_a_cohort_with_members()
    {
        var t = await _world.NewTenantAsync();
        var draft = await _world.NewCourseAsync(t, "COH-D", publish: false);
        var live = await _world.NewCourseAsync(t, "COH-L");
        var ada = await _world.AddLearnerAsync(t, "Ada");
        var empty = await NewCohortAsync(t, "Empty");
        var full = await NewCohortAsync(t, "Full", ada);

        Assert.Equal(HttpStatusCode.BadRequest, (await t.Admin.PostAsJsonAsync($"/api/v1/tenant/cohorts/{empty}/enroll", new { courseId = live.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t.Admin.PostAsJsonAsync($"/api/v1/tenant/cohorts/{full}/enroll", new { courseId = draft.Id })).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_cohort_keeps_the_enrollments_made_from_it()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "COH-2");
        var ada = await _world.AddLearnerAsync(t, "Ada");
        var cohort = await NewCohortAsync(t, "Temporary", ada);
        await t.Admin.PostAsJsonAsync($"/api/v1/tenant/cohorts/{cohort}/enroll", new { courseId = course.Id });
        Assert.Equal(HttpStatusCode.NoContent, (await t.Admin.DeleteAsync($"/api/v1/tenant/cohorts/{cohort}")).StatusCode);
        Assert.Equal("Active", await EnrollmentStatusAsync(ada, course));
        Assert.Equal(HttpStatusCode.NotFound, (await t.Admin.GetAsync($"/api/v1/tenant/cohorts/{cohort}")).StatusCode);
    }

    // ---------- invitations: sending ----------
    [Fact]
    public async Task An_invitation_returns_its_one_time_token_and_the_list_never_shows_it_again()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "INV-1");
        var response = await Invite(t, course, "New.Person@Example.org");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.True(body.GetProperty("token").GetString()!.Length >= 40);

        var list = await ReadAsync(await t.Admin.GetAsync("/api/v1/tenant/invitations"));
        Assert.Equal("new.person@example.org", list[0].GetProperty("email").GetString()); // stored lower-case
        Assert.Equal("Pending", list[0].GetProperty("status").GetString());
        Assert.DoesNotContain("token", list.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not-an-email", null)]
    [InlineData("a@b.co", 0)]
    [InlineData("a@b.co", 91)]
    public async Task Invalid_invitations_are_rejected(string email, int? days)
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "INV-2");
        Assert.Equal(HttpStatusCode.BadRequest, (await Invite(t, course, email, new { courseId = course.Id, email, expiresInDays = days })).StatusCode);
    }

    [Fact]
    public async Task Invitations_need_a_published_course_and_cannot_target_someone_already_in()
    {
        var t = await _world.NewTenantAsync();
        var draft = await _world.NewCourseAsync(t, "INV-D", publish: false);
        var live = await _world.NewCourseAsync(t, "INV-L");
        var ada = await _world.AddLearnerAsync(t, "Ada");
        Assert.Equal(HttpStatusCode.NotFound, (await Invite(t, draft, ada.Email)).StatusCode);

        await EnrollAsync(ada, live);
        Assert.Equal(HttpStatusCode.Conflict, (await Invite(t, live, ada.Email)).StatusCode);
    }

    [Fact]
    public async Task Sending_again_replaces_the_earlier_invitation_and_its_token()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "INV-3");
        var ada = await _world.AddLearnerAsync(t, "Ada");
        var first = await InviteOkAsync(t, course, ada.Email);
        var second = await InviteOkAsync(t, course, ada.Email);

        var statuses = (await ReadAsync(await t.Admin.GetAsync("/api/v1/tenant/invitations"))).EnumerateArray().Select(i => i.GetProperty("status").GetString()).ToList();
        Assert.Equal(1, statuses.Count(status => status == "Pending"));
        Assert.Equal(1, statuses.Count(status => status == "Revoked"));

        // The first invitation's token stopped working the moment a new one was sent.
        Assert.Equal(HttpStatusCode.Conflict, (await ada.Client.PostAsJsonAsync("/api/v1/tenant/invitations/accept", new { token = first.Token })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ada.Client.PostAsJsonAsync("/api/v1/tenant/invitations/accept", new { token = second.Token })).StatusCode);
    }

    [Fact]
    public async Task Staff_can_revoke_pending_invitations_only()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "INV-4");
        var ada = await _world.AddLearnerAsync(t, "Ada");
        var invite = await InviteOkAsync(t, course, ada.Email);
        Assert.Equal(HttpStatusCode.NoContent, (await t.Admin.PostAsync($"/api/v1/tenant/invitations/{invite.Id}/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await t.Admin.PostAsync($"/api/v1/tenant/invitations/{invite.Id}/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ada.Client.PostAsync($"/api/v1/tenant/invitations/{invite.Id}/accept", null)).StatusCode); // revoked
        Assert.Empty((await MineAsync(ada)).EnumerateArray());
    }

    [Fact]
    public async Task Sending_and_managing_invitations_is_for_staff_only()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "INV-5");
        var ada = await _world.AddLearnerAsync(t, "Ada");
        Assert.Equal(HttpStatusCode.Forbidden, (await ada.Client.PostAsJsonAsync("/api/v1/tenant/invitations", new { courseId = course.Id, email = "x@y.co" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ada.Client.GetAsync("/api/v1/tenant/invitations")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ada.Client.PostAsync($"/api/v1/tenant/invitations/{Guid.NewGuid()}/revoke", null)).StatusCode);
    }

    // ---------- invitations: receiving ----------
    [Fact]
    public async Task An_invited_person_sees_only_their_own_invitations_and_is_told_in_the_app()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "INV-6");
        var ada = await _world.AddLearnerAsync(t, "Ada"); var ben = await _world.AddLearnerAsync(t, "Ben");
        await InviteOkAsync(t, course, ada.Email);

        var mine = await MineAsync(ada);
        Assert.Equal(1, mine.GetArrayLength());
        Assert.Equal("Course INV-6", mine[0].GetProperty("courseTitle").GetString());
        Assert.Empty((await MineAsync(ben)).EnumerateArray());

        var notice = Assert.Single((await InboxAsync(ada, "COURSE_INVITATION")).EnumerateArray());
        Assert.Contains("Course INV-6", notice.GetProperty("subject").GetString());
        Assert.Empty((await InboxAsync(ben, "COURSE_INVITATION")).EnumerateArray());
    }

    [Fact]
    public async Task The_token_is_never_put_in_a_notification()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "INV-7");
        var ada = await _world.AddLearnerAsync(t, "Ada");
        var invite = await InviteOkAsync(t, course, ada.Email);
        var inbox = await InboxAsync(ada, "COURSE_INVITATION");
        Assert.DoesNotContain(invite.Token, inbox.GetRawText());
    }

    [Fact]
    public async Task Accepting_by_id_enrolls_the_person_and_closes_the_invitation()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "INV-8");
        var ada = await _world.AddLearnerAsync(t, "Ada");
        var invite = await InviteOkAsync(t, course, ada.Email);

        var accepted = await ReadAsync(await ada.Client.PostAsync($"/api/v1/tenant/invitations/{invite.Id}/accept", null));
        Assert.Equal("Enrolled", accepted.GetProperty("outcome").GetString());
        Assert.Equal("Active", await EnrollmentStatusAsync(ada, course));
        var enrollment = (await ReadAsync(await ada.Client.GetAsync("/api/v1/tenant/enrollments")))[0];
        Assert.Equal("Invitation", enrollment.GetProperty("source").GetString());

        Assert.Equal(HttpStatusCode.Conflict, (await ada.Client.PostAsync($"/api/v1/tenant/invitations/{invite.Id}/accept", null)).StatusCode); // only once
        Assert.Empty((await MineAsync(ada)).EnumerateArray());
        Assert.Equal("Accepted", (await ReadAsync(await t.Admin.GetAsync("/api/v1/tenant/invitations")))[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Accepting_with_the_token_works_for_the_addressee_and_nobody_else()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "INV-9");
        var ada = await _world.AddLearnerAsync(t, "Ada"); var ben = await _world.AddLearnerAsync(t, "Ben");
        var invite = await InviteOkAsync(t, course, ada.Email);

        // Holding Ada's token is not enough: you must be signed in as Ada.
        Assert.Equal(HttpStatusCode.NotFound, (await ben.Client.PostAsJsonAsync("/api/v1/tenant/invitations/accept", new { token = invite.Token })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ben.Client.PostAsync($"/api/v1/tenant/invitations/{invite.Id}/accept", null)).StatusCode);
        Assert.Equal("None", await EnrollmentStatusAsync(ben, course));

        Assert.Equal(HttpStatusCode.NotFound, (await ada.Client.PostAsJsonAsync("/api/v1/tenant/invitations/accept", new { token = "made-up-token" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ada.Client.PostAsJsonAsync("/api/v1/tenant/invitations/accept", new { token = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ada.Client.PostAsJsonAsync("/api/v1/tenant/invitations/accept", new { token = invite.Token })).StatusCode);
    }

    [Fact]
    public async Task Declining_closes_an_invitation_for_good()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "INV-10");
        var ada = await _world.AddLearnerAsync(t, "Ada"); var ben = await _world.AddLearnerAsync(t, "Ben");
        var invite = await InviteOkAsync(t, course, ada.Email);

        Assert.Equal(HttpStatusCode.NotFound, (await ben.Client.PostAsync($"/api/v1/tenant/invitations/{invite.Id}/decline", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ada.Client.PostAsync($"/api/v1/tenant/invitations/{invite.Id}/decline", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ada.Client.PostAsync($"/api/v1/tenant/invitations/{invite.Id}/decline", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ada.Client.PostAsync($"/api/v1/tenant/invitations/{invite.Id}/accept", null)).StatusCode);
        Assert.Equal("Declined", (await ReadAsync(await t.Admin.GetAsync("/api/v1/tenant/invitations")))[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task An_expired_invitation_cannot_be_accepted_and_reads_as_expired()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "INV-11");
        var ada = await _world.AddLearnerAsync(t, "Ada");
        var invite = await InviteOkAsync(t, course, ada.Email);
        await _world.WithDbAsync(t.Slug, async db =>
        {
            var row = await db.CourseInvitations.SingleAsync(item => item.Id == invite.Id);
            row.ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        });

        var attempt = await ada.Client.PostAsync($"/api/v1/tenant/invitations/{invite.Id}/accept", null);
        Assert.Equal(HttpStatusCode.Conflict, attempt.StatusCode);
        Assert.Contains("expired", (await ReadAsync(attempt)).GetProperty("message").GetString());
        Assert.Empty((await MineAsync(ada)).EnumerateArray());
        Assert.Equal("Expired", (await ReadAsync(await t.Admin.GetAsync("/api/v1/tenant/invitations")))[0].GetProperty("status").GetString());
        Assert.Single((await ReadAsync(await t.Admin.GetAsync("/api/v1/tenant/invitations?status=expired"))).EnumerateArray());
    }

    [Fact]
    public async Task Accepting_for_a_full_course_puts_the_person_on_the_waitlist()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "INV-12", capacity: 1);
        var holder = await _world.AddLearnerAsync(t, "Ada"); var invited = await _world.AddLearnerAsync(t, "Ben");
        await EnrollAsync(holder, course);
        var invite = await InviteOkAsync(t, course, invited.Email);

        var accepted = await ReadAsync(await invited.Client.PostAsync($"/api/v1/tenant/invitations/{invite.Id}/accept", null));
        Assert.Equal("Waitlisted", accepted.GetProperty("outcome").GetString());
        Assert.Equal("Waitlisted", await EnrollmentStatusAsync(invited, course));
    }

    [Fact]
    public async Task A_missing_prerequisite_blocks_acceptance_but_leaves_the_invitation_open()
    {
        var t = await _world.NewTenantAsync();
        var basics = await _world.NewCourseAsync(t, "INV-B"); var advanced = await _world.NewCourseAsync(t, "INV-A");
        await t.Admin.PutAsJsonAsync($"/api/v1/tenant/courses/{advanced.Id}/prerequisites", new { courseIds = new[] { basics.Id } });
        var ada = await _world.AddLearnerAsync(t, "Ada");
        var invite = await InviteOkAsync(t, advanced, ada.Email);

        var blocked = await ada.Client.PostAsync($"/api/v1/tenant/invitations/{invite.Id}/accept", null);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("Course INV-B", (await ReadAsync(blocked)).GetProperty("missingPrerequisites")[0].GetString());
        Assert.Single((await MineAsync(ada)).EnumerateArray()); // still pending

        await EnrollAsync(ada, basics); await CompleteCourseAsync(ada, basics);
        Assert.Equal(HttpStatusCode.OK, (await ada.Client.PostAsync($"/api/v1/tenant/invitations/{invite.Id}/accept", null)).StatusCode);
    }

    [Fact]
    public async Task Someone_already_enrolled_can_still_accept_which_just_closes_the_invitation()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "INV-13");
        var ada = await _world.AddLearnerAsync(t, "Ada");
        var invite = await InviteOkAsync(t, course, ada.Email);
        await EnrollAsync(ada, course); // enrolled on their own after the invitation went out
        var accepted = await ReadAsync(await ada.Client.PostAsync($"/api/v1/tenant/invitations/{invite.Id}/accept", null));
        Assert.Equal("AlreadyEnrolled", accepted.GetProperty("outcome").GetString());
        Assert.Empty((await MineAsync(ada)).EnumerateArray());
    }

    [Fact]
    public async Task An_invitation_to_a_course_that_was_archived_cannot_be_accepted()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "INV-14");
        var ada = await _world.AddLearnerAsync(t, "Ada");
        var invite = await InviteOkAsync(t, course, ada.Email);
        (await t.Admin.PostAsync($"/api/v1/tenant/courses/{course.Id}/archive", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await ada.Client.PostAsync($"/api/v1/tenant/invitations/{invite.Id}/accept", null)).StatusCode);
        Assert.Empty((await MineAsync(ada)).EnumerateArray());
    }

    // ---------- cohort invitations and isolation ----------
    [Fact]
    public async Task Inviting_a_cohort_sends_one_invitation_per_member_and_skips_people_already_in()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "COH-INV");
        var ada = await _world.AddLearnerAsync(t, "Ada"); var ben = await _world.AddLearnerAsync(t, "Ben"); var cleo = await _world.AddLearnerAsync(t, "Cleo");
        await EnrollAsync(cleo, course);
        var cohort = await NewCohortAsync(t, "Invite me", ada, ben, cleo);

        var result = await ReadAsync(await t.Admin.PostAsJsonAsync($"/api/v1/tenant/cohorts/{cohort}/invite", new { courseId = course.Id, message = "Join us" }));
        Assert.Equal(2, result.GetProperty("succeeded").GetInt32());
        Assert.Equal(1, result.GetProperty("alreadyIn").GetInt32());
        Assert.Single((await MineAsync(ada)).EnumerateArray());
        Assert.Equal("Join us", (await MineAsync(ben))[0].GetProperty("message").GetString());
        Assert.Empty((await MineAsync(cleo)).EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await t.Admin.PostAsJsonAsync($"/api/v1/tenant/cohorts/{cohort}/invite", new { courseId = course.Id, expiresInDays = 500 })).StatusCode);
    }

    [Fact]
    public async Task Invitations_are_isolated_between_organizations()
    {
        var a = await _world.NewTenantAsync();
        var b = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(a, "ISO-1");
        var inA = await _world.AddLearnerAsync(a, "Ada");
        var invite = await InviteOkAsync(a, course, inA.Email);

        Assert.Equal(0, (await ReadAsync(await b.Admin.GetAsync("/api/v1/tenant/invitations"))).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.PostAsync($"/api/v1/tenant/invitations/{invite.Id}/revoke", null)).StatusCode);
        var sameEmailElsewhere = await _world.AddLearnerAsync(b, "Ada"); // a different person with a different address
        Assert.Equal(HttpStatusCode.NotFound, (await sameEmailElsewhere.Client.PostAsJsonAsync("/api/v1/tenant/invitations/accept", new { token = invite.Token })).StatusCode);
    }
}
