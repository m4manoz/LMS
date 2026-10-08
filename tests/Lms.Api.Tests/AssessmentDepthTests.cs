using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

/// <summary>Versions, question pools, shuffling, accommodations, rubrics and file answers.</summary>
public sealed class AssessmentDepthTests : IClassFixture<LmsApiFactory>
{
    private readonly TestWorld _world;
    public AssessmentDepthTests(LmsApiFactory factory) => _world = new TestWorld(factory);

    private sealed record Setup(Tenant Tenant, CourseInfo Course, Person Learner);

    private async Task<Setup> NewSetupAsync(string learnerName = "Lena")
    {
        var tenant = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(tenant, "ASM");
        var learner = await _world.AddLearnerAsync(tenant, learnerName);
        Assert.True((await EnrollAsync(learner, course)).IsSuccessStatusCode);
        return new Setup(tenant, course, learner);
    }

    private static async Task<Guid> NewAssessmentAsync(Setup s, object? settings = null)
    {
        var body = settings ?? new { title = "Quiz", attemptLimit = 3 };
        var response = await s.Tenant.Admin.PostAsJsonAsync($"/api/v1/tenant/courses/{s.Course.Id}/assessments", body);
        response.EnsureSuccessStatusCode();
        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> AddAsync(Setup s, Guid assessmentId, object question)
    {
        var response = await s.Tenant.Admin.PostAsJsonAsync($"/api/v1/tenant/assessments/{assessmentId}/questions", question);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    private static object Choice(string prompt, int points = 1, string? pool = null) => new { type = "MultipleChoice", prompt, options = new[] { "right", "wrong", "other" }, correctAnswers = new[] { "right" }, points, pool };

    private static Task<HttpResponseMessage> PublishAsync(Setup s, Guid assessmentId) => s.Tenant.Admin.PostAsync($"/api/v1/tenant/assessments/{assessmentId}/publish", null);
    private static async Task<JsonElement> StartAsync(Person person, Guid assessmentId) => await ReadAsync(await person.Client.PostAsync($"/api/v1/tenant/assessments/{assessmentId}/attempts", null));
    private static Guid AttemptId(JsonElement attempt) => attempt.GetProperty("attempt").GetProperty("id").GetGuid();
    private static List<JsonElement> QuestionsOf(JsonElement attempt) => attempt.GetProperty("questions").EnumerateArray().ToList();

    private static async Task<JsonElement> AnswerAllRightAsync(Person person, JsonElement attempt)
    {
        foreach (var question in QuestionsOf(attempt))
            (await person.Client.PutAsJsonAsync($"/api/v1/tenant/assessment-attempts/{AttemptId(attempt)}/answers/{question.GetProperty("id").GetGuid()}", new { answers = new[] { "right" } })).EnsureSuccessStatusCode();
        return await ReadAsync(await person.Client.PostAsync($"/api/v1/tenant/assessment-attempts/{AttemptId(attempt)}/submit", null));
    }

    // ---------- versions ----------
    [Fact]
    public async Task A_published_assessment_is_changed_through_a_new_version_and_old_attempts_keep_their_questions()
    {
        var s = await NewSetupAsync();
        var id = await NewAssessmentAsync(s);
        await AddAsync(s, id, Choice("One"));
        await AddAsync(s, id, Choice("Two"));
        Assert.True((await PublishAsync(s, id)).IsSuccessStatusCode);

        var first = await StartAsync(s.Learner, id);
        Assert.Equal(2, QuestionsOf(first).Count);
        var graded = await AnswerAllRightAsync(s.Learner, first);
        Assert.Equal(100m, graded.GetProperty("attempt").GetProperty("percentage").GetDecimal());

        // A published assessment cannot be edited in place.
        var blocked = await s.Tenant.Admin.PostAsJsonAsync($"/api/v1/tenant/assessments/{id}/questions", Choice("Sneaky"));
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

        var started = await ReadAsync(await s.Tenant.Admin.PostAsync($"/api/v1/tenant/assessments/{id}/versions", null));
        Assert.Equal(2, started.GetProperty("draftVersion").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await s.Tenant.Admin.PostAsync($"/api/v1/tenant/assessments/{id}/versions", null)).StatusCode);

        await AddAsync(s, id, Choice("Three"));
        // While the new version is a draft, learners still get the current one.
        Assert.Equal(2, QuestionsOf(await StartAsync(s.Learner, id)).Count);

        var detail = await ReadAsync(await s.Tenant.Admin.GetAsync($"/api/v1/tenant/assessments/{id}"));
        Assert.True(detail.GetProperty("editingDraftVersion").GetBoolean());
        Assert.Equal(3, detail.GetProperty("questions").GetArrayLength());

        Assert.True((await PublishAsync(s, id)).IsSuccessStatusCode);
        Assert.Equal(2, (await ReadAsync(await s.Tenant.Admin.GetAsync($"/api/v1/tenant/assessments/{id}"))).GetProperty("assessment").GetProperty("currentVersion").GetInt32());

        // A new attempt gets the new version; the finished one is untouched.
        var third = await StartAsync(s.Learner, id);
        Assert.Equal(3, QuestionsOf(third).Count);
        var old = await ReadAsync(await s.Learner.Client.GetAsync($"/api/v1/tenant/assessment-attempts/{AttemptId(first)}"));
        Assert.Equal(2, QuestionsOf(old).Count);
        Assert.Equal(100m, old.GetProperty("attempt").GetProperty("percentage").GetDecimal());
    }

    [Fact]
    public async Task A_new_version_can_be_discarded_and_changes_to_it_never_touch_the_current_one()
    {
        var s = await NewSetupAsync();
        var id = await NewAssessmentAsync(s);
        var keep = await AddAsync(s, id, Choice("Keep"));
        await PublishAsync(s, id);
        await s.Tenant.Admin.PostAsync($"/api/v1/tenant/assessments/{id}/versions", null);

        var draft = await ReadAsync(await s.Tenant.Admin.GetAsync($"/api/v1/tenant/assessments/{id}"));
        var copyId = draft.GetProperty("questions")[0].GetProperty("id").GetGuid();
        Assert.NotEqual(keep, copyId);   // the draft version edits its own copy
        Assert.Equal(HttpStatusCode.OK, (await s.Tenant.Admin.PutAsJsonAsync($"/api/v1/tenant/assessments/{id}/questions/{copyId}", Choice("Changed"))).StatusCode);
        Assert.Equal("Keep", QuestionsOf(await StartAsync(s.Learner, id))[0].GetProperty("prompt").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await s.Tenant.Admin.DeleteAsync($"/api/v1/tenant/assessments/{id}/versions/draft")).StatusCode);
        var after = await ReadAsync(await s.Tenant.Admin.GetAsync($"/api/v1/tenant/assessments/{id}"));
        Assert.False(after.GetProperty("editingDraftVersion").GetBoolean());
        Assert.Equal("Keep", after.GetProperty("questions")[0].GetProperty("prompt").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await s.Tenant.Admin.DeleteAsync($"/api/v1/tenant/assessments/{id}/versions/draft")).StatusCode);
    }

    [Fact]
    public async Task Questions_of_a_draft_can_be_edited_and_removed_and_the_rest_renumber()
    {
        var s = await NewSetupAsync();
        var id = await NewAssessmentAsync(s);
        var a = await AddAsync(s, id, Choice("A"));
        await AddAsync(s, id, Choice("B"));
        await AddAsync(s, id, Choice("C"));
        Assert.Equal(HttpStatusCode.OK, (await s.Tenant.Admin.PutAsJsonAsync($"/api/v1/tenant/assessments/{id}/questions/{a}", Choice("A2", 4))).StatusCode);
        var second = (await ReadAsync(await s.Tenant.Admin.GetAsync($"/api/v1/tenant/assessments/{id}"))).GetProperty("questions")[0];
        Assert.Equal(("A2", 4), (second.GetProperty("prompt").GetString(), second.GetProperty("points").GetInt32()));

        Assert.Equal(HttpStatusCode.NoContent, (await s.Tenant.Admin.DeleteAsync($"/api/v1/tenant/assessments/{id}/questions/{a}")).StatusCode);
        var rest = (await ReadAsync(await s.Tenant.Admin.GetAsync($"/api/v1/tenant/assessments/{id}"))).GetProperty("questions").EnumerateArray().ToList();
        Assert.Equal(new[] { 1, 2 }, rest.Select(item => item.GetProperty("displayOrder").GetInt32()).ToArray());
        Assert.Equal(new[] { "B", "C" }, rest.Select(item => item.GetProperty("prompt").GetString()).ToArray());
        Assert.Equal(HttpStatusCode.NotFound, (await s.Tenant.Admin.DeleteAsync($"/api/v1/tenant/assessments/{id}/questions/{a}")).StatusCode);
    }

    [Fact]
    public async Task Learners_never_see_the_questions_before_an_attempt_and_cannot_list_attempts()
    {
        var s = await NewSetupAsync();
        var id = await NewAssessmentAsync(s);
        await AddAsync(s, id, Choice("Secret"));
        await PublishAsync(s, id);
        var learnerView = await ReadAsync(await s.Learner.Client.GetAsync($"/api/v1/tenant/assessments/{id}"));
        Assert.Equal(0, learnerView.GetProperty("questions").GetArrayLength());
        Assert.Equal(1, learnerView.GetProperty("assessment").GetProperty("questionCount").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Learner.Client.GetAsync($"/api/v1/tenant/assessments/{id}/attempts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Learner.Client.PutAsJsonAsync($"/api/v1/tenant/assessments/{id}/questions/{Guid.NewGuid()}", Choice("x"))).StatusCode);
    }

    [Fact]
    public async Task Settings_can_be_changed_and_are_checked()
    {
        var s = await NewSetupAsync();
        var id = await NewAssessmentAsync(s);
        var saved = await ReadAsync(await s.Tenant.Admin.PutAsJsonAsync($"/api/v1/tenant/assessments/{id}", new { title = " Renamed ", timeLimitMinutes = 20, attemptLimit = 2, shuffleQuestions = true, shuffleOptions = true }));
        Assert.Equal("Renamed", saved.GetProperty("title").GetString());
        Assert.True(saved.GetProperty("shuffleQuestions").GetBoolean());
        Assert.Equal(20, saved.GetProperty("timeLimitMinutes").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Tenant.Admin.PutAsJsonAsync($"/api/v1/tenant/assessments/{id}", new { title = "", attemptLimit = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Tenant.Admin.PutAsJsonAsync($"/api/v1/tenant/assessments/{id}", new { title = "x", attemptLimit = 99 })).StatusCode);
    }

    // ---------- pools and shuffling ----------
    [Fact]
    public async Task An_attempt_draws_the_pools_draw_count_and_every_question_outside_a_pool()
    {
        var s = await NewSetupAsync();
        var id = await NewAssessmentAsync(s, new { title = "Pooled", attemptLimit = 5 });
        await AddAsync(s, id, Choice("Always", 2));
        for (var index = 1; index <= 4; index++) await AddAsync(s, id, Choice($"Pool {index}", 3, "Algebra"));
        Assert.Equal(HttpStatusCode.OK, (await s.Tenant.Admin.PutAsJsonAsync($"/api/v1/tenant/assessments/{id}/pools/algebra", new { drawCount = 2 })).StatusCode);
        Assert.True((await PublishAsync(s, id)).IsSuccessStatusCode);

        var detail = await ReadAsync(await s.Tenant.Admin.GetAsync($"/api/v1/tenant/assessments/{id}"));
        var pool = Assert.Single(detail.GetProperty("pools").EnumerateArray());
        Assert.Equal((2, 4), (pool.GetProperty("drawCount").GetInt32(), pool.GetProperty("questionCount").GetInt32()));
        Assert.Equal(3, detail.GetProperty("assessment").GetProperty("questionCount").GetInt32());   // 1 fixed + 2 drawn

        var seen = new HashSet<string>();
        for (var round = 0; round < 4; round++)
        {
            var attempt = await StartAsync(s.Learner, id);
            var prompts = QuestionsOf(attempt).Select(item => item.GetProperty("prompt").GetString()!).ToList();
            Assert.Equal(3, prompts.Count);
            Assert.Contains("Always", prompts);
            Assert.Equal(2, prompts.Count(item => item.StartsWith("Pool")));
            Assert.Equal(prompts.Count, prompts.Distinct().Count());
            Assert.Equal(8, attempt.GetProperty("attempt").GetProperty("possiblePoints").GetInt32());   // 2 + 3 + 3
            foreach (var prompt in prompts.Where(item => item.StartsWith("Pool"))) seen.Add(prompt);
            await AnswerAllRightAsync(s.Learner, attempt);
        }
        Assert.True(seen.Count >= 3, "four attempts should have drawn more than the same two questions");
    }

    [Fact]
    public async Task A_pool_must_be_fair_and_possible_before_it_can_be_published()
    {
        var s = await NewSetupAsync();
        var uneven = await NewAssessmentAsync(s, new { title = "Uneven" });
        await AddAsync(s, uneven, Choice("Small", 1, "Mixed"));
        await AddAsync(s, uneven, Choice("Big", 5, "mixed"));   // same pool, spelled differently
        var unevenDetail = await ReadAsync(await s.Tenant.Admin.GetAsync($"/api/v1/tenant/assessments/{uneven}"));
        Assert.Single(unevenDetail.GetProperty("pools").EnumerateArray());
        var refused = await PublishAsync(s, uneven);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("same points", await refused.Content.ReadAsStringAsync());

        var tooMany = await NewAssessmentAsync(s, new { title = "Too many" });
        await AddAsync(s, tooMany, Choice("Only", 1, "Tiny"));
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Tenant.Admin.PutAsJsonAsync($"/api/v1/tenant/assessments/{tooMany}/pools/Tiny", new { drawCount = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Tenant.Admin.PutAsJsonAsync($"/api/v1/tenant/assessments/{tooMany}/pools/Tiny", new { drawCount = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Tenant.Admin.PutAsJsonAsync($"/api/v1/tenant/assessments/{tooMany}/pools/Nothing", new { drawCount = 1 })).StatusCode);
    }

    [Fact]
    public async Task Removing_the_last_question_of_a_pool_removes_the_pool()
    {
        var s = await NewSetupAsync();
        var id = await NewAssessmentAsync(s);
        var pooled = await AddAsync(s, id, Choice("In pool", 1, "Solo"));
        await AddAsync(s, id, Choice("Outside"));
        Assert.Single((await ReadAsync(await s.Tenant.Admin.GetAsync($"/api/v1/tenant/assessments/{id}"))).GetProperty("pools").EnumerateArray());
        await s.Tenant.Admin.DeleteAsync($"/api/v1/tenant/assessments/{id}/questions/{pooled}");
        Assert.Empty((await ReadAsync(await s.Tenant.Admin.GetAsync($"/api/v1/tenant/assessments/{id}"))).GetProperty("pools").EnumerateArray());
    }

    [Fact]
    public async Task Shuffling_changes_the_order_of_questions_and_options_but_grading_still_works()
    {
        var s = await NewSetupAsync();
        var id = await NewAssessmentAsync(s, new { title = "Shuffled", attemptLimit = 3, shuffleQuestions = true, shuffleOptions = true });
        for (var index = 1; index <= 12; index++)
            await AddAsync(s, id, new { type = "MultipleChoice", prompt = $"Q{index:00}", options = new[] { "A1", "B2", "C3", "D4", "E5", "F6", "G7", "H8" }, correctAnswers = new[] { "A1" }, points = 1 });
        await PublishAsync(s, id);

        var attempt = await StartAsync(s.Learner, id);
        var questions = QuestionsOf(attempt);
        var prompts = questions.Select(item => item.GetProperty("prompt").GetString()).ToArray();
        Assert.Equal(12, prompts.Distinct().Count());
        Assert.NotEqual(prompts.OrderBy(item => item).ToArray(), prompts);   // 1 chance in 12! of being in order
        var options = questions[0].GetProperty("options").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Equal(8, options.Length);
        Assert.Equal(new[] { 1, 2, 3 }, questions.Take(3).Select(item => item.GetProperty("displayOrder").GetInt32()).ToArray());   // numbered in the order shown
        Assert.True(questions.Any(item => !item.GetProperty("options").EnumerateArray().Select(option => option.GetString()).SequenceEqual(new[] { "A1", "B2", "C3", "D4", "E5", "F6", "G7", "H8" })));

        foreach (var question in questions)
            (await s.Learner.Client.PutAsJsonAsync($"/api/v1/tenant/assessment-attempts/{AttemptId(attempt)}/answers/{question.GetProperty("id").GetGuid()}", new { answers = new[] { "A1" } })).EnsureSuccessStatusCode();
        var graded = await ReadAsync(await s.Learner.Client.PostAsync($"/api/v1/tenant/assessment-attempts/{AttemptId(attempt)}/submit", null));
        Assert.Equal(100m, graded.GetProperty("attempt").GetProperty("percentage").GetDecimal());

        // Re-opening the attempt shows the same order the learner saw.
        var again = await ReadAsync(await s.Learner.Client.GetAsync($"/api/v1/tenant/assessment-attempts/{AttemptId(attempt)}"));
        Assert.Equal(prompts, QuestionsOf(again).Select(item => item.GetProperty("prompt").GetString()).ToArray());
    }

    [Fact]
    public async Task Without_shuffling_questions_keep_their_authored_order()
    {
        var s = await NewSetupAsync();
        var id = await NewAssessmentAsync(s);
        for (var index = 1; index <= 6; index++) await AddAsync(s, id, Choice($"Q{index}"));
        await PublishAsync(s, id);
        Assert.Equal(new[] { "Q1", "Q2", "Q3", "Q4", "Q5", "Q6" }, QuestionsOf(await StartAsync(s.Learner, id)).Select(item => item.GetProperty("prompt").GetString()).ToArray());
    }

    [Fact]
    public async Task A_typed_short_answer_counts_when_it_matches_any_accepted_answer()
    {
        var s = await NewSetupAsync();
        var id = await NewAssessmentAsync(s);
        await AddAsync(s, id, new { type = "ShortAnswer", prompt = "Capital of Nepal?", correctAnswers = new[] { "Kathmandu", "Katmandu" }, points = 2 });
        await PublishAsync(s, id);
        var attempt = await StartAsync(s.Learner, id);
        var question = QuestionsOf(attempt)[0].GetProperty("id").GetGuid();
        (await s.Learner.Client.PutAsJsonAsync($"/api/v1/tenant/assessment-attempts/{AttemptId(attempt)}/answers/{question}", new { text = "  katmandu " })).EnsureSuccessStatusCode();
        var graded = await ReadAsync(await s.Learner.Client.PostAsync($"/api/v1/tenant/assessment-attempts/{AttemptId(attempt)}/submit", null));
        Assert.Equal(2, graded.GetProperty("attempt").GetProperty("scorePoints").GetInt32());
    }

    // ---------- accommodations ----------
    [Fact]
    public async Task A_learner_with_an_accommodation_gets_extra_time_and_attempts()
    {
        var s = await NewSetupAsync();
        var other = await _world.AddLearnerAsync(s.Tenant, "Otto");
        Assert.True((await EnrollAsync(other, s.Course)).IsSuccessStatusCode);
        var id = await NewAssessmentAsync(s, new { title = "Timed", timeLimitMinutes = 10, attemptLimit = 1 });
        await AddAsync(s, id, Choice("One"));
        await PublishAsync(s, id);

        var set = await ReadAsync(await s.Tenant.Admin.PutAsJsonAsync($"/api/v1/tenant/courses/{s.Course.Id}/accommodations/{s.Learner.Id}", new { extraTimePercent = 50, extraAttempts = 1, note = "Learning support plan" }));
        Assert.Equal(50, set.GetProperty("extraTimePercent").GetInt32());

        var view = await ReadAsync(await s.Learner.Client.GetAsync($"/api/v1/tenant/assessments/{id}"));
        Assert.Equal(15, view.GetProperty("effectiveTimeLimitMinutes").GetInt32());
        Assert.Equal(2, view.GetProperty("effectiveAttemptLimit").GetInt32());
        Assert.False(view.GetProperty("accommodation").TryGetProperty("note", out _));   // the learner is not shown the staff note

        var attempt = await StartAsync(s.Learner, id);
        Assert.Equal(15, attempt.GetProperty("timeLimitMinutes").GetInt32());
        var started = attempt.GetProperty("attempt").GetProperty("startedAtUtc").GetDateTimeOffset();
        Assert.Equal(15, Math.Round((attempt.GetProperty("expiresAtUtc").GetDateTimeOffset() - started).TotalMinutes));

        Assert.Equal(HttpStatusCode.Created, (await s.Learner.Client.PostAsync($"/api/v1/tenant/assessments/{id}/attempts", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Learner.Client.PostAsync($"/api/v1/tenant/assessments/{id}/attempts", null)).StatusCode);

        // Someone without one gets the ordinary limits.
        var ordinary = await StartAsync(other, id);
        Assert.Equal(10, ordinary.GetProperty("timeLimitMinutes").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await other.Client.PostAsync($"/api/v1/tenant/assessments/{id}/attempts", null)).StatusCode);
    }

    [Fact]
    public async Task Accommodations_are_listed_changed_removed_and_audited_for_staff_only()
    {
        var s = await NewSetupAsync();
        var url = $"/api/v1/tenant/courses/{s.Course.Id}/accommodations";
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Learner.Client.PutAsJsonAsync($"{url}/{s.Learner.Id}", new { extraTimePercent = 100, extraAttempts = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Learner.Client.GetAsync(url)).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await s.Tenant.Admin.PutAsJsonAsync($"{url}/{s.Learner.Id}", new { extraTimePercent = 500, extraAttempts = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Tenant.Admin.PutAsJsonAsync($"{url}/{s.Learner.Id}", new { extraTimePercent = 10, extraAttempts = 11 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Tenant.Admin.PutAsJsonAsync($"{url}/{Guid.NewGuid()}", new { extraTimePercent = 10, extraAttempts = 0 })).StatusCode);

        Assert.True((await s.Tenant.Admin.PutAsJsonAsync($"{url}/{s.Learner.Id}", new { extraTimePercent = 25, extraAttempts = 0, note = "Dyslexia" })).IsSuccessStatusCode);
        Assert.True((await s.Tenant.Admin.PutAsJsonAsync($"{url}/{s.Learner.Id}", new { extraTimePercent = 100, extraAttempts = 2 })).IsSuccessStatusCode);
        var row = Assert.Single((await ReadAsync(await s.Tenant.Admin.GetAsync(url))).EnumerateArray());
        Assert.Equal(("Lena", 100, 2), (row.GetProperty("learnerName").GetString(), row.GetProperty("extraTimePercent").GetInt32(), row.GetProperty("extraAttempts").GetInt32()));

        var audit = (await ReadAsync(await s.Tenant.Admin.GetAsync("/api/v1/tenant/security/audit-events"))).EnumerateArray().Select(item => item.GetProperty("action").GetString()).ToList();
        Assert.Equal(2, audit.Count(action => action == "assessment.accommodation.set"));

        // Zero extra time and zero extra attempts is the same as none.
        Assert.Equal(HttpStatusCode.NoContent, (await s.Tenant.Admin.PutAsJsonAsync($"{url}/{s.Learner.Id}", new { extraTimePercent = 0, extraAttempts = 0 })).StatusCode);
        Assert.Empty((await ReadAsync(await s.Tenant.Admin.GetAsync(url))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await s.Tenant.Admin.DeleteAsync($"{url}/{s.Learner.Id}")).StatusCode);
    }

    // ---------- rubrics ----------
    private static object Rubric(string name = "Essay rubric") => new
    {
        name,
        criteria = new object[]
        {
            new { name = "Argument", description = "Is it convincing?", levels = new object[] { new { label = "Strong", points = 6 }, new { label = "Fair", points = 3 }, new { label = "Weak", points = 0 } } },
            new { name = "Grammar", levels = new object[] { new { label = "Clean", points = 4 }, new { label = "Errors", points = 1 } } },
        }
    };

    private static async Task<JsonElement> CreateRubricAsync(Setup s, object? body = null)
    {
        var response = await s.Tenant.Admin.PostAsJsonAsync($"/api/v1/tenant/courses/{s.Course.Id}/rubrics", body ?? Rubric());
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await ReadAsync(response);
    }

    [Fact]
    public async Task A_rubric_is_checked_and_totalled()
    {
        var s = await NewSetupAsync();
        var rubric = await CreateRubricAsync(s);
        Assert.Equal(10, rubric.GetProperty("totalPoints").GetInt32());   // 6 + 4
        Assert.Equal(2, rubric.GetProperty("criteria").GetArrayLength());
        Assert.Equal(6, rubric.GetProperty("criteria")[0].GetProperty("levels")[0].GetProperty("points").GetInt32());   // best level first
        Assert.Equal(HttpStatusCode.Conflict, (await s.Tenant.Admin.PostAsJsonAsync($"/api/v1/tenant/courses/{s.Course.Id}/rubrics", Rubric())).StatusCode);

        var url = $"/api/v1/tenant/courses/{s.Course.Id}/rubrics";
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Tenant.Admin.PostAsJsonAsync(url, new { name = "", criteria = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Tenant.Admin.PostAsJsonAsync(url, new { name = "No criteria", criteria = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Tenant.Admin.PostAsJsonAsync(url, new { name = "One level", criteria = new[] { new { name = "C", levels = new[] { new { label = "Only", points = 3 } } } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Tenant.Admin.PostAsJsonAsync(url, new { name = "Same points", criteria = new[] { new { name = "C", levels = new[] { new { label = "A", points = 3 }, new { label = "B", points = 3 } } } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Tenant.Admin.PostAsJsonAsync(url, new { name = "Too big", criteria = new[] { new { name = "C", levels = new[] { new { label = "A", points = 90 }, new { label = "B", points = 0 } } }, new { name = "D", levels = new[] { new { label = "A", points = 90 }, new { label = "B", points = 0 } } } } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Learner.Client.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task A_rubric_nobody_uses_can_be_edited_or_deleted_but_a_used_one_is_frozen()
    {
        var s = await NewSetupAsync();
        var free = await CreateRubricAsync(s, Rubric("Free"));
        var freeUrl = $"/api/v1/tenant/rubrics/{free.GetProperty("id").GetGuid()}";
        var edited = await ReadAsync(await s.Tenant.Admin.PutAsJsonAsync(freeUrl, Rubric("Free renamed")));
        Assert.Equal("Free renamed", edited.GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await s.Tenant.Admin.DeleteAsync(freeUrl)).StatusCode);

        var used = await CreateRubricAsync(s, Rubric("Used"));
        var usedUrl = $"/api/v1/tenant/rubrics/{used.GetProperty("id").GetGuid()}";
        var id = await NewAssessmentAsync(s);
        await AddAsync(s, id, new { type = "Essay", prompt = "Discuss", rubricId = used.GetProperty("id").GetGuid() });
        Assert.Equal(HttpStatusCode.Conflict, (await s.Tenant.Admin.PutAsJsonAsync(usedUrl, Rubric("Used"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Tenant.Admin.DeleteAsync(usedUrl)).StatusCode);
        var listed = Assert.Single((await ReadAsync(await s.Tenant.Admin.GetAsync($"/api/v1/tenant/courses/{s.Course.Id}/rubrics"))).EnumerateArray());
        Assert.Equal(1, listed.GetProperty("usedByQuestions").GetInt32());
    }

    [Fact]
    public async Task A_rubric_question_is_worth_the_rubrics_total_and_only_essay_questions_take_one()
    {
        var s = await NewSetupAsync();
        var rubricId = (await CreateRubricAsync(s)).GetProperty("id").GetGuid();
        var id = await NewAssessmentAsync(s);
        await AddAsync(s, id, new { type = "Essay", prompt = "Discuss", points = 3, rubricId });
        var question = (await ReadAsync(await s.Tenant.Admin.GetAsync($"/api/v1/tenant/assessments/{id}"))).GetProperty("questions")[0];
        Assert.Equal(10, question.GetProperty("points").GetInt32());
        Assert.Equal("Essay rubric", question.GetProperty("rubricName").GetString());

        var wrongType = await s.Tenant.Admin.PostAsJsonAsync($"/api/v1/tenant/assessments/{id}/questions", new { type = "MultipleChoice", prompt = "x", options = new[] { "a", "b" }, correctAnswers = new[] { "a" }, rubricId });
        Assert.Equal(HttpStatusCode.BadRequest, wrongType.StatusCode);
        var missing = await s.Tenant.Admin.PostAsJsonAsync($"/api/v1/tenant/assessments/{id}/questions", new { type = "Essay", prompt = "x", rubricId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }

    private sealed record Graded(JsonElement Attempt, Guid QuestionId, JsonElement Rubric);

    private async Task<(Setup S, Guid AssessmentId, JsonElement Submitted, Guid QuestionId, JsonElement Rubric)> SubmittedEssayAsync()
    {
        var s = await NewSetupAsync();
        var rubric = await CreateRubricAsync(s);
        var id = await NewAssessmentAsync(s);
        await AddAsync(s, id, Choice("Warm-up", 2));
        await AddAsync(s, id, new { type = "Essay", prompt = "Discuss", rubricId = rubric.GetProperty("id").GetGuid() });
        await PublishAsync(s, id);
        var attempt = await StartAsync(s.Learner, id);
        var essay = QuestionsOf(attempt).Single(item => item.GetProperty("type").GetString() == "Essay");
        Assert.Equal(2, essay.GetProperty("rubric").GetProperty("criteria").GetArrayLength());   // learners see how they will be marked
        var essayId = essay.GetProperty("id").GetGuid();
        foreach (var question in QuestionsOf(attempt).Where(item => item.GetProperty("type").GetString() == "MultipleChoice"))
            (await s.Learner.Client.PutAsJsonAsync($"/api/v1/tenant/assessment-attempts/{AttemptId(attempt)}/answers/{question.GetProperty("id").GetGuid()}", new { answers = new[] { "right" } })).EnsureSuccessStatusCode();
        (await s.Learner.Client.PutAsJsonAsync($"/api/v1/tenant/assessment-attempts/{AttemptId(attempt)}/answers/{essayId}", new { text = "My considered essay." })).EnsureSuccessStatusCode();
        var submitted = await ReadAsync(await s.Learner.Client.PostAsync($"/api/v1/tenant/assessment-attempts/{AttemptId(attempt)}/submit", null));
        Assert.Equal("Submitted", submitted.GetProperty("attempt").GetProperty("status").GetString());
        return (s, id, submitted, essayId, rubric);
    }

    [Fact]
    public async Task A_grader_sees_the_essay_text_and_scores_each_criterion()
    {
        var (s, _, submitted, essayId, rubric) = await SubmittedEssayAsync();
        var attemptId = AttemptId(submitted);
        var seen = await ReadAsync(await s.Tenant.Admin.GetAsync($"/api/v1/tenant/assessment-attempts/{attemptId}"));
        Assert.Equal("My considered essay.", QuestionsOf(seen).Single(item => item.GetProperty("id").GetGuid() == essayId).GetProperty("text").GetString());

        var criteria = rubric.GetProperty("criteria").EnumerateArray().ToList();
        var argument = criteria[0].GetProperty("id").GetGuid();
        var grammar = criteria[1].GetProperty("id").GetGuid();
        var url = $"/api/v1/tenant/assessment-attempts/{attemptId}/grade";

        // Too many points for a criterion, a missing criterion and an unknown one are all refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Tenant.Admin.PostAsJsonAsync(url, new { scorePoints = 0, answers = new[] { new { questionId = essayId, criterionScores = new[] { new { criterionId = argument, points = 7 }, new { criterionId = grammar, points = 1 } } } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Tenant.Admin.PostAsJsonAsync(url, new { scorePoints = 0, answers = new[] { new { questionId = essayId, criterionScores = new[] { new { criterionId = argument, points = 3 } } } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Tenant.Admin.PostAsJsonAsync(url, new { scorePoints = 0, answers = new[] { new { questionId = essayId, criterionScores = new[] { new { criterionId = Guid.NewGuid(), points = 3 } } } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Tenant.Admin.PostAsJsonAsync(url, new { scorePoints = 0, answers = Array.Empty<object>() })).StatusCode);

        var graded = await ReadAsync(await s.Tenant.Admin.PostAsJsonAsync(url, new
        {
            scorePoints = 0, feedback = "Well argued.",
            answers = new[] { new { questionId = essayId, criterionScores = new[] { new { criterionId = argument, points = 3 }, new { criterionId = grammar, points = 4 } }, feedback = "Check the second paragraph." } }
        }));
        Assert.Equal("Graded", graded.GetProperty("status").GetString());
        Assert.Equal(9, graded.GetProperty("scorePoints").GetInt32());   // 2 (warm-up) + 3 + 4
        Assert.Equal(12, graded.GetProperty("possiblePoints").GetInt32());
        Assert.Equal(75m, graded.GetProperty("percentage").GetDecimal());

        // The learner now sees each criterion's score and the feedback.
        var mine = await ReadAsync(await s.Learner.Client.GetAsync($"/api/v1/tenant/assessment-attempts/{attemptId}"));
        var essay = QuestionsOf(mine).Single(item => item.GetProperty("id").GetGuid() == essayId);
        Assert.Equal(new[] { 3, 4 }, essay.GetProperty("rubricScores").EnumerateArray().Select(item => item.GetProperty("points").GetInt32()).ToArray());
        Assert.Equal("Check the second paragraph.", essay.GetProperty("feedback").GetString());
        Assert.Equal("Well argued.", mine.GetProperty("teacherFeedback").GetString());
    }

    [Fact]
    public async Task Only_graders_and_the_learner_can_open_an_attempt()
    {
        var (s, _, submitted, _, _) = await SubmittedEssayAsync();
        var classmate = await _world.AddLearnerAsync(s.Tenant, "Cleo");
        var url = $"/api/v1/tenant/assessment-attempts/{AttemptId(submitted)}";
        Assert.Equal(HttpStatusCode.Forbidden, (await classmate.Client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Learner.Client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await classmate.Client.PostAsJsonAsync($"{url}/grade", new { scorePoints = 1 })).StatusCode);
    }

    [Fact]
    public async Task Essay_and_file_questions_can_also_be_graded_with_plain_points_per_question()
    {
        var s = await NewSetupAsync();
        var id = await NewAssessmentAsync(s);
        await AddAsync(s, id, new { type = "Essay", prompt = "Discuss", points = 8 });
        await PublishAsync(s, id);
        var attempt = await StartAsync(s.Learner, id);
        var essay = QuestionsOf(attempt)[0].GetProperty("id").GetGuid();
        (await s.Learner.Client.PutAsJsonAsync($"/api/v1/tenant/assessment-attempts/{AttemptId(attempt)}/answers/{essay}", new { text = "Words" })).EnsureSuccessStatusCode();
        await s.Learner.Client.PostAsync($"/api/v1/tenant/assessment-attempts/{AttemptId(attempt)}/submit", null);
        var url = $"/api/v1/tenant/assessment-attempts/{AttemptId(attempt)}/grade";
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Tenant.Admin.PostAsJsonAsync(url, new { scorePoints = 0, answers = new[] { new { questionId = essay, scorePoints = 9 } } })).StatusCode);
        var graded = await ReadAsync(await s.Tenant.Admin.PostAsJsonAsync(url, new { scorePoints = 0, answers = new[] { new { questionId = essay, scorePoints = 6 } } }));
        Assert.Equal((6, 75m), (graded.GetProperty("scorePoints").GetInt32(), graded.GetProperty("percentage").GetDecimal()));
    }

    [Fact]
    public async Task The_simple_single_total_grade_still_works()
    {
        var s = await NewSetupAsync();
        var id = await NewAssessmentAsync(s);
        await AddAsync(s, id, new { type = "Essay", prompt = "Discuss", points = 10 });
        await PublishAsync(s, id);
        var attempt = await StartAsync(s.Learner, id);
        await s.Learner.Client.PostAsync($"/api/v1/tenant/assessment-attempts/{AttemptId(attempt)}/submit", null);
        var graded = await ReadAsync(await s.Tenant.Admin.PostAsJsonAsync($"/api/v1/tenant/assessment-attempts/{AttemptId(attempt)}/grade", new { scorePoints = 7, feedback = "Fine" }));
        Assert.Equal((7, 70m), (graded.GetProperty("scorePoints").GetInt32(), graded.GetProperty("percentage").GetDecimal()));
        var list = Assert.Single((await ReadAsync(await s.Tenant.Admin.GetAsync($"/api/v1/tenant/assessments/{id}/attempts"))).EnumerateArray());
        Assert.Equal("Lena", list.GetProperty("learnerName").GetString());
    }

    // ---------- file answers ----------
    private static MultipartFormDataContent FileContent(string name, string text)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        content.Add(file, "file", name);
        return content;
    }

    private async Task<(Setup S, Guid AssessmentId, JsonElement Attempt, Guid FileQuestion, Guid TextQuestion)> FileAttemptAsync()
    {
        var s = await NewSetupAsync();
        var id = await NewAssessmentAsync(s);
        await AddAsync(s, id, new { type = "FileUpload", prompt = "Upload your report", points = 10 });
        await AddAsync(s, id, new { type = "Essay", prompt = "Explain", points = 5 });
        await PublishAsync(s, id);
        var attempt = await StartAsync(s.Learner, id);
        var questions = QuestionsOf(attempt);
        return (s, id, attempt, questions.Single(item => item.GetProperty("type").GetString() == "FileUpload").GetProperty("id").GetGuid(), questions.Single(item => item.GetProperty("type").GetString() == "Essay").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_learner_attaches_a_file_and_the_grader_downloads_it()
    {
        var (s, _, attempt, fileQuestion, textQuestion) = await FileAttemptAsync();
        var url = $"/api/v1/tenant/assessment-attempts/{AttemptId(attempt)}/answers/{fileQuestion}/file";
        var uploaded = await ReadAsync(await s.Learner.Client.PostAsync(url, FileContent("report.txt", "the report body")));
        Assert.Equal("report.txt", uploaded.GetProperty("fileName").GetString());

        // Saving other answers afterwards does not drop the file.
        (await s.Learner.Client.PutAsJsonAsync($"/api/v1/tenant/assessment-attempts/{AttemptId(attempt)}/answers/{fileQuestion}", new { text = "A note for the grader" })).EnsureSuccessStatusCode();
        var reopened = await ReadAsync(await s.Learner.Client.GetAsync($"/api/v1/tenant/assessment-attempts/{AttemptId(attempt)}"));
        var shown = QuestionsOf(reopened).Single(item => item.GetProperty("id").GetGuid() == fileQuestion);
        Assert.Equal("report.txt", shown.GetProperty("file").GetProperty("fileName").GetString());
        Assert.Equal("A note for the grader", shown.GetProperty("text").GetString());

        (await s.Learner.Client.PostAsync($"/api/v1/tenant/assessment-attempts/{AttemptId(attempt)}/submit", null)).EnsureSuccessStatusCode();
        var download = await s.Tenant.Admin.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("the report body", await download.Content.ReadAsStringAsync());
        Assert.Equal("report.txt", download.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal(HttpStatusCode.OK, (await s.Learner.Client.GetAsync(url)).StatusCode);

        var graded = await ReadAsync(await s.Tenant.Admin.PostAsJsonAsync($"/api/v1/tenant/assessment-attempts/{AttemptId(attempt)}/grade",
            new { scorePoints = 0, answers = new[] { new { questionId = fileQuestion, scorePoints = 9 }, new { questionId = textQuestion, scorePoints = 4 } } }));
        Assert.Equal(13, graded.GetProperty("scorePoints").GetInt32());
    }

    [Fact]
    public async Task File_uploads_are_restricted_by_question_type_size_file_type_and_owner()
    {
        var (s, _, attempt, fileQuestion, textQuestion) = await FileAttemptAsync();
        var baseUrl = $"/api/v1/tenant/assessment-attempts/{AttemptId(attempt)}/answers";
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Learner.Client.PostAsync($"{baseUrl}/{textQuestion}/file", FileContent("a.txt", "x"))).StatusCode);   // not a file question
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Learner.Client.PostAsync($"{baseUrl}/{fileQuestion}/file", FileContent("virus.exe", "MZ"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Learner.Client.PostAsync($"{baseUrl}/{fileQuestion}/file", FileContent("page.html", "<script>"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Learner.Client.PostAsync($"{baseUrl}/{fileQuestion}/file", new MultipartFormDataContent())).StatusCode);   // no file
        Assert.Equal(HttpStatusCode.NotFound, (await s.Learner.Client.PostAsync($"{baseUrl}/{Guid.NewGuid()}/file", FileContent("a.txt", "x"))).StatusCode);

        Assert.True((await s.Learner.Client.PostAsync($"{baseUrl}/{fileQuestion}/file", FileContent("ok.txt", "fine"))).IsSuccessStatusCode);
        var classmate = await _world.AddLearnerAsync(s.Tenant, "Cleo");
        Assert.Equal(HttpStatusCode.NotFound, (await classmate.Client.GetAsync($"{baseUrl}/{fileQuestion}/file")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await classmate.Client.PostAsync($"{baseUrl}/{fileQuestion}/file", FileContent("a.txt", "x"))).StatusCode);
    }

    [Fact]
    public async Task A_file_can_be_replaced_or_removed_until_the_attempt_is_submitted()
    {
        var (s, _, attempt, fileQuestion, _) = await FileAttemptAsync();
        var url = $"/api/v1/tenant/assessment-attempts/{AttemptId(attempt)}/answers/{fileQuestion}/file";
        await s.Learner.Client.PostAsync(url, FileContent("first.txt", "one"));
        await s.Learner.Client.PostAsync(url, FileContent("second.txt", "two"));
        var download = await s.Learner.Client.GetAsync(url);
        Assert.Equal("two", await download.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await s.Learner.Client.DeleteAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Learner.Client.GetAsync(url)).StatusCode);

        await s.Learner.Client.PostAsync(url, FileContent("third.txt", "three"));
        await s.Learner.Client.PostAsync($"/api/v1/tenant/assessment-attempts/{AttemptId(attempt)}/submit", null);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Learner.Client.PostAsync(url, FileContent("late.txt", "late"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Learner.Client.DeleteAsync(url)).StatusCode);
        Assert.Equal("three", await (await s.Learner.Client.GetAsync(url)).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Other_organizations_cannot_reach_any_of_it()
    {
        var s = await NewSetupAsync();
        var other = await NewSetupAsync("Olga");
        var id = await NewAssessmentAsync(s);
        await AddAsync(s, id, Choice("One"));
        await PublishAsync(s, id);
        var rubric = await CreateRubricAsync(s);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Tenant.Admin.GetAsync($"/api/v1/tenant/assessments/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Tenant.Admin.PostAsync($"/api/v1/tenant/assessments/{id}/versions", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Tenant.Admin.PutAsJsonAsync($"/api/v1/tenant/rubrics/{rubric.GetProperty("id").GetGuid()}", Rubric("Taken"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Tenant.Admin.DeleteAsync($"/api/v1/tenant/rubrics/{rubric.GetProperty("id").GetGuid()}")).StatusCode);
        Assert.Empty((await ReadAsync(await other.Tenant.Admin.GetAsync($"/api/v1/tenant/courses/{s.Course.Id}/rubrics"))).EnumerateArray());
    }
}
