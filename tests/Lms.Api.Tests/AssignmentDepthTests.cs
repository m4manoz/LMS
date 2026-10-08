using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lms.Api.Infrastructure.Assignments;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

public sealed class TextSimilarityTests
{
    private const string Essay = "The industrial revolution changed the way that people lived and worked in cities across europe because factories replaced small workshops and machines replaced hand tools";

    [Fact]
    public void Words_ignore_case_and_punctuation()
        => Assert.Equal(new[] { "it's", "a", "test", "42" }, TextSimilarity.Words("It's, A TEST! 42."));

    [Fact]
    public void A_copy_inside_a_longer_text_is_found_in_full_not_diluted()
    {
        var copy = TextSimilarity.Shingles(TextSimilarity.Words(Essay));
        var longer = TextSimilarity.Shingles(TextSimilarity.Words("Before we begin, a short opening paragraph about nothing in particular. " + Essay + " And then a long ending that goes on and on about quite different matters for a while."));
        var match = TextSimilarity.Compare(copy, longer);
        Assert.Equal(100, match.Percent);
        Assert.Single(match.Examples);   // the overlapping phrases are merged back into one passage
        Assert.StartsWith("the industrial revolution", match.Examples[0]);
        Assert.EndsWith("replaced hand tools", match.Examples[0]);
    }

    [Fact]
    public void Different_texts_share_nothing_and_a_few_common_words_do_not_count()
    {
        var a = TextSimilarity.Shingles(TextSimilarity.Words(Essay));
        var b = TextSimilarity.Shingles(TextSimilarity.Words("Photosynthesis lets green plants turn sunlight water and carbon dioxide into sugar and oxygen which feeds almost every food chain on the planet today"));
        Assert.Equal(0, TextSimilarity.Compare(a, b).Percent);
        Assert.Equal(0, TextSimilarity.Compare([], b).Percent);
    }

    [Fact]
    public void Phrases_from_the_instructions_are_left_out()
    {
        var instructions = TextSimilarity.Shingles(TextSimilarity.Words("Describe how the industrial revolution changed the way that people lived and worked")).ToHashSet();
        var withIgnore = TextSimilarity.Shingles(TextSimilarity.Words(Essay), instructions);
        var without = TextSimilarity.Shingles(TextSimilarity.Words(Essay));
        Assert.True(withIgnore.Count < without.Count);
        Assert.DoesNotContain("the industrial revolution changed the", withIgnore);
    }

    [Fact]
    public void Shingles_need_enough_words()
        => Assert.Empty(TextSimilarity.Shingles(TextSimilarity.Words("only four words here")));
}

/// <summary>Assignments scored by rubric, done in groups, and checked for copying.</summary>
public sealed class AssignmentDepthTests : IClassFixture<LmsApiFactory>
{
    private const string Essay = "The industrial revolution changed the way that people lived and worked in cities across europe because factories replaced small workshops and machines replaced hand tools and so families moved to towns";
    private readonly TestWorld _world;
    public AssignmentDepthTests(LmsApiFactory factory) => _world = new TestWorld(factory);

    private sealed record Setup(Tenant Tenant, CourseInfo Course, Person Lena, Person Otto, Person Cleo);

    private async Task<Setup> NewSetupAsync()
    {
        var tenant = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(tenant, "ASG");
        var lena = await _world.AddLearnerAsync(tenant, "Lena");
        var otto = await _world.AddLearnerAsync(tenant, "Otto");
        var cleo = await _world.AddLearnerAsync(tenant, "Cleo");
        foreach (var learner in new[] { lena, otto, cleo }) Assert.True((await EnrollAsync(learner, course)).IsSuccessStatusCode);
        return new Setup(tenant, course, lena, otto, cleo);
    }

    private static async Task<Guid> CreateAsync(Setup s, object body, bool publish = true)
    {
        var response = await s.Tenant.Admin.PostAsJsonAsync("/api/v1/tenant/assignments", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await ReadAsync(response)).GetProperty("id").GetGuid();
        if (publish) Assert.Equal(HttpStatusCode.NoContent, (await s.Tenant.Admin.PostAsync($"/api/v1/tenant/assignments/{id}/publish", null)).StatusCode);
        return id;
    }

    private static MultipartFormDataContent Form(string text, string? fileName = null, string fileText = "", string contentType = "text/plain")
    {
        var content = new MultipartFormDataContent { { new StringContent(text), "text" } };
        if (fileName is not null) { var file = new ByteArrayContent(Encoding.UTF8.GetBytes(fileText)); file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType); content.Add(file, "file", fileName); }
        return content;
    }

    private static Task<HttpResponseMessage> Submit(Person who, Guid assignmentId, MultipartFormDataContent form) => who.Client.PostAsync($"/api/v1/tenant/assignments/{assignmentId}/submission", form);

    private static async Task<JsonElement> RubricAsync(Setup s, string name = "Report rubric")
    {
        var response = await s.Tenant.Admin.PostAsJsonAsync($"/api/v1/tenant/courses/{s.Course.Id}/rubrics", new
        {
            name,
            criteria = new object[]
            {
                new { name = "Content", levels = new object[] { new { label = "Full", points = 6 }, new { label = "Part", points = 3 }, new { label = "Little", points = 0 } } },
                new { name = "Clarity", levels = new object[] { new { label = "Clear", points = 4 }, new { label = "Muddled", points = 1 } } },
            }
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync(response);
    }

    private static async Task<JsonElement> MineAsync(Person who, Guid assignmentId)
        => await ReadAsync(await who.Client.GetAsync($"/api/v1/tenant/assignments/{assignmentId}"));

    // ---------- rubrics ----------
    [Fact]
    public async Task An_assignment_with_a_rubric_is_worth_the_rubrics_total_and_learners_see_the_criteria()
    {
        var s = await NewSetupAsync();
        var rubric = await RubricAsync(s);
        var id = await CreateAsync(s, new { courseId = s.Course.Id, title = "Report", maxPoints = 50, allowLate = false, latePenaltyPercent = 0, rubricId = rubric.GetProperty("id").GetGuid() });
        var seen = await MineAsync(s.Lena, id);
        Assert.Equal(10, seen.GetProperty("maxPoints").GetInt32());   // 6 + 4, not the 50 that was sent
        Assert.Equal(new[] { "Content", "Clarity" }, seen.GetProperty("rubric").GetProperty("criteria").EnumerateArray().Select(item => item.GetProperty("name").GetString()).ToArray());

        var missing = await s.Tenant.Admin.PostAsJsonAsync("/api/v1/tenant/assignments", new { courseId = s.Course.Id, title = "Other report", maxPoints = 10, rubricId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        // A rubric an assignment uses is frozen.
        var url = $"/api/v1/tenant/rubrics/{rubric.GetProperty("id").GetGuid()}";
        Assert.Equal(HttpStatusCode.Conflict, (await s.Tenant.Admin.DeleteAsync(url)).StatusCode);
        Assert.Equal(1, (await ReadAsync(await s.Tenant.Admin.GetAsync($"/api/v1/tenant/courses/{s.Course.Id}/rubrics"))).EnumerateArray().Single().GetProperty("usedByQuestions").GetInt32());
    }

    [Fact]
    public async Task Grading_by_rubric_adds_up_the_criteria_and_the_learner_sees_each_one()
    {
        var s = await NewSetupAsync();
        var rubric = await RubricAsync(s);
        var criteria = rubric.GetProperty("criteria").EnumerateArray().ToList();
        var content = criteria[0].GetProperty("id").GetGuid();
        var clarity = criteria[1].GetProperty("id").GetGuid();
        var id = await CreateAsync(s, new { courseId = s.Course.Id, title = "Report", maxPoints = 1, rubricId = rubric.GetProperty("id").GetGuid() });
        var submitted = await ReadAsync(await Submit(s.Lena, id, Form("My report")));
        var url = $"/api/v1/tenant/assignments/submissions/{submitted.GetProperty("id").GetGuid()}/grade";

        Assert.Equal(HttpStatusCode.BadRequest, (await s.Tenant.Admin.PostAsJsonAsync(url, new { scorePoints = 9 })).StatusCode);   // a rubric needs its criteria scored
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Tenant.Admin.PostAsJsonAsync(url, new { criterionScores = new[] { new { criterionId = content, points = 7 }, new { criterionId = clarity, points = 1 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Tenant.Admin.PostAsJsonAsync(url, new { criterionScores = new[] { new { criterionId = content, points = 3 } } })).StatusCode);

        var graded = await ReadAsync(await s.Tenant.Admin.PostAsJsonAsync(url, new { scorePoints = 999, feedback = "Nice", criterionScores = new[] { new { criterionId = content, points = 6 }, new { criterionId = clarity, points = 1 } } }));
        Assert.Equal(7, graded.GetProperty("scorePoints").GetDecimal());   // the typed 999 is ignored; the criteria decide
        var mine = (await MineAsync(s.Lena, id)).GetProperty("mySubmission");
        Assert.Equal(new[] { 6, 1 }, mine.GetProperty("rubricScores").EnumerateArray().Select(item => item.GetProperty("points").GetInt32()).ToArray());
        Assert.Equal("Content", mine.GetProperty("rubricScores")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_late_penalty_still_applies_to_a_rubric_score()
    {
        var s = await NewSetupAsync();
        var rubric = await RubricAsync(s);
        var criteria = rubric.GetProperty("criteria").EnumerateArray().ToList();
        var id = await CreateAsync(s, new { courseId = s.Course.Id, title = "Late report", maxPoints = 10, dueAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5), allowLate = true, latePenaltyPercent = 50, rubricId = rubric.GetProperty("id").GetGuid() });
        var submitted = await ReadAsync(await Submit(s.Lena, id, Form("Late")));
        var graded = await ReadAsync(await s.Tenant.Admin.PostAsJsonAsync($"/api/v1/tenant/assignments/submissions/{submitted.GetProperty("id").GetGuid()}/grade",
            new { criterionScores = new[] { new { criterionId = criteria[0].GetProperty("id").GetGuid(), points = 6 }, new { criterionId = criteria[1].GetProperty("id").GetGuid(), points = 4 } } }));
        Assert.Equal((10m, 5m), (graded.GetProperty("scorePoints").GetDecimal(), graded.GetProperty("finalPoints").GetDecimal()));
    }

    // ---------- groups ----------
    private async Task<(Guid Assignment, Guid Group)> GroupAssignmentAsync(Setup s)
    {
        var id = await CreateAsync(s, new { courseId = s.Course.Id, title = "Team project", maxPoints = 100, isGroup = true });
        var created = await s.Tenant.Admin.PostAsJsonAsync($"/api/v1/tenant/assignments/{id}/groups", new { name = "Team A", memberUserIds = new[] { s.Lena.Id, s.Otto.Id } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (id, (await ReadAsync(created)).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Groups_are_checked_when_they_are_made()
    {
        var s = await NewSetupAsync();
        var plain = await CreateAsync(s, new { courseId = s.Course.Id, title = "Solo work", maxPoints = 10 });
        Assert.Equal(HttpStatusCode.Conflict, (await s.Tenant.Admin.PostAsJsonAsync($"/api/v1/tenant/assignments/{plain}/groups", new { name = "X", memberUserIds = new[] { s.Lena.Id } })).StatusCode);

        var (id, _) = await GroupAssignmentAsync(s);
        var url = $"/api/v1/tenant/assignments/{id}/groups";
        Assert.Equal(HttpStatusCode.Conflict, (await s.Tenant.Admin.PostAsJsonAsync(url, new { name = "Team B", memberUserIds = new[] { s.Lena.Id, s.Cleo.Id } })).StatusCode);   // Lena is already in Team A
        Assert.Equal(HttpStatusCode.Conflict, (await s.Tenant.Admin.PostAsJsonAsync(url, new { name = "Team A", memberUserIds = new[] { s.Cleo.Id } })).StatusCode);          // the name is taken
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Tenant.Admin.PostAsJsonAsync(url, new { name = "Ghosts", memberUserIds = new[] { Guid.NewGuid() } })).StatusCode);   // not enrolled
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Tenant.Admin.PostAsJsonAsync(url, new { name = "Empty", memberUserIds = Array.Empty<Guid>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Tenant.Admin.PostAsJsonAsync(url, new { name = "", memberUserIds = new[] { s.Cleo.Id } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Lena.Client.PostAsJsonAsync(url, new { name = "Mine", memberUserIds = new[] { s.Cleo.Id } })).StatusCode);

        var overview = await ReadAsync(await s.Tenant.Admin.GetAsync(url));
        Assert.Equal(new[] { "Lena", "Otto" }, overview.GetProperty("groups")[0].GetProperty("members").EnumerateArray().Select(item => item.GetProperty("name").GetString()).ToArray());
        Assert.Equal(new[] { "Cleo" }, overview.GetProperty("unassigned").EnumerateArray().Select(item => item.GetProperty("name").GetString()).ToArray());
        var mine = (await MineAsync(s.Otto, id)).GetProperty("myGroup");
        Assert.Equal("Team A", mine.GetProperty("name").GetString());
        Assert.Equal(new[] { "Lena", "Otto" }, mine.GetProperty("members").EnumerateArray().Select(item => item.GetString()).ToArray());
    }

    [Fact]
    public async Task One_members_submission_is_the_whole_groups_and_one_grade_reaches_everyone()
    {
        var s = await NewSetupAsync();
        var (id, _) = await GroupAssignmentAsync(s);
        var submitted = await ReadAsync(await Submit(s.Lena, id, Form("Our project", "plan.txt", "the plan")));
        Assert.Equal("Our project", (await MineAsync(s.Otto, id)).GetProperty("mySubmission").GetProperty("textResponse").GetString());   // the teammate sees it too
        Assert.Equal("plan.txt", (await MineAsync(s.Otto, id)).GetProperty("mySubmission").GetProperty("fileName").GetString());

        // A teammate improves it; there is still one piece of work.
        Assert.Equal(HttpStatusCode.OK, (await Submit(s.Otto, id, Form("Our better project"))).StatusCode);
        Assert.Equal("Our better project", (await MineAsync(s.Lena, id)).GetProperty("mySubmission").GetProperty("textResponse").GetString());
        Assert.Equal("plan.txt", (await MineAsync(s.Lena, id)).GetProperty("mySubmission").GetProperty("fileName").GetString());   // the earlier file is kept

        var rows = (await ReadAsync(await s.Tenant.Admin.GetAsync($"/api/v1/tenant/assignments/{id}/submissions"))).EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("Team A", row.GetProperty("groupName").GetString()));

        // Grading either row grades both.
        var graded = await s.Tenant.Admin.PostAsJsonAsync($"/api/v1/tenant/assignments/submissions/{rows[0].GetProperty("id").GetGuid()}/grade", new { scorePoints = 88, feedback = "Strong teamwork" });
        Assert.Equal(HttpStatusCode.OK, graded.StatusCode);
        foreach (var member in new[] { s.Lena, s.Otto })
        {
            var mine = (await MineAsync(member, id)).GetProperty("mySubmission");
            Assert.Equal(("Graded", 88m, "Strong teamwork"), (mine.GetProperty("status").GetString(), mine.GetProperty("finalPoints").GetDecimal(), mine.GetProperty("feedback").GetString()));
            Assert.Single((await InboxAsync(member, "ASSIGNMENT_GRADED")).EnumerateArray());
        }
        Assert.Equal(HttpStatusCode.Conflict, (await Submit(s.Lena, id, Form("Changed our minds"))).StatusCode);
        // Each member has a row of their own; the order the two come back in does not matter.
        Assert.NotEqual(rows[0].GetProperty("id").GetGuid(), rows[1].GetProperty("id").GetGuid());
        Assert.Contains(submitted.GetProperty("id").GetGuid(), rows.Select(row => row.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task Someone_without_a_group_cannot_submit_group_work_and_a_group_that_has_submitted_cannot_be_changed()
    {
        var s = await NewSetupAsync();
        var (id, groupId) = await GroupAssignmentAsync(s);
        var refused = await Submit(s.Cleo, id, Form("Solo attempt"));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("group", await refused.Content.ReadAsStringAsync());

        // Before any work is handed in the group can be rearranged.
        var url = $"/api/v1/tenant/assignments/{id}/groups/{groupId}";
        Assert.Equal(HttpStatusCode.NoContent, (await s.Tenant.Admin.PutAsJsonAsync(url, new { name = "Team A", memberUserIds = new[] { s.Lena.Id, s.Cleo.Id } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Submit(s.Cleo, id, Form("Now in a group"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Tenant.Admin.PutAsJsonAsync(url, new { name = "Team A", memberUserIds = new[] { s.Otto.Id } })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Tenant.Admin.DeleteAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Submit(s.Otto, id, Form("Not mine"))).StatusCode);   // moved out of the group earlier, so he has none now
    }

    [Fact]
    public async Task An_empty_group_can_be_deleted_and_individual_work_is_unchanged()
    {
        var s = await NewSetupAsync();
        var (id, groupId) = await GroupAssignmentAsync(s);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Tenant.Admin.DeleteAsync($"/api/v1/tenant/assignments/{id}/groups/{groupId}")).StatusCode);
        Assert.Empty((await ReadAsync(await s.Tenant.Admin.GetAsync($"/api/v1/tenant/assignments/{id}/groups"))).GetProperty("groups").EnumerateArray());

        var solo = await CreateAsync(s, new { courseId = s.Course.Id, title = "Solo", maxPoints = 10 });
        await Submit(s.Lena, solo, Form("Mine"));
        Assert.Equal(JsonValueKind.Null, (await MineAsync(s.Otto, solo)).GetProperty("mySubmission").ValueKind);   // nobody else is affected
        Assert.Single((await ReadAsync(await s.Tenant.Admin.GetAsync($"/api/v1/tenant/assignments/{solo}/submissions"))).EnumerateArray());
    }

    // ---------- similarity ----------
    private static async Task<JsonElement> ReportAsync(Setup s, Guid assignmentId, int? threshold = null)
        => await ReadAsync(await s.Tenant.Admin.GetAsync($"/api/v1/tenant/assignments/{assignmentId}/similarity" + (threshold is null ? "" : $"?threshold={threshold}")));

    [Fact]
    public async Task Copied_work_is_listed_with_how_much_and_what_was_shared_and_honest_work_is_not()
    {
        var s = await NewSetupAsync();
        var id = await CreateAsync(s, new { courseId = s.Course.Id, title = "Essay", maxPoints = 10 });
        await Submit(s.Lena, id, Form(Essay));
        await Submit(s.Otto, id, Form("Introduction written by Otto himself. " + Essay + " Finally Otto adds a conclusion of his own about the long term effects on society and the environment."));
        await Submit(s.Cleo, id, Form("Photosynthesis lets green plants turn sunlight water and carbon dioxide into sugar and oxygen which feeds almost every food chain on the planet today and has done so for millions of years"));

        var report = await ReportAsync(s, id);
        Assert.Equal((3, 3), (report.GetProperty("submissions").GetInt32(), report.GetProperty("compared").GetInt32()));
        var pair = Assert.Single(report.GetProperty("pairs").EnumerateArray());
        Assert.Equal(new[] { "Lena", "Otto" }, new[] { pair.GetProperty("firstName").GetString(), pair.GetProperty("secondName").GetString() }.Order().ToArray());
        Assert.Equal(100, pair.GetProperty("percent").GetInt32());   // all of Lena's text is inside Otto's
        Assert.Contains("industrial revolution", pair.GetProperty("examples")[0].GetString());
    }

    [Fact]
    public async Task Wording_taken_from_the_instructions_is_not_counted_and_short_texts_are_skipped()
    {
        var s = await NewSetupAsync();
        var question = "Explain in your own words why the industrial revolution changed the way that people lived and worked in cities across europe";
        var id = await CreateAsync(s, new { courseId = s.Course.Id, title = "Quoting", instructions = question, maxPoints = 10 });
        await Submit(s.Lena, id, Form(question + " I think it was mostly because of steam power and the railways which let goods and people move quickly between places that were once far apart"));
        await Submit(s.Otto, id, Form(question + " In my view the most important change was the growth of banks and of trade with colonies which brought raw cotton to the new mills of the north"));
        await Submit(s.Cleo, id, Form("Too short to compare."));

        var report = await ReportAsync(s, id);
        Assert.Empty(report.GetProperty("pairs").EnumerateArray());
        Assert.Equal(1, report.GetProperty("tooShort").GetInt32());
        Assert.Equal(2, report.GetProperty("compared").GetInt32());
    }

    [Fact]
    public async Task Teammates_are_never_compared_and_the_threshold_filters()
    {
        var s = await NewSetupAsync();
        var (id, _) = await GroupAssignmentAsync(s);
        await Submit(s.Lena, id, Form(Essay));
        Assert.Empty((await ReportAsync(s, id)).GetProperty("pairs").EnumerateArray());   // Lena and Otto hold the same work on purpose

        var plain = await CreateAsync(s, new { courseId = s.Course.Id, title = "Partly alike", maxPoints = 10 });
        await Submit(s.Lena, plain, Form(Essay));
        await Submit(s.Otto, plain, Form("Quite a different opening about the age of steam and the railways that were built. " + Essay.Split(" and so families")[0] + " but then it carries on with completely new thoughts about education reform and the growth of unions in the later nineteenth century"));
        Assert.Single((await ReportAsync(s, plain, 20)).GetProperty("pairs").EnumerateArray());
        Assert.Empty((await ReportAsync(s, plain, 100)).GetProperty("pairs").EnumerateArray());
    }

    [Fact]
    public async Task Text_files_are_read_and_other_files_are_reported_as_not_read()
    {
        var s = await NewSetupAsync();
        var id = await CreateAsync(s, new { courseId = s.Course.Id, title = "Files", maxPoints = 10 });
        await Submit(s.Lena, id, Form("See my file", "essay.txt", Essay));
        await Submit(s.Otto, id, Form("Mine is attached", "copy.txt", Essay));
        await Submit(s.Cleo, id, Form("A pdf this time", "report.pdf", "%PDF-1.4", "application/pdf"));
        var report = await ReportAsync(s, id);
        Assert.Single(report.GetProperty("pairs").EnumerateArray());
        Assert.Equal(1, report.GetProperty("filesNotRead").GetInt32());
    }

    [Fact]
    public async Task Only_graders_can_run_the_check_and_other_organizations_cannot()
    {
        var s = await NewSetupAsync();
        var id = await CreateAsync(s, new { courseId = s.Course.Id, title = "Private", maxPoints = 10 });
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Lena.Client.GetAsync($"/api/v1/tenant/assignments/{id}/similarity")).StatusCode);
        var other = await _world.NewTenantAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.GetAsync($"/api/v1/tenant/assignments/{id}/similarity")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Submit(s.Lena, Guid.NewGuid(), Form("x"))).StatusCode);
    }
}
