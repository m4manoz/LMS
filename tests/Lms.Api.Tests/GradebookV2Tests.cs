using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lms.Api.Domain.Gradebook;

namespace Lms.Api.Tests;

public sealed class GradebookV2Tests : IClassFixture<LmsApiFactory>
{
    private readonly LmsApiFactory _factory;
    public GradebookV2Tests(LmsApiFactory factory) => _factory = factory;

    private sealed record Setup(string Slug, HttpClient Admin, HttpClient Learner, Guid CourseId);

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<Guid> NewCourseAsync(HttpClient admin, string code)
    {
        var course = await ReadAsync(await admin.PostAsJsonAsync("/api/v1/tenant/courses", new { code, title = $"Course {code}" }));
        var id = course.GetProperty("course").GetProperty("id").GetGuid();
        (await admin.PostAsync($"/api/v1/tenant/courses/{id}/submit-review", null)).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/api/v1/tenant/courses/{id}/publish", null)).EnsureSuccessStatusCode();
        return id;
    }

    private async Task<Setup> CreateAsync()
    {
        var (slug, _, adminToken) = await _factory.ProvisionTenantWithAdminAsync();
        var admin = _factory.CreateTenantClient(slug, adminToken);
        var courseId = await NewCourseAsync(admin, "GRD-1");
        var email = $"lena@{slug}.test";
        (await admin.PostAsJsonAsync("/api/v1/tenant/users", new { email, displayName = "Lena", password = LmsApiFactory.AdminPassword, roleCode = "LEARNER" })).EnsureSuccessStatusCode();
        var learner = _factory.CreateTenantClient(slug, await _factory.LoginAsync(slug, email, LmsApiFactory.AdminPassword));
        (await learner.PostAsync($"/api/v1/tenant/courses/{courseId}/enroll", null)).EnsureSuccessStatusCode();
        return new Setup(slug, admin, learner, courseId);
    }

    private static async Task<Guid> AssignmentAsync(HttpClient admin, Guid courseId, string title, int max)
    {
        var created = await ReadAsync(await admin.PostAsJsonAsync("/api/v1/tenant/assignments", new { courseId, title, maxPoints = max }));
        var id = created.GetProperty("id").GetGuid();
        (await admin.PostAsync($"/api/v1/tenant/assignments/{id}/publish", null)).EnsureSuccessStatusCode();
        return id;
    }

    private static async Task GradeAsync(Setup s, Guid assignmentId, decimal score)
    {
        var form = new MultipartFormDataContent { { new StringContent("answer"), "text" } };
        var submission = await ReadAsync(await s.Learner.PostAsync($"/api/v1/tenant/assignments/{assignmentId}/submission", form));
        (await s.Admin.PostAsJsonAsync($"/api/v1/tenant/assignments/submissions/{submission.GetProperty("id").GetGuid()}/grade", new { scorePoints = score, feedback = "ok" })).EnsureSuccessStatusCode();
    }

    private static Task<HttpResponseMessage> SaveSettings(Setup s, object body) => s.Admin.PutAsJsonAsync($"/api/v1/tenant/gradebook/courses/{s.CourseId}/settings", body);

    private static async Task<(Guid Homework, Guid Project)> TwoCategoriesAsync(Setup s)
    {
        var saved = await ReadAsync(await SaveSettings(s, new { passPercent = 50, categories = new object[] { new { name = "Homework", weightPercent = 30 }, new { name = "Project", weightPercent = 70 } } }));
        var categories = saved.GetProperty("categories");
        return (categories[0].GetProperty("id").GetGuid(), categories[1].GetProperty("id").GetGuid());
    }

    private static Task<HttpResponseMessage> Assign(Setup s, string kind, Guid itemId, Guid? categoryId)
        => s.Admin.PutAsJsonAsync($"/api/v1/tenant/gradebook/courses/{s.CourseId}/items/category", new { itemKind = kind, itemId, categoryId });

    private static async Task<JsonElement> BookAsync(Setup s) => await ReadAsync(await s.Admin.GetAsync($"/api/v1/tenant/gradebook/courses/{s.CourseId}"));

    // ---------- pure scale logic ----------
    [Fact]
    public void Scale_lookup_picks_the_highest_band_reached()
    {
        Assert.Equal("A", GradeScales.Lookup(GradeScales.BuiltIn, 90)!.Label);
        Assert.Equal("B", GradeScales.Lookup(GradeScales.BuiltIn, 89.99m)!.Label);
        Assert.Equal("F", GradeScales.Lookup(GradeScales.BuiltIn, 0)!.Label);
        Assert.Equal("A", GradeScales.Lookup(GradeScales.BuiltIn, 100)!.Label);
        Assert.Null(GradeScales.Lookup(GradeScales.BuiltIn, null));
    }

    [Fact]
    public void Scale_validation_catches_unusable_bands()
    {
        Assert.NotEmpty(GradeScales.Validate(null));
        Assert.NotEmpty(GradeScales.Validate([]));
        Assert.Empty(GradeScales.Validate(GradeScales.BuiltIn));
        Assert.Contains("lowest band", GradeScales.Validate([new(50, "Pass", null), new(70, "Merit", null)])[0]);
        Assert.Contains("same percentage", GradeScales.Validate([new(0, "F", null), new(0, "G", null)])[0]);
        Assert.Contains("different", GradeScales.Validate([new(0, "A", null), new(50, "a", null)])[0]);
        Assert.Contains("label", GradeScales.Validate([new(0, "", null)])[0]);
        Assert.Contains("between 0 and 100", GradeScales.Validate([new(0, "F", null), new(101, "A", null)])[0]);
        Assert.Contains("at most 12", GradeScales.Validate(Enumerable.Range(0, 13).Select(i => new GradeBand(i * 5, $"G{i}", null)).ToList())[0]);
    }

    // ---------- scales API ----------
    [Fact]
    public async Task The_built_in_scale_is_listed_until_an_organization_default_exists()
    {
        var s = await CreateAsync();
        var before = await ReadAsync(await s.Admin.GetAsync("/api/v1/tenant/gradebook/scales"));
        Assert.True(before[0].GetProperty("builtIn").GetBoolean());
        Assert.True(before[0].GetProperty("isDefault").GetBoolean());

        var created = await s.Admin.PostAsJsonAsync("/api/v1/tenant/gradebook/scales", new { name = "Honours", isDefault = true, bands = new[] { new { minPercent = 0, label = "Fail" }, new { minPercent = 50, label = "Pass" }, new { minPercent = 80, label = "Distinction" } } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var after = await ReadAsync(await s.Admin.GetAsync("/api/v1/tenant/gradebook/scales"));
        Assert.Equal("Honours", after[0].GetProperty("name").GetString());
        Assert.True(after[0].GetProperty("isDefault").GetBoolean());
        Assert.False(after.EnumerateArray().Single(x => x.GetProperty("builtIn").GetBoolean()).GetProperty("isDefault").GetBoolean());
        Assert.Equal("Distinction", after[0].GetProperty("bands")[0].GetProperty("label").GetString()); // highest first
    }

    [Fact]
    public async Task Scale_requests_are_validated_and_only_one_scale_is_the_default()
    {
        var s = await CreateAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Admin.PostAsJsonAsync("/api/v1/tenant/gradebook/scales", new { name = "", bands = new[] { new { minPercent = 0, label = "F" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Admin.PostAsJsonAsync("/api/v1/tenant/gradebook/scales", new { name = "No zero", bands = new[] { new { minPercent = 50, label = "P" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Admin.PostAsJsonAsync("/api/v1/tenant/gradebook/scales", new { name = "Empty", bands = Array.Empty<object>() })).StatusCode);

        var bands = new[] { new { minPercent = 0, label = "F" }, new { minPercent = 60, label = "P" } };
        Assert.Equal(HttpStatusCode.Created, (await s.Admin.PostAsJsonAsync("/api/v1/tenant/gradebook/scales", new { name = "One", isDefault = true, bands })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Admin.PostAsJsonAsync("/api/v1/tenant/gradebook/scales", new { name = "one", bands })).StatusCode); // duplicate name
        var two = await ReadAsync(await s.Admin.PostAsJsonAsync("/api/v1/tenant/gradebook/scales", new { name = "Two", isDefault = true, bands }));

        var all = await ReadAsync(await s.Admin.GetAsync("/api/v1/tenant/gradebook/scales"));
        var defaults = all.EnumerateArray().Where(x => x.GetProperty("isDefault").GetBoolean()).ToList();
        Assert.Single(defaults);
        Assert.Equal(two.GetProperty("id").GetGuid(), defaults[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_scale_in_use_cannot_be_deleted_and_an_unused_one_can()
    {
        var s = await CreateAsync();
        var bands = new[] { new { minPercent = 0, label = "F" }, new { minPercent = 60, label = "P" } };
        var used = (await ReadAsync(await s.Admin.PostAsJsonAsync("/api/v1/tenant/gradebook/scales", new { name = "Used", bands }))).GetProperty("id").GetGuid();
        var spare = (await ReadAsync(await s.Admin.PostAsJsonAsync("/api/v1/tenant/gradebook/scales", new { name = "Spare", bands }))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await SaveSettings(s, new { gradeScaleId = used, passPercent = 60 })).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await s.Admin.DeleteAsync($"/api/v1/tenant/gradebook/scales/{used}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Admin.DeleteAsync($"/api/v1/tenant/gradebook/scales/{spare}")).StatusCode);
    }

    [Fact]
    public async Task Learners_can_read_scales_but_not_change_any_grading_setup()
    {
        var s = await CreateAsync();
        Assert.Equal(HttpStatusCode.OK, (await s.Learner.GetAsync("/api/v1/tenant/gradebook/scales")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Learner.PostAsJsonAsync("/api/v1/tenant/gradebook/scales", new { name = "X", bands = new[] { new { minPercent = 0, label = "F" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Learner.GetAsync($"/api/v1/tenant/gradebook/courses/{s.CourseId}/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Learner.PutAsJsonAsync($"/api/v1/tenant/gradebook/courses/{s.CourseId}/settings", new { passPercent = 10 })).StatusCode);
    }

    // ---------- course settings ----------
    [Fact]
    public async Task Category_weights_must_add_up_to_one_hundred()
    {
        var s = await CreateAsync();
        var bad = await SaveSettings(s, new { passPercent = 50, categories = new object[] { new { name = "A", weightPercent = 60 }, new { name = "B", weightPercent = 30 } } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("100", (await ReadAsync(bad)).GetProperty("message").GetString());
        Assert.Equal(HttpStatusCode.OK, (await SaveSettings(s, new { passPercent = 50, categories = new object[] { new { name = "A", weightPercent = 66.67 }, new { name = "B", weightPercent = 33.33 } } })).StatusCode);
    }

    [Theory]
    [InlineData("same")]
    [InlineData("blank")]
    [InlineData("zero")]
    [InlineData("toomany")]
    public async Task Invalid_category_sets_are_rejected(string problem)
    {
        var s = await CreateAsync();
        object[] categories = problem switch
        {
            "same" => [new { name = "Quiz", weightPercent = 50 }, new { name = "quiz", weightPercent = 50 }],
            "blank" => [new { name = " ", weightPercent = 100 }],
            "zero" => [new { name = "A", weightPercent = 100 }, new { name = "B", weightPercent = 0 }],
            _ => Enumerable.Range(0, 11).Select(i => (object)new { name = $"C{i}", weightPercent = i == 0 ? 10 : 9 }).ToArray(),
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await SaveSettings(s, new { passPercent = 50, categories })).StatusCode);
    }

    [Fact]
    public async Task Pass_mark_and_scale_are_validated()
    {
        var s = await CreateAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await SaveSettings(s, new { passPercent = 101 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SaveSettings(s, new { passPercent = -1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SaveSettings(s, new { passPercent = 50, gradeScaleId = Guid.NewGuid() })).StatusCode);
    }

    [Fact]
    public async Task Removing_a_category_frees_its_items_and_other_courses_categories_cannot_be_used()
    {
        var s = await CreateAsync();
        var work = await AssignmentAsync(s.Admin, s.CourseId, "Essay", 10);
        var (homework, _) = await TwoCategoriesAsync(s);
        Assert.Equal(HttpStatusCode.NoContent, (await Assign(s, "assignment", work, homework)).StatusCode);

        var other = await NewCourseAsync(s.Admin, "OTHER-1");
        var foreign = await ReadAsync(await s.Admin.PutAsJsonAsync($"/api/v1/tenant/gradebook/courses/{other}/settings", new { passPercent = 50, categories = new object[] { new { name = "Only", weightPercent = 100 } } }));
        var foreignCategory = foreign.GetProperty("categories")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await Assign(s, "assignment", work, foreignCategory)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SaveSettings(s, new { passPercent = 50, categories = new object[] { new { id = foreignCategory, name = "Stolen", weightPercent = 100 } } })).StatusCode);

        // Replace the set: the old categories are gone, and so is the item's membership.
        Assert.Equal(HttpStatusCode.OK, (await SaveSettings(s, new { passPercent = 50, categories = new object[] { new { name = "Everything", weightPercent = 100 } } })).StatusCode);
        var settings = await ReadAsync(await s.Admin.GetAsync($"/api/v1/tenant/gradebook/courses/{s.CourseId}/settings"));
        Assert.Equal(1, settings.GetProperty("categories").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("items").EnumerateArray().Single().GetProperty("categoryId").ValueKind);
    }

    [Fact]
    public async Task Items_can_be_moved_between_categories_and_unassigned_with_checks()
    {
        var s = await CreateAsync();
        var work = await AssignmentAsync(s.Admin, s.CourseId, "Essay", 10);
        var (homework, project) = await TwoCategoriesAsync(s);

        Assert.Equal(HttpStatusCode.BadRequest, (await Assign(s, "worksheet", work, homework)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Assign(s, "assignment", Guid.NewGuid(), homework)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Assign(s, "assignment", work, homework)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Assign(s, "assignment", work, project)).StatusCode);
        var moved = await ReadAsync(await s.Admin.GetAsync($"/api/v1/tenant/gradebook/courses/{s.CourseId}/settings"));
        Assert.Equal(project, moved.GetProperty("items")[0].GetProperty("categoryId").GetGuid());
        Assert.Equal(HttpStatusCode.NoContent, (await Assign(s, "assignment", work, null)).StatusCode);
        var cleared = await ReadAsync(await s.Admin.GetAsync($"/api/v1/tenant/gradebook/courses/{s.CourseId}/settings"));
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("items")[0].GetProperty("categoryId").ValueKind);
    }

    // ---------- the arithmetic ----------
    [Fact]
    public async Task Without_categories_the_grade_is_still_total_points_with_a_letter_and_pass_flag()
    {
        var s = await CreateAsync();
        await GradeAsync(s, await AssignmentAsync(s.Admin, s.CourseId, "One", 10), 10);
        await GradeAsync(s, await AssignmentAsync(s.Admin, s.CourseId, "Two", 90), 45);

        var learner = (await BookAsync(s)).GetProperty("learners")[0];
        Assert.Equal(55m, learner.GetProperty("overallPercent").GetDecimal());
        Assert.Equal("F", learner.GetProperty("letter").GetString());   // 55 is below the built-in D
        Assert.True(learner.GetProperty("passed").GetBoolean());        // default pass mark is 50
        Assert.False((await BookAsync(s)).GetProperty("weighted").GetBoolean());
    }

    [Fact]
    public async Task Categories_weight_the_overall_grade_instead_of_raw_points()
    {
        var s = await CreateAsync();
        var small = await AssignmentAsync(s.Admin, s.CourseId, "Worksheet", 10);
        var large = await AssignmentAsync(s.Admin, s.CourseId, "Big project", 100);
        var (homework, project) = await TwoCategoriesAsync(s);
        await Assign(s, "assignment", small, homework);
        await Assign(s, "assignment", large, project);
        await GradeAsync(s, small, 10);   // Homework 100%
        await GradeAsync(s, large, 50);   // Project 50%

        var book = await BookAsync(s);
        var learner = book.GetProperty("learners")[0];
        Assert.True(book.GetProperty("weighted").GetBoolean());
        Assert.Equal(65m, learner.GetProperty("overallPercent").GetDecimal()); // 0.3 * 100 + 0.7 * 50, not 60/110
        Assert.Equal(100m, learner.GetProperty("categoryPercents")[0].GetDecimal());
        Assert.Equal(50m, learner.GetProperty("categoryPercents")[1].GetDecimal());
        Assert.Equal("D", learner.GetProperty("letter").GetString());
        Assert.Equal(60m, learner.GetProperty("earnedPoints").GetDecimal()); // raw points still reported
    }

    [Fact]
    public async Task A_category_with_no_graded_work_yet_does_not_drag_the_grade_down()
    {
        var s = await CreateAsync();
        var small = await AssignmentAsync(s.Admin, s.CourseId, "Worksheet", 10);
        var large = await AssignmentAsync(s.Admin, s.CourseId, "Big project", 100);
        var (homework, project) = await TwoCategoriesAsync(s);
        await Assign(s, "assignment", small, homework);
        await Assign(s, "assignment", large, project);

        await GradeAsync(s, small, 8); // only Homework is graded so far
        var early = (await BookAsync(s)).GetProperty("learners")[0];
        Assert.Equal(80m, early.GetProperty("overallPercent").GetDecimal());
        Assert.Equal(JsonValueKind.Null, early.GetProperty("categoryPercents")[1].ValueKind);

        await GradeAsync(s, large, 100);
        var later = (await BookAsync(s)).GetProperty("learners")[0];
        Assert.Equal(94m, later.GetProperty("overallPercent").GetDecimal()); // 0.3 * 80 + 0.7 * 100
    }

    [Fact]
    public async Task Items_outside_every_category_are_not_counted_while_categories_are_in_use()
    {
        var s = await CreateAsync();
        var counted = await AssignmentAsync(s.Admin, s.CourseId, "Counted", 10);
        var loose = await AssignmentAsync(s.Admin, s.CourseId, "Not in a category", 10);
        var (homework, _) = await TwoCategoriesAsync(s);
        await Assign(s, "assignment", counted, homework);
        await GradeAsync(s, counted, 9);
        await GradeAsync(s, loose, 0);

        var book = await BookAsync(s);
        Assert.Equal(90m, book.GetProperty("learners")[0].GetProperty("overallPercent").GetDecimal());
        var items = book.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(JsonValueKind.Null, items.Single(i => i.GetProperty("title").GetString() == "Not in a category").GetProperty("categoryId").ValueKind);
    }

    [Fact]
    public async Task A_course_scale_overrides_the_default_and_the_pass_mark_decides_pass_or_fail()
    {
        var s = await CreateAsync();
        await s.Admin.PostAsJsonAsync("/api/v1/tenant/gradebook/scales", new { name = "Default words", isDefault = true, bands = new[] { new { minPercent = 0, label = "Low" }, new { minPercent = 50, label = "High" } } });
        var course = (await ReadAsync(await s.Admin.PostAsJsonAsync("/api/v1/tenant/gradebook/scales", new { name = "Course scale", bands = new[] { new { minPercent = 0, label = "Fail" }, new { minPercent = 50, label = "Merit" }, new { minPercent = 90, label = "Distinction" } } }))).GetProperty("id").GetGuid();
        await GradeAsync(s, await AssignmentAsync(s.Admin, s.CourseId, "Essay", 100), 65);

        // Organization default first.
        Assert.Equal("High", (await BookAsync(s)).GetProperty("learners")[0].GetProperty("letter").GetString());
        // The course picks its own scale and a stricter pass mark.
        await SaveSettings(s, new { gradeScaleId = course, passPercent = 70 });
        var learner = (await BookAsync(s)).GetProperty("learners")[0];
        Assert.Equal("Merit", learner.GetProperty("letter").GetString());
        Assert.False(learner.GetProperty("passed").GetBoolean());
        Assert.Equal("Course scale", (await BookAsync(s)).GetProperty("scaleName").GetString());
    }

    [Fact]
    public async Task Learners_see_their_letter_pass_result_and_category_breakdown()
    {
        var s = await CreateAsync();
        var work = await AssignmentAsync(s.Admin, s.CourseId, "Essay", 10);
        var exam = await AssignmentAsync(s.Admin, s.CourseId, "Final", 100);
        var (homework, project) = await TwoCategoriesAsync(s);
        await Assign(s, "assignment", work, homework);
        await Assign(s, "assignment", exam, project);
        await GradeAsync(s, work, 10);
        await GradeAsync(s, exam, 80);

        var mine = (await ReadAsync(await s.Learner.GetAsync("/api/v1/tenant/gradebook/me")))[0];
        Assert.True(mine.GetProperty("weighted").GetBoolean());
        Assert.Equal(86m, mine.GetProperty("overallPercent").GetDecimal()); // 0.3 * 100 + 0.7 * 80
        Assert.Equal("B", mine.GetProperty("letter").GetString());
        Assert.True(mine.GetProperty("passed").GetBoolean());
        var breakdown = mine.GetProperty("categories");
        Assert.Equal("Homework", breakdown[0].GetProperty("name").GetString());
        Assert.Equal(70m, breakdown[1].GetProperty("weightPercent").GetDecimal());
        Assert.Equal("Homework", mine.GetProperty("rows")[0].GetProperty("categoryName").GetString());
    }

    [Fact]
    public async Task The_csv_export_includes_category_percentages_and_the_grade()
    {
        var s = await CreateAsync();
        var work = await AssignmentAsync(s.Admin, s.CourseId, "Essay", 10);
        var (homework, _) = await TwoCategoriesAsync(s);
        await Assign(s, "assignment", work, homework);
        await GradeAsync(s, work, 10);

        var csv = await (await s.Admin.GetAsync($"/api/v1/tenant/gradebook/courses/{s.CourseId}/export.csv")).Content.ReadAsStringAsync();
        Assert.Contains("\"Homework (30%)\"", csv);
        Assert.Contains("\"Project (70%)\"", csv);
        Assert.Contains("\"Grade\"", csv);
        Assert.Contains("\"Pass\"", csv);
        Assert.Contains("\"A\"", csv);
    }

    [Fact]
    public async Task Grading_setup_is_isolated_between_tenants()
    {
        var a = await CreateAsync();
        var b = await CreateAsync();
        await a.Admin.PostAsJsonAsync("/api/v1/tenant/gradebook/scales", new { name = "Only in A", isDefault = true, bands = new[] { new { minPercent = 0, label = "X" } } });
        var scalesB = await ReadAsync(await b.Admin.GetAsync("/api/v1/tenant/gradebook/scales"));
        Assert.DoesNotContain(scalesB.EnumerateArray(), item => item.GetProperty("name").GetString() == "Only in A");
        Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.GetAsync($"/api/v1/tenant/gradebook/courses/{a.CourseId}/settings")).StatusCode);
    }
}
