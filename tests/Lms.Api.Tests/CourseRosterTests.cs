using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

/// <summary>Staff putting people in a course from its Enrollment tab: who is in, who can be added, and adding several at once.</summary>
public sealed class CourseRosterTests : IClassFixture<LmsApiFactory>
{
    private readonly TestWorld _world;
    public CourseRosterTests(LmsApiFactory factory) { _world = new TestWorld(factory); }

    private static string Url(CourseInfo course, string tail) => $"/api/v1/tenant/courses/{course.Id}/{tail}";
    private static Task<HttpResponseMessage> EnrollMany(HttpClient client, CourseInfo course, object body) => client.PostAsJsonAsync(Url(course, "enrollments"), body);
    private static async Task<List<string>> NamesAsync(HttpClient client, string url) => (await ReadAsync(await client.GetAsync(url))).EnumerateArray().Select(item => item.GetProperty("name").GetString()!).ToList();

    [Fact]
    public async Task Staff_enroll_several_learners_at_once_and_they_appear_in_the_roster_and_stop_being_offered()
    {
        var t = await _world.NewTenantAsync();
        var teacher = await _world.AddPersonAsync(t, "Tara", "TEACHER");
        var ada = await _world.AddLearnerAsync(t, "Ada");
        var ben = await _world.AddLearnerAsync(t, "Ben");
        var cleo = await _world.AddLearnerAsync(t, "Cleo");
        var course = await _world.NewCourseAsync(t, "ROS-1");

        Assert.Contains("Ada", await NamesAsync(teacher.Client, Url(course, "enrollable-learners")));
        var response = await EnrollMany(teacher.Client, course, new { learnerUserIds = new[] { ada.Id, ben.Id } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal(2, body.GetProperty("enrolled").GetInt32());
        Assert.Equal(0, body.GetProperty("waitlisted").GetInt32());
        Assert.All(body.GetProperty("results").EnumerateArray(), item => Assert.Equal("Enrolled", item.GetProperty("outcome").GetString()));

        var roster = (await ReadAsync(await teacher.Client.GetAsync(Url(course, "enrollments")))).EnumerateArray().ToList();
        Assert.Equal(["Ada", "Ben"], roster.Select(item => item.GetProperty("name").GetString()));            // by name
        Assert.All(roster, item => { Assert.Equal("Active", item.GetProperty("status").GetString()); Assert.Equal("Administrator", item.GetProperty("source").GetString()); });
        var offered = await NamesAsync(teacher.Client, Url(course, "enrollable-learners"));
        Assert.DoesNotContain("Ada", offered);                                                                      // already in
        Assert.Contains("Cleo", offered);

        // The learners now see the course in their own list.
        Assert.Single((await ReadAsync(await ada.Client.GetAsync("/api/v1/tenant/enrollments"))).EnumerateArray());
        Assert.Equal(0, (await ReadAsync(await cleo.Client.GetAsync("/api/v1/tenant/enrollments"))).GetArrayLength());
    }

    [Fact]
    public async Task Enrolling_someone_already_in_is_reported_and_a_full_course_waitlists_the_rest()
    {
        var t = await _world.NewTenantAsync();
        var ada = await _world.AddLearnerAsync(t, "Ada");
        var ben = await _world.AddLearnerAsync(t, "Ben");
        var course = await _world.NewCourseAsync(t, "ROS-2", capacity: 1);

        var first = await ReadAsync(await EnrollMany(t.Admin, course, new { learnerUserIds = new[] { ada.Id, ben.Id } }));
        Assert.Equal(1, first.GetProperty("enrolled").GetInt32());
        Assert.Equal(1, first.GetProperty("waitlisted").GetInt32());
        Assert.Equal(["Enrolled", "Waitlisted"], first.GetProperty("results").EnumerateArray().Select(item => item.GetProperty("outcome").GetString()));
        Assert.Equal(["Ada"], await NamesAsync(t.Admin, Url(course, "enrollments")));                               // the waiting one is on the waitlist, not the roster
        Assert.DoesNotContain("Ben", await NamesAsync(t.Admin, Url(course, "enrollable-learners")));              // already waiting

        var again = await ReadAsync(await EnrollMany(t.Admin, course, new { learnerUserIds = new[] { ada.Id } }));
        Assert.Equal("AlreadyEnrolled", again.GetProperty("results")[0].GetProperty("outcome").GetString());
        Assert.Equal(0, again.GetProperty("enrolled").GetInt32());
    }

    [Fact]
    public async Task Prerequisites_are_checked_unless_staff_choose_to_enroll_anyway()
    {
        var t = await _world.NewTenantAsync();
        var ada = await _world.AddLearnerAsync(t, "Ada");
        var basics = await _world.NewCourseAsync(t, "ROS-BASE");
        var advanced = await _world.NewCourseAsync(t, "ROS-ADV");
        Assert.Equal(HttpStatusCode.OK, (await t.Admin.PutAsJsonAsync($"/api/v1/tenant/courses/{advanced.Id}/prerequisites", new { courseIds = new[] { basics.Id } })).StatusCode);

        var blocked = (await ReadAsync(await EnrollMany(t.Admin, advanced, new { learnerUserIds = new[] { ada.Id } }))).GetProperty("results")[0];
        Assert.Equal("MissingPrerequisites", blocked.GetProperty("outcome").GetString());
        Assert.Contains("ROS-BASE", blocked.GetProperty("message").GetString());
        Assert.Empty(await NamesAsync(t.Admin, Url(advanced, "enrollments")));

        var forced = await ReadAsync(await EnrollMany(t.Admin, advanced, new { learnerUserIds = new[] { ada.Id }, overridePrerequisites = true }));
        Assert.Equal(1, forced.GetProperty("enrolled").GetInt32());
    }

    [Fact]
    public async Task Bad_requests_are_refused_and_a_course_must_be_published_first()
    {
        var t = await _world.NewTenantAsync();
        var ada = await _world.AddLearnerAsync(t, "Ada");
        var course = await _world.NewCourseAsync(t, "ROS-3");
        Assert.Equal(HttpStatusCode.BadRequest, (await EnrollMany(t.Admin, course, new { learnerUserIds = Array.Empty<Guid>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await EnrollMany(t.Admin, course, new { learnerUserIds = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToArray() })).StatusCode);
        var stranger = await ReadAsync(await EnrollMany(t.Admin, course, new { learnerUserIds = new[] { Guid.NewGuid() } }));
        Assert.Equal("NotAvailable", stranger.GetProperty("results")[0].GetProperty("outcome").GetString());        // not a member of this organization
        Assert.Equal(HttpStatusCode.NotFound, (await EnrollMany(t.Admin, new CourseInfo(Guid.NewGuid(), "X", "X", [], []), new { learnerUserIds = new[] { ada.Id } })).StatusCode);

        var draft = await _world.NewCourseAsync(t, "ROS-DRAFT", publish: false);
        var refused = await EnrollMany(t.Admin, draft, new { learnerUserIds = new[] { ada.Id } });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("Publish the course", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await t.Admin.GetAsync(Url(course, $"enrollable-learners?q={new string('a', 101)}"))).StatusCode);
    }

    [Fact]
    public async Task The_people_offered_can_be_searched_by_name_or_email()
    {
        var t = await _world.NewTenantAsync();
        await _world.AddLearnerAsync(t, "Ada");
        await _world.AddLearnerAsync(t, "Adrian");
        await _world.AddLearnerAsync(t, "Ben");
        var course = await _world.NewCourseAsync(t, "ROS-4");
        var matches = await NamesAsync(t.Admin, Url(course, "enrollable-learners?q=AD"));                       // not case-sensitive
        Assert.Contains("Ada", matches); Assert.Contains("Adrian", matches); Assert.DoesNotContain("Ben", matches);
        Assert.Contains("Ben", await NamesAsync(t.Admin, Url(course, "enrollable-learners?q=@")));              // the email is searched too
        Assert.Equal(["Ben"], await NamesAsync(t.Admin, Url(course, "enrollable-learners?q=ben")));
        Assert.Empty(await NamesAsync(t.Admin, Url(course, "enrollable-learners?q=zebra")));
    }

    [Fact]
    public async Task Removing_someone_takes_them_out_of_the_roster_and_offers_them_again()
    {
        var t = await _world.NewTenantAsync();
        var ada = await _world.AddLearnerAsync(t, "Ada");
        var course = await _world.NewCourseAsync(t, "ROS-5");
        await EnrollMany(t.Admin, course, new { learnerUserIds = new[] { ada.Id } });
        var enrollmentId = (await ReadAsync(await t.Admin.GetAsync(Url(course, "enrollments"))))[0].GetProperty("enrollmentId").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await t.Admin.PostAsync($"/api/v1/tenant/enrollments/{enrollmentId}/withdraw", null)).StatusCode);
        Assert.Empty(await NamesAsync(t.Admin, Url(course, "enrollments")));
        Assert.Contains("Ada", await NamesAsync(t.Admin, Url(course, "enrollable-learners")));
        var back = await ReadAsync(await EnrollMany(t.Admin, course, new { learnerUserIds = new[] { ada.Id } }));      // and can be enrolled again
        Assert.Equal(1, back.GetProperty("enrolled").GetInt32());
    }

    [Fact]
    public async Task Only_people_who_manage_enrollment_can_use_these_and_other_organizations_see_nothing()
    {
        var t = await _world.NewTenantAsync();
        var ada = await _world.AddLearnerAsync(t, "Ada");
        var course = await _world.NewCourseAsync(t, "ROS-6");
        foreach (var url in new[] { Url(course, "enrollments"), Url(course, "enrollable-learners") })
            Assert.Equal(HttpStatusCode.Forbidden, (await ada.Client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await EnrollMany(ada.Client, course, new { learnerUserIds = new[] { ada.Id } })).StatusCode);

        var other = await _world.NewTenantAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.GetAsync(Url(course, "enrollments"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await EnrollMany(other.Admin, course, new { learnerUserIds = new[] { ada.Id } })).StatusCode);
    }
}
