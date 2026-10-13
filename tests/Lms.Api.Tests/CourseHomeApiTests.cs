using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lms.Api.Domain.Assessments;
using Lms.Api.Domain.Assignments;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

/// <summary>What one course's page needs: the lists a learner sees can be narrowed to one course, and a learner can read their own assessment results.</summary>
public sealed class CourseHomeApiTests(LmsApiFactory factory) : IClassFixture<LmsApiFactory>
{
    private readonly TestWorld world = new(factory);

    private sealed record Setup(Tenant Tenant, Person Teacher, Person Ada, Person Ben, CourseInfo Algebra, CourseInfo Art);

    private async Task<Setup> NewSetupAsync()
    {
        var t = await world.NewTenantAsync();
        var teacher = await world.AddPersonAsync(t, "Tara", "TEACHER");
        var ada = await world.AddLearnerAsync(t, "Ada");
        var ben = await world.AddLearnerAsync(t, "Ben");
        var algebra = await world.NewCourseAsync(t, "ALG-1");
        var art = await world.NewCourseAsync(t, "ART-1");
        foreach (var learner in new[] { ada, ben }) Assert.True((await EnrollAsync(learner, algebra)).IsSuccessStatusCode);
        Assert.True((await EnrollAsync(ada, art)).IsSuccessStatusCode);
        return new Setup(t, teacher, ada, ben, algebra, art);
    }

    private static async Task<string[]> TitlesAsync(HttpClient client, string url, string property = "title")
    {
        var body = await ReadAsync(await client.GetAsync(url));
        var list = body.ValueKind == JsonValueKind.Array ? body : body.GetProperty("items");     // the to-do list comes inside a feed
        return list.EnumerateArray().Select(item => item.GetProperty(property).GetString()!).ToArray();
    }

    [Fact]
    public async Task Live_classes_can_be_narrowed_to_one_course()
    {
        var s = await NewSetupAsync();
        var start = DateTimeOffset.UtcNow.AddHours(2);
        foreach (var (course, title) in new[] { (s.Algebra, "Algebra live"), (s.Art, "Art live") })
            Assert.Equal(HttpStatusCode.Created, (await s.Teacher.Client.PostAsJsonAsync("/api/v1/tenant/live-classes/sessions", new { courseId = course.Id, title, startAtUtc = start, endAtUtc = start.AddHours(1) })).StatusCode);
        const string url = "/api/v1/tenant/live-classes/sessions";
        Assert.Equal(["Algebra live", "Art live"], (await TitlesAsync(s.Ada.Client, url)).Order());
        Assert.Equal(["Algebra live"], await TitlesAsync(s.Ada.Client, $"{url}?courseId={s.Algebra.Id}"));
        Assert.Equal(["Art live"], await TitlesAsync(s.Ada.Client, $"{url}?courseId={s.Art.Id}"));
        Assert.Empty(await TitlesAsync(s.Ben.Client, $"{url}?courseId={s.Art.Id}"));                              // Ben is not in Art: still nothing
    }

    [Fact]
    public async Task Announcements_for_a_course_include_the_organizations_own()
    {
        var s = await NewSetupAsync();
        foreach (var (courseId, title) in new (Guid?, string)[] { (s.Algebra.Id, "Algebra news"), (s.Art.Id, "Art news"), (null, "Everyone news") })
            Assert.Equal(HttpStatusCode.Created, (await s.Teacher.Client.PostAsJsonAsync("/api/v1/tenant/community/announcements", new { title, body = "Read this.", courseId })).StatusCode);
        const string url = "/api/v1/tenant/community/announcements";
        Assert.Equal(["Algebra news", "Art news", "Everyone news"], (await TitlesAsync(s.Ada.Client, url)).Order());
        Assert.Equal(["Algebra news", "Everyone news"], (await TitlesAsync(s.Ada.Client, $"{url}?courseId={s.Algebra.Id}")).Order());
    }

    [Fact]
    public async Task Grades_and_certificates_can_be_narrowed_to_one_course()
    {
        var s = await NewSetupAsync();
        var mine = await ReadAsync(await s.Ada.Client.GetAsync("/api/v1/tenant/gradebook/me"));
        Assert.Equal(2, mine.GetArrayLength());
        var art = await ReadAsync(await s.Ada.Client.GetAsync($"/api/v1/tenant/gradebook/me?courseId={s.Art.Id}"));
        Assert.Equal(s.Art.Id, art.EnumerateArray().Single().GetProperty("courseId").GetGuid());
        Assert.Empty((await ReadAsync(await s.Ben.Client.GetAsync($"/api/v1/tenant/gradebook/me?courseId={s.Art.Id}"))).EnumerateArray());   // not enrolled there

        await CompleteCourseAsync(s.Ada, s.Algebra);
        var enrollments = await ReadAsync(await s.Ada.Client.GetAsync("/api/v1/tenant/enrollments"));
        var enrollmentId = enrollments.EnumerateArray().Single(item => item.GetProperty("courseId").GetGuid() == s.Algebra.Id).GetProperty("id").GetGuid();
        Assert.True((await s.Tenant.Admin.PostAsync($"/api/v1/tenant/enrollments/{enrollmentId}/certificate", null)).IsSuccessStatusCode);
        Assert.Single((await ReadAsync(await s.Ada.Client.GetAsync("/api/v1/tenant/certificates"))).EnumerateArray());
        Assert.Single((await ReadAsync(await s.Ada.Client.GetAsync($"/api/v1/tenant/certificates?courseId={s.Algebra.Id}"))).EnumerateArray());
        Assert.Empty((await ReadAsync(await s.Ada.Client.GetAsync($"/api/v1/tenant/certificates?courseId={s.Art.Id}"))).EnumerateArray());
    }

    [Fact]
    public async Task The_to_do_list_can_be_narrowed_to_one_course()
    {
        var s = await NewSetupAsync();
        var now = DateTimeOffset.UtcNow;
        await world.WithDbAsync(s.Tenant.Slug, async db =>
        {
            foreach (var course in new[] { s.Algebra, s.Art })
                db.Assignments.Add(new Assignment { Id = Guid.NewGuid(), TenantId = db.Courses.Single(item => item.Id == course.Id).TenantId, CourseId = course.Id, Title = $"Homework {course.Code}", MaxPoints = 10, DueAtUtc = now.AddDays(2), Status = AssignmentStatus.Published, CreatedByUserId = s.Teacher.Id, CreatedAtUtc = now, UpdatedAtUtc = now, PublishedAtUtc = now });
            await db.SaveChangesAsync();
        });
        var from = Uri.EscapeDataString(now.AddDays(-1).ToString("O")); var to = Uri.EscapeDataString(now.AddDays(10).ToString("O"));
        var url = $"/api/v1/tenant/tasks?from={from}&to={to}";
        Assert.Equal(["Homework ALG-1", "Homework ART-1"], (await TitlesAsync(s.Ada.Client, url)).Order());
        Assert.Equal(["Homework ART-1"], await TitlesAsync(s.Ada.Client, $"{url}&courseId={s.Art.Id}"));
        Assert.Equal(["Homework ALG-1"], await TitlesAsync(s.Ben.Client, url));
        Assert.Empty(await TitlesAsync(s.Ben.Client, $"{url}&courseId={s.Art.Id}"));
    }

    [Fact]
    public async Task A_learner_reads_their_own_results_for_a_course_and_nobody_elses()
    {
        var s = await NewSetupAsync();
        var now = DateTimeOffset.UtcNow;
        Guid published = Guid.Empty;
        await world.WithDbAsync(s.Tenant.Slug, async db =>
        {
            var tenantId = db.Courses.Single(item => item.Id == s.Algebra.Id).TenantId;
            Assessment Make(string title, AssessmentStatus status, Guid courseId) => new() { Id = Guid.NewGuid(), TenantId = tenantId, CourseId = courseId, CourseVersionId = Guid.NewGuid(), QuestionBankId = Guid.NewGuid(), Title = title, Status = status, AttemptLimit = 3, CreatedByUserId = s.Teacher.Id, CreatedAtUtc = now, UpdatedAtUtc = now };
            var quiz = Make("Quiz 1", AssessmentStatus.Published, s.Algebra.Id); published = quiz.Id;
            var draft = Make("Draft quiz", AssessmentStatus.Draft, s.Algebra.Id);
            var other = Make("Art quiz", AssessmentStatus.Published, s.Art.Id);
            db.Assessments.AddRange(quiz, draft, other);
            AssessmentAttempt Attempt(Assessment assessment, Guid learner, int number, int score, AttemptStatus status) => new() { Id = Guid.NewGuid(), TenantId = tenantId, AssessmentId = assessment.Id, CourseId = assessment.CourseId, LearnerUserId = learner, AttemptNumber = number, Status = status, ScorePoints = score, PossiblePoints = 10, Percentage = score * 10, StartedAtUtc = now.AddMinutes(number - 10), SubmittedAtUtc = now };
            db.AssessmentAttempts.AddRange(Attempt(quiz, s.Ada.Id, 1, 4, AttemptStatus.Graded), Attempt(quiz, s.Ada.Id, 2, 8, AttemptStatus.Graded), Attempt(quiz, s.Ben.Id, 1, 10, AttemptStatus.Graded), Attempt(other, s.Ada.Id, 1, 7, AttemptStatus.Graded));
            await db.SaveChangesAsync();
        });
        var url = $"/api/v1/tenant/courses/{s.Algebra.Id}/assessment-results";
        var results = (await ReadAsync(await s.Ada.Client.GetAsync(url))).EnumerateArray().ToList();
        Assert.Equal([2, 1], results.Select(item => item.GetProperty("attempt").GetProperty("attemptNumber").GetInt32()).ToArray());   // newest first
        Assert.All(results, item => { Assert.Equal(published, item.GetProperty("assessmentId").GetGuid()); Assert.Equal("Quiz 1", item.GetProperty("assessmentTitle").GetString()); });
        Assert.Equal(80m, results[0].GetProperty("attempt").GetProperty("percentage").GetDecimal());
        Assert.Equal(s.Ada.Id, results[0].GetProperty("attempt").GetProperty("learnerUserId").GetGuid());

        var ben = (await ReadAsync(await s.Ben.Client.GetAsync(url))).EnumerateArray().ToList();
        Assert.Equal(100m, ben.Single().GetProperty("attempt").GetProperty("percentage").GetDecimal());                                    // only Ben's own
        Assert.Equal(["Art quiz"], (await ReadAsync(await s.Ada.Client.GetAsync($"/api/v1/tenant/courses/{s.Art.Id}/assessment-results"))).EnumerateArray().Select(item => item.GetProperty("assessmentTitle").GetString()!).ToArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Teacher.Client.GetAsync(url)).StatusCode);                                        // taking assessments is a learner's permission
    }
}
