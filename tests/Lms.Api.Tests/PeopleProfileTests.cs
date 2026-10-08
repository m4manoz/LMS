using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

/// <summary>The Instructors and Learners directories and each person's profile.</summary>
public sealed class PeopleProfileTests : IClassFixture<LmsApiFactory>
{
    private readonly LmsApiFactory _factory;
    private readonly TestWorld _world;
    public PeopleProfileTests(LmsApiFactory factory) { _factory = factory; _world = new TestWorld(factory); }

    private static async Task<JsonElement> ListAsync(HttpClient client, string kind, string? q = null)
        => await ReadAsync(await client.GetAsync($"/api/v1/tenant/people?kind={kind}" + (q is null ? "" : $"&q={Uri.EscapeDataString(q)}")));

    [Fact]
    public async Task Directories_separate_learners_from_instructors_and_can_be_searched()
    {
        var t = await _world.NewTenantAsync();
        await _world.AddLearnerAsync(t, "Lena");
        await _world.AddLearnerAsync(t, "Liam");
        await _world.AddPersonAsync(t, "Tara", "TEACHER");

        var learners = (await ListAsync(t.Admin, "learners")).EnumerateArray().Select(item => item.GetProperty("displayName").GetString()).ToArray();
        Assert.Equal(["Lena", "Liam"], learners);
        Assert.Equal(["Tara"], (await ListAsync(t.Admin, "instructors")).EnumerateArray().Select(item => item.GetProperty("displayName").GetString()).ToArray());
        Assert.Equal(["Liam"], (await ListAsync(t.Admin, "learners", "LIA")).EnumerateArray().Select(item => item.GetProperty("displayName").GetString()).ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await t.Admin.GetAsync("/api/v1/tenant/people?kind=everyone")).StatusCode);
    }

    [Fact]
    public async Task Staff_can_look_but_learners_and_guardians_cannot()
    {
        var t = await _world.NewTenantAsync();
        var learner = await _world.AddLearnerAsync(t, "Lena");
        var guardian = await _world.AddPersonAsync(t, "Gary", "GUARDIAN");
        var teacher = await _world.AddPersonAsync(t, "Tara", "TEACHER");

        Assert.Equal(HttpStatusCode.OK, (await teacher.Client.GetAsync("/api/v1/tenant/people?kind=learners")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await teacher.Client.GetAsync($"/api/v1/tenant/people/{learner.Id}")).StatusCode);
        foreach (var outsider in new[] { learner, guardian })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await outsider.Client.GetAsync("/api/v1/tenant/people?kind=learners")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await outsider.Client.GetAsync($"/api/v1/tenant/people/{teacher.Id}")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateTenantClient(t.Slug).GetAsync("/api/v1/tenant/people?kind=learners")).StatusCode);
    }

    [Fact]
    public async Task A_learner_profile_shows_courses_and_progress()
    {
        var t = await _world.NewTenantAsync();
        var lena = await _world.AddLearnerAsync(t, "Lena");
        var done = await _world.NewCourseAsync(t, "DONE", lessonsPerModule: 2);
        var half = await _world.NewCourseAsync(t, "HALF", lessonsPerModule: 2);
        Assert.True((await EnrollAsync(lena, done)).IsSuccessStatusCode);
        Assert.True((await EnrollAsync(lena, half)).IsSuccessStatusCode);
        await CompleteCourseAsync(lena, done);
        await CompleteLessonAsync(lena, half, half.LessonIds[0][0]);

        var profile = await ReadAsync(await t.Admin.GetAsync($"/api/v1/tenant/people/{lena.Id}"));
        Assert.Equal("Lena", profile.GetProperty("displayName").GetString());
        Assert.Contains("LEARNER", profile.GetProperty("roles").EnumerateArray().Select(item => item.GetString()));
        var summary = profile.GetProperty("summary");
        Assert.Equal(1, summary.GetProperty("activeCourses").GetInt32());
        Assert.Equal(1, summary.GetProperty("completedCourses").GetInt32());
        Assert.Equal(50, summary.GetProperty("averageProgressPercent").GetInt32());
        Assert.Equal(2, profile.GetProperty("enrollments").GetArrayLength());

        var listed = (await ListAsync(t.Admin, "learners")).EnumerateArray().Single();
        Assert.Equal(2, listed.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task An_instructor_profile_shows_their_courses_and_learners()
    {
        var t = await _world.NewTenantAsync();
        var teacher = await _world.AddPersonAsync(t, "Tara", "TEACHER");
        var lena = await _world.AddLearnerAsync(t, "Lena");
        var created = await ReadAsync(await teacher.Client.PostAsJsonAsync("/api/v1/tenant/courses", new { code = "TARA1", title = "Tara's course" }));
        var courseId = created.GetProperty("course").GetProperty("id").GetGuid();
        var module = await ReadAsync(await teacher.Client.PostAsJsonAsync($"/api/v1/tenant/courses/{courseId}/modules", new { title = "M" }));
        (await teacher.Client.PostAsJsonAsync($"/api/v1/tenant/courses/{courseId}/modules/{module.GetProperty("id").GetGuid()}/lessons", new { title = "L", contentHtml = "x" })).EnsureSuccessStatusCode();
        (await teacher.Client.PostAsync($"/api/v1/tenant/courses/{courseId}/submit-review", null)).EnsureSuccessStatusCode();
        (await t.Admin.PostAsync($"/api/v1/tenant/courses/{courseId}/publish", null)).EnsureSuccessStatusCode();
        Assert.True((await lena.Client.PostAsync($"/api/v1/tenant/courses/{courseId}/enroll", null)).IsSuccessStatusCode);

        var profile = await ReadAsync(await t.Admin.GetAsync($"/api/v1/tenant/people/{teacher.Id}"));
        var course = Assert.Single(profile.GetProperty("courses").EnumerateArray());
        Assert.Equal("TARA1", course.GetProperty("code").GetString());
        Assert.Equal(1, course.GetProperty("learners").GetInt32());
        Assert.Equal(1, profile.GetProperty("summary").GetProperty("coursesTaught").GetInt32());
        Assert.Equal(1, (await ListAsync(t.Admin, "instructors")).EnumerateArray().Single().GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Profile_details_are_saved_by_user_managers_and_birth_dates_stay_private_to_them()
    {
        var t = await _world.NewTenantAsync();
        var lena = await _world.AddLearnerAsync(t, "Lena");
        var teacher = await _world.AddPersonAsync(t, "Tara", "TEACHER");

        var save = await t.Admin.PutAsJsonAsync($"/api/v1/tenant/people/{lena.Id}/profile", new { kind = "learner", studentNumber = " S-100 ", gradeLevel = "Grade 8", dateOfBirthAd = "2012-04-05" });
        Assert.Equal(HttpStatusCode.NoContent, save.StatusCode);

        var adminView = (await ReadAsync(await t.Admin.GetAsync($"/api/v1/tenant/people/{lena.Id}"))).GetProperty("learner");
        Assert.Equal("S-100", adminView.GetProperty("studentNumber").GetString());
        Assert.Equal("Grade 8", adminView.GetProperty("gradeLevel").GetString());
        Assert.Equal("2012-04-05", adminView.GetProperty("dateOfBirthAd").GetString());
        var teacherView = (await ReadAsync(await teacher.Client.GetAsync($"/api/v1/tenant/people/{lena.Id}"))).GetProperty("learner");
        Assert.Equal("S-100", teacherView.GetProperty("studentNumber").GetString());
        Assert.Equal(JsonValueKind.Null, teacherView.GetProperty("dateOfBirthAd").ValueKind);

        Assert.Equal(HttpStatusCode.NoContent, (await t.Admin.PutAsJsonAsync($"/api/v1/tenant/people/{teacher.Id}/profile", new { kind = "instructor", employeeNumber = "E-7", subjectSpecialty = "Physics" })).StatusCode);
        Assert.Equal("Physics", (await ReadAsync(await t.Admin.GetAsync($"/api/v1/tenant/people/{teacher.Id}"))).GetProperty("teacher").GetProperty("subjectSpecialty").GetString());
    }

    [Fact]
    public async Task Only_user_managers_may_edit_and_bad_values_are_refused()
    {
        var t = await _world.NewTenantAsync();
        var lena = await _world.AddLearnerAsync(t, "Lena");
        var teacher = await _world.AddPersonAsync(t, "Tara", "TEACHER");

        Assert.Equal(HttpStatusCode.Forbidden, (await teacher.Client.PutAsJsonAsync($"/api/v1/tenant/people/{lena.Id}/profile", new { kind = "learner", gradeLevel = "9" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await lena.Client.PutAsJsonAsync($"/api/v1/tenant/people/{lena.Id}/profile", new { kind = "learner", gradeLevel = "9" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await t.Admin.PutAsJsonAsync($"/api/v1/tenant/people/{lena.Id}/profile", new { kind = "learner", gradeLevel = new string('x', 81) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await t.Admin.PutAsJsonAsync($"/api/v1/tenant/people/{lena.Id}/profile", new { kind = "learner", dateOfBirthAd = "2999-01-01" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await t.Admin.PutAsJsonAsync($"/api/v1/tenant/people/{lena.Id}/profile", new { kind = "wizard" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t.Admin.PutAsJsonAsync($"/api/v1/tenant/people/{Guid.NewGuid()}/profile", new { kind = "learner" })).StatusCode);
    }

    [Fact]
    public async Task People_of_other_organizations_are_invisible()
    {
        var mine = await _world.NewTenantAsync();
        var other = await _world.NewTenantAsync();
        var stranger = await _world.AddLearnerAsync(other, "Stella");
        Assert.Equal(HttpStatusCode.NotFound, (await mine.Admin.GetAsync($"/api/v1/tenant/people/{stranger.Id}")).StatusCode);
        Assert.Empty((await ListAsync(mine.Admin, "learners")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await mine.Admin.PutAsJsonAsync($"/api/v1/tenant/people/{stranger.Id}/profile", new { kind = "learner", gradeLevel = "1" })).StatusCode);
    }
}
