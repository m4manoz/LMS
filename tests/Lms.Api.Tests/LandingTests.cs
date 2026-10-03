using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lms.Api.Domain.Courses;
using Lms.Api.Domain.Landing;
using Lms.Api.Infrastructure.Landing;
using Microsoft.EntityFrameworkCore;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

public sealed class LandingContentRulesTests
{
    [Theory]
    [InlineData("#courses", true)]
    [InlineData("/?org=acme", true)]
    [InlineData("https://example.org/page", true)]
    [InlineData("mailto:help@example.org", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html;base64,AAAA", false)]
    [InlineData("http://example.org", false)]
    [InlineData("//evil.example.org", false)]
    [InlineData("/\\evil", false)]
    [InlineData("https://user:pw@example.org", false)]
    [InlineData("#", false)]
    [InlineData("", false)]
    [InlineData("courses", false)]
    public void Only_safe_links_are_accepted(string link, bool safe) => Assert.Equal(safe, LandingContentRules.IsSafeLink(link));

    [Fact]
    public void The_default_page_is_valid_and_survives_saving_unchanged()
    {
        var defaults = LandingDefaults.Create("Acme");
        var normalized = LandingContentRules.Normalize(defaults, out var error);
        Assert.Null(error);
        Assert.NotNull(normalized);
        Assert.Equal(defaults.Banners.Count, normalized!.Banners.Count);
        Assert.Equal(defaults.Faq.Count, normalized.Faq.Count);
        Assert.Contains("Acme", normalized.Hero.Subtitle);
    }

    [Fact]
    public void Stored_text_that_cannot_be_read_gives_the_default_page()
    {
        Assert.Equal("Learn without limits", LandingContentRules.Read("{ not json", "Acme").Hero.Title);
        Assert.Equal("Learn without limits", LandingContentRules.Read(null, "Acme").Hero.Title);
        Assert.Equal("Learn without limits", LandingContentRules.Read("{\"hero\":{\"title\":\"\",\"subtitle\":\"\",\"primaryLabel\":\"\",\"primaryLink\":\"\",\"searchPlaceholder\":\"\"}}", "Acme").Hero.Title);   // an unusable page
    }
}

/// <summary>The public front page, applying for a course, the content staff control, and deciding on applications.</summary>
public sealed class LandingTests : IClassFixture<LmsApiFactory>
{
    private readonly LmsApiFactory _factory;
    private readonly TestWorld _world;
    public LandingTests(LmsApiFactory factory) { _factory = factory; _world = new TestWorld(factory); }

    private HttpClient Visitor() => _factory.CreateClient();   // no sign-in and no organization header
    private static string Public(Tenant t, string tail = "") => $"/api/v1/public/{t.Slug}{tail}";
    private async Task<JsonElement> LandingAsync(Tenant t) => await ReadAsync(await Visitor().GetAsync(Public(t, "/landing")));
    private static List<string> Titles(JsonElement landing) => landing.GetProperty("courses").EnumerateArray().Select(item => item.GetProperty("title").GetString()!).ToList();

    private async Task<Guid> AddCategoryAsync(Tenant t, CourseInfo course, string name)
    {
        var id = Guid.NewGuid();
        await _world.WithDbAsync(t.Slug, async db =>
        {
            var tenantId = (await db.Courses.SingleAsync(item => item.Id == course.Id)).TenantId;
            db.CourseCategories.Add(new CourseCategory { Id = id, TenantId = tenantId, Name = name, Slug = name.ToLowerInvariant() });
            (await db.Courses.SingleAsync(item => item.Id == course.Id)).CategoryId = id;
            await db.SaveChangesAsync();
        });
        return id;
    }

    // ---------- the public page ----------
    [Fact]
    public async Task A_visitor_sees_the_default_page_for_an_organization_that_has_not_written_one_and_only_its_published_courses()
    {
        var t = await _world.NewTenantAsync();
        var landing = await LandingAsync(t);
        Assert.Equal(t.Slug, landing.GetProperty("organization").GetProperty("slug").GetString());
        var content = landing.GetProperty("content");
        Assert.Equal("Learn without limits", content.GetProperty("hero").GetProperty("title").GetString());
        Assert.True(content.GetProperty("banners").GetArrayLength() >= 1);
        Assert.Empty(landing.GetProperty("courses").EnumerateArray());
        Assert.Empty(landing.GetProperty("rows").EnumerateArray());                       // rows with nothing to show are left out

        await _world.NewCourseAsync(t, "PUB-1");
        await _world.NewCourseAsync(t, "DRAFT-1", publish: false);
        Assert.Equal(1, (await LandingAsync(t)).GetProperty("courses").GetArrayLength());
    }

    [Fact]
    public async Task Courses_show_a_short_summary_the_category_the_teacher_and_the_seats_left()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "CARD-1", capacity: 3);
        var learner = await _world.AddLearnerAsync(t, "Ada");
        Assert.True((await EnrollAsync(learner, course)).IsSuccessStatusCode);
        await AddCategoryAsync(t, course, "Science");
        await _world.WithDbAsync(t.Slug, async db => { var item = await db.Courses.SingleAsync(c => c.Id == course.Id); item.Description = "Line one.\n\n   Line   two. " + new string('x', 300); await db.SaveChangesAsync(); });

        var card = (await LandingAsync(t)).GetProperty("courses")[0];
        Assert.Equal(course.Title, card.GetProperty("title").GetString());
        Assert.Equal("CARD-1", card.GetProperty("code").GetString());
        Assert.Equal("Science", card.GetProperty("category").GetString());
        Assert.Equal(2, card.GetProperty("seatsLeft").GetInt32());
        Assert.False(string.IsNullOrEmpty(card.GetProperty("teacher").GetString()));
        var summary = card.GetProperty("summary").GetString()!;
        Assert.StartsWith("Line one. Line two.", summary);                              // whitespace tidied
        Assert.True(summary.Length <= 180);
        Assert.EndsWith("…", summary);
        var categories = (await LandingAsync(t)).GetProperty("categories").EnumerateArray().ToList();
        Assert.Equal(("Science", 1), (categories[0].GetProperty("name").GetString(), categories[0].GetProperty("courses").GetInt32()));
    }

    [Fact]
    public async Task Rows_pick_the_most_popular_a_category_or_chosen_courses_in_order()
    {
        var t = await _world.NewTenantAsync();
        var quiet = await _world.NewCourseAsync(t, "QUIET");
        var busy = await _world.NewCourseAsync(t, "BUSY");
        var third = await _world.NewCourseAsync(t, "THIRD");
        foreach (var name in new[] { "Ada", "Ben" }) Assert.True((await EnrollAsync(await _world.AddLearnerAsync(t, name), busy)).IsSuccessStatusCode);
        var science = await AddCategoryAsync(t, third, "Science");
        var rows = new[]
        {
            new { id = "a", title = "Popular", subtitle = (string?)null, mode = "popular", categoryId = (Guid?)null, courseIds = Array.Empty<Guid>(), limit = 2 },
            new { id = "b", title = "Science", subtitle = (string?)null, mode = "category", categoryId = (Guid?)science, courseIds = Array.Empty<Guid>(), limit = 8 },
            new { id = "c", title = "Picked", subtitle = (string?)"By hand", mode = "manual", categoryId = (Guid?)null, courseIds = new[] { quiet.Id, Guid.NewGuid(), third.Id }, limit = 8 },
        };
        var current = (await ReadAsync(await t.Admin.GetAsync("/api/v1/tenant/landing"))).GetProperty("content").Clone();
        var edited = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(current.GetRawText())!;
        var body = edited.ToDictionary(pair => pair.Key, pair => (object)pair.Value);
        body["rows"] = rows;
        Assert.Equal(HttpStatusCode.OK, (await t.Admin.PutAsJsonAsync("/api/v1/tenant/landing", body)).StatusCode);

        var landing = await LandingAsync(t);
        var titles = landing.GetProperty("courses").EnumerateArray().ToDictionary(item => item.GetProperty("id").GetGuid(), item => item.GetProperty("code").GetString()!);
        List<string> Row(string name) => landing.GetProperty("rows").EnumerateArray().Single(item => item.GetProperty("title").GetString() == name).GetProperty("courseIds").EnumerateArray().Select(id => titles[id.GetGuid()]).ToList();
        Assert.Equal("BUSY", Row("Popular")[0]);                 // the course with the most learners comes first
        Assert.Equal(2, Row("Popular").Count);                   // and the row stops at its limit
        Assert.Equal(["THIRD"], Row("Science"));
        Assert.Equal(["QUIET", "THIRD"], Row("Picked"));         // in the order chosen; a course that does not exist is skipped
        Assert.Equal("By hand", landing.GetProperty("rows").EnumerateArray().Single(item => item.GetProperty("title").GetString() == "Picked").GetProperty("subtitle").GetString());
    }

    [Fact]
    public async Task An_unknown_organization_is_not_found_the_name_is_not_case_sensitive_and_organizations_are_kept_apart()
    {
        var t = await _world.NewTenantAsync();
        var other = await _world.NewTenantAsync();
        await _world.NewCourseAsync(t, "ONLY-T");
        Assert.Equal(HttpStatusCode.NotFound, (await Visitor().GetAsync("/api/v1/public/no-such-org/landing")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Visitor().GetAsync($"/api/v1/public/{t.Slug.ToUpperInvariant()}/landing")).StatusCode);
        Assert.Empty((await LandingAsync(other)).GetProperty("courses").EnumerateArray());
        Assert.Single((await LandingAsync(t)).GetProperty("courses").EnumerateArray());
    }

    [Fact]
    public async Task A_single_organization_install_can_name_the_organization_to_show_by_default()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Visitor().GetAsync("/api/v1/public/organizations/default")).StatusCode);   // nothing configured on this host

        using var factory = new LmsApiFactory { ExtraSettings = new() { ["Public:DefaultTenantSlug"] = "the-default" } };
        var platform = factory.CreateClient();
        platform.DefaultRequestHeaders.Add("X-Platform-Key", LmsApiFactory.PlatformKey);
        Assert.True((await platform.PostAsJsonAsync("/api/v1/platform/tenants", new { name = "The Default School", slug = "the-default" })).IsSuccessStatusCode);
        var found = await ReadAsync(await factory.CreateClient().GetAsync("/api/v1/public/organizations/default"));
        Assert.Equal(("the-default", "The Default School"), (found.GetProperty("slug").GetString(), found.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Course_details_show_the_outline_and_never_a_draft_or_another_organizations_course()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "DET-1", modules: 2, lessonsPerModule: 2);
        var draft = await _world.NewCourseAsync(t, "DET-DRAFT", publish: false);
        var detail = await ReadAsync(await Visitor().GetAsync(Public(t, $"/courses/{course.Id}")));
        Assert.Equal("DET-1", detail.GetProperty("code").GetString());
        var modules = detail.GetProperty("modules").EnumerateArray().ToList();
        Assert.Equal(2, modules.Count);
        Assert.Equal(2, modules[0].GetProperty("lessons").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await Visitor().GetAsync(Public(t, $"/courses/{draft.Id}"))).StatusCode);
        var other = await _world.NewTenantAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await Visitor().GetAsync(Public(other, $"/courses/{course.Id}"))).StatusCode);
    }

    // ---------- applying ----------
    private static object Application(string name = "Nina Newcomer", string email = "nina@example.org", string? phone = "9800000000", string? message = "I would like to join.", string? website = null) => new { fullName = name, email, phone, message, website };

    [Fact]
    public async Task A_visitor_applies_without_an_account_and_staff_see_the_application()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "APP-1");
        var response = await Visitor().PostAsJsonAsync(Public(t, $"/courses/{course.Id}/applications"), Application(email: "  Nina@Example.ORG "));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains("received your application", await response.Content.ReadAsStringAsync());

        var listed = (await ReadAsync(await t.Admin.GetAsync("/api/v1/tenant/applications"))).EnumerateArray().ToList();
        var item = Assert.Single(listed);
        Assert.Equal("Nina Newcomer", item.GetProperty("fullName").GetString());
        Assert.Equal("nina@example.org", item.GetProperty("email").GetString());                    // tidied
        Assert.Equal("Pending", item.GetProperty("status").GetString());
        Assert.Equal(course.Title, item.GetProperty("courseTitle").GetString());
        Assert.Equal("I would like to join.", item.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Applying_again_adds_nothing_and_a_hidden_box_that_only_programs_fill_is_ignored()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "APP-2");
        var url = Public(t, $"/courses/{course.Id}/applications");
        Assert.Equal(HttpStatusCode.Accepted, (await Visitor().PostAsJsonAsync(url, Application())).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Visitor().PostAsJsonAsync(url, Application())).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Visitor().PostAsJsonAsync(url, Application(email: "bot@example.org", website: "http://spam.example"))).StatusCode);
        Assert.Single((await ReadAsync(await t.Admin.GetAsync("/api/v1/tenant/applications"))).EnumerateArray());
    }

    [Fact]
    public async Task Applications_are_checked_and_only_published_courses_of_a_real_organization_take_them()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "APP-3");
        var draft = await _world.NewCourseAsync(t, "APP-DRAFT", publish: false);
        var url = Public(t, $"/courses/{course.Id}/applications");
        foreach (var bad in new object[] { Application(name: "N"), Application(name: new string('n', 121)), Application(email: "not-an-email"), Application(email: ""), Application(phone: new string('9', 41)), Application(message: new string('m', 1001)) })
            Assert.Equal(HttpStatusCode.BadRequest, (await Visitor().PostAsJsonAsync(url, bad)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Visitor().PostAsJsonAsync(Public(t, $"/courses/{draft.Id}/applications"), Application())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Visitor().PostAsJsonAsync("/api/v1/public/no-such-org/courses/" + course.Id + "/applications", Application())).StatusCode);
        Assert.Empty((await ReadAsync(await t.Admin.GetAsync("/api/v1/tenant/applications"))).EnumerateArray());
    }

    [Fact]
    public async Task The_answer_does_not_reveal_whether_an_email_already_has_an_account()
    {
        var t = await _world.NewTenantAsync();
        var learner = await _world.AddLearnerAsync(t, "Ada");
        var course = await _world.NewCourseAsync(t, "APP-4");
        var known = await Visitor().PostAsJsonAsync(Public(t, $"/courses/{course.Id}/applications"), Application(email: learner.Email));
        var unknown = await Visitor().PostAsJsonAsync(Public(t, $"/courses/{course.Id}/applications"), Application(email: "stranger@example.org"));
        Assert.Equal(known.StatusCode, unknown.StatusCode);
        Assert.Equal(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
    }

    // ---------- deciding ----------
    [Fact]
    public async Task Approving_sends_an_invitation_that_staff_can_share_and_marks_the_application()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "DEC-1");
        await Visitor().PostAsJsonAsync(Public(t, $"/courses/{course.Id}/applications"), Application());
        var id = (await ReadAsync(await t.Admin.GetAsync("/api/v1/tenant/applications")))[0].GetProperty("id").GetGuid();

        var approved = await t.Admin.PostAsJsonAsync($"/api/v1/tenant/applications/{id}/approve", new { message = "Welcome!", expiresInDays = 14 });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var body = await ReadAsync(approved);
        Assert.Equal("Approved", body.GetProperty("application").GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("token").GetString()));                    // the code to share, shown once
        Assert.NotEqual(Guid.Empty, body.GetProperty("application").GetProperty("invitationId").GetGuid());

        var invitations = (await ReadAsync(await t.Admin.GetAsync("/api/v1/tenant/invitations"))).EnumerateArray().ToList();
        var invitation = Assert.Single(invitations);
        Assert.Equal("nina@example.org", invitation.GetProperty("email").GetString());
        Assert.Equal(course.Id, invitation.GetProperty("courseId").GetGuid());
        Assert.Equal("Approved", Assert.Single((await ReadAsync(await t.Admin.GetAsync("/api/v1/tenant/applications?status=approved"))).EnumerateArray()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Someone_who_already_has_an_account_is_told_in_the_app_and_one_already_enrolled_needs_nothing()
    {
        var t = await _world.NewTenantAsync();
        var learner = await _world.AddLearnerAsync(t, "Ada");
        var course = await _world.NewCourseAsync(t, "DEC-2");
        await Visitor().PostAsJsonAsync(Public(t, $"/courses/{course.Id}/applications"), Application(email: learner.Email));
        var id = (await ReadAsync(await t.Admin.GetAsync("/api/v1/tenant/applications")))[0].GetProperty("id").GetGuid();
        var first = await ReadAsync(await t.Admin.PostAsJsonAsync($"/api/v1/tenant/applications/{id}/approve", new { }));
        Assert.Equal("NotNeeded", first.GetProperty("emailStatus").GetString());                       // they hear about it in the app

        Assert.True((await EnrollAsync(learner, course)).IsSuccessStatusCode);
        await Visitor().PostAsJsonAsync(Public(t, $"/courses/{course.Id}/applications"), Application(email: learner.Email, name: "Ada Again"));
        var second = (await ReadAsync(await t.Admin.GetAsync("/api/v1/tenant/applications?status=pending")))[0].GetProperty("id").GetGuid();
        Assert.Equal("AlreadyEnrolled", (await ReadAsync(await t.Admin.PostAsJsonAsync($"/api/v1/tenant/applications/{second}/approve", new { }))).GetProperty("emailStatus").GetString());
    }

    [Fact]
    public async Task Declining_closes_the_application_and_an_approved_one_cannot_be_declined()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "DEC-3");
        await Visitor().PostAsJsonAsync(Public(t, $"/courses/{course.Id}/applications"), Application());
        await Visitor().PostAsJsonAsync(Public(t, $"/courses/{course.Id}/applications"), Application(email: "other@example.org"));
        var ids = (await ReadAsync(await t.Admin.GetAsync("/api/v1/tenant/applications"))).EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToList();
        var declined = await ReadAsync(await t.Admin.PostAsync($"/api/v1/tenant/applications/{ids[0]}/decline", null));
        Assert.Equal("Declined", declined.GetProperty("status").GetString());
        Assert.Empty((await ReadAsync(await t.Admin.GetAsync("/api/v1/tenant/invitations"))).EnumerateArray());
        await t.Admin.PostAsJsonAsync($"/api/v1/tenant/applications/{ids[1]}/approve", new { });
        Assert.Equal(HttpStatusCode.Conflict, (await t.Admin.PostAsync($"/api/v1/tenant/applications/{ids[1]}/decline", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t.Admin.PostAsync($"/api/v1/tenant/applications/{Guid.NewGuid()}/decline", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await t.Admin.PostAsJsonAsync($"/api/v1/tenant/applications/{ids[0]}/approve", new { expiresInDays = 400 })).StatusCode);
    }

    [Fact]
    public async Task Only_people_who_manage_enrollment_decide_and_applications_stay_inside_their_organization()
    {
        var t = await _world.NewTenantAsync();
        var learner = await _world.AddLearnerAsync(t, "Ada");
        var teacher = await _world.AddPersonAsync(t, "Tara", "TEACHER");
        var course = await _world.NewCourseAsync(t, "DEC-4");
        await Visitor().PostAsJsonAsync(Public(t, $"/courses/{course.Id}/applications"), Application());
        var id = (await ReadAsync(await teacher.Client.GetAsync("/api/v1/tenant/applications")))[0].GetProperty("id").GetGuid();          // a teacher manages enrollment
        Assert.Equal(HttpStatusCode.Forbidden, (await learner.Client.GetAsync("/api/v1/tenant/applications")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await learner.Client.PostAsync($"/api/v1/tenant/applications/{id}/decline", null)).StatusCode);
        var other = await _world.NewTenantAsync();
        Assert.Empty((await ReadAsync(await other.Admin.GetAsync("/api/v1/tenant/applications"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.PostAsync($"/api/v1/tenant/applications/{id}/decline", null)).StatusCode);
    }

    [Fact]
    public async Task An_application_for_a_course_that_is_no_longer_published_cannot_be_approved()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "DEC-5");
        await Visitor().PostAsJsonAsync(Public(t, $"/courses/{course.Id}/applications"), Application());
        var id = (await ReadAsync(await t.Admin.GetAsync("/api/v1/tenant/applications")))[0].GetProperty("id").GetGuid();
        await _world.WithDbAsync(t.Slug, async db => { (await db.Courses.SingleAsync(item => item.Id == course.Id)).Status = CourseStatus.Archived; await db.SaveChangesAsync(); });
        Assert.Equal(HttpStatusCode.Conflict, (await t.Admin.PostAsJsonAsync($"/api/v1/tenant/applications/{id}/approve", new { })).StatusCode);
    }

    // ---------- the content staff control ----------
    private static Dictionary<string, object> Edit(JsonElement content, Action<Dictionary<string, object>> change)
    {
        var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(content.GetRawText())!.ToDictionary(pair => pair.Key, pair => (object)pair.Value);
        change(body);
        return body;
    }

    private async Task<JsonElement> ContentAsync(Tenant t) => (await ReadAsync(await t.Admin.GetAsync("/api/v1/tenant/landing"))).GetProperty("content").Clone();

    [Fact]
    public async Task Staff_start_from_the_default_page_change_it_and_visitors_see_the_change_and_it_can_be_reset()
    {
        var t = await _world.NewTenantAsync();
        var first = await ReadAsync(await t.Admin.GetAsync("/api/v1/tenant/landing"));
        Assert.True(first.GetProperty("isDefault").GetBoolean());
        Assert.Equal(t.Slug, first.GetProperty("slug").GetString());
        Assert.Contains("dark", first.GetProperty("themes").EnumerateArray().Select(item => item.GetString()));

        var body = Edit(first.GetProperty("content"), content =>
        {
            content["hero"] = new { title = "  Study with us  ", subtitle = "Real classes.", primaryLabel = "See courses", primaryLink = "#courses", searchPlaceholder = "Search" };
            content["testimonials"] = new[] { new { name = "Sarah W.", role = "Data analyst", quote = "Flexible and practical." }, new { name = "", role = "", quote = "dropped: no name" } };
            content["faq"] = new[] { new { question = "Is it free to apply?", answer = "Yes." } };
        });
        var saved = await t.Admin.PutAsJsonAsync("/api/v1/tenant/landing", body);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.False((await ReadAsync(await t.Admin.GetAsync("/api/v1/tenant/landing"))).GetProperty("isDefault").GetBoolean());

        var page = (await LandingAsync(t)).GetProperty("content");
        Assert.Equal("Study with us", page.GetProperty("hero").GetProperty("title").GetString());                // trimmed
        Assert.Equal(1, page.GetProperty("testimonials").GetArrayLength());                                      // the empty one was dropped
        Assert.Equal("Sarah W.", page.GetProperty("testimonials")[0].GetProperty("name").GetString());
        Assert.Equal(1, page.GetProperty("faq").GetArrayLength());

        var reset = await ReadAsync(await t.Admin.PostAsync("/api/v1/tenant/landing/reset", null));
        Assert.True(reset.GetProperty("isDefault").GetBoolean());
        Assert.Equal("Learn without limits", (await LandingAsync(t)).GetProperty("content").GetProperty("hero").GetProperty("title").GetString());
    }

    [Fact]
    public async Task Unsafe_or_oversized_content_is_refused_with_a_reason_and_nothing_is_saved()
    {
        var t = await _world.NewTenantAsync();
        var content = await ContentAsync(t);
        async Task<string> Refused(Action<Dictionary<string, object>> change)
        {
            var response = await t.Admin.PutAsJsonAsync("/api/v1/tenant/landing", Edit(content, change));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return await response.Content.ReadAsStringAsync();
        }
        Assert.Contains("main heading", await Refused(body => body["hero"] = new { title = " ", subtitle = "", primaryLabel = "", primaryLink = "", searchPlaceholder = "" }));
        Assert.Contains("must start with #", await Refused(body => body["hero"] = new { title = "Hi", subtitle = "", primaryLabel = "Go", primaryLink = "javascript:alert(1)", searchPlaceholder = "" }));
        Assert.Contains("banner", await Refused(body => body["banners"] = new[] { new { id = "x", title = "Banner", text = "", buttonLabel = "Go", link = "data:text/html,x", theme = "blue" } }));
        Assert.Contains("at most 6 banners", await Refused(body => body["banners"] = Enumerable.Range(0, 7).Select(i => new { id = $"b{i}", title = $"Banner {i}", text = "", buttonLabel = "", link = "", theme = "blue" }).ToArray()));
        Assert.Contains("Choose a category", await Refused(body => body["rows"] = new[] { new { id = "r", title = "By category", subtitle = (string?)null, mode = "category", categoryId = (Guid?)null, courseIds = Array.Empty<Guid>(), limit = 8 } }));
        Assert.Contains("Choose at least one course", await Refused(body => body["rows"] = new[] { new { id = "r", title = "Picked", subtitle = (string?)null, mode = "manual", categoryId = (Guid?)null, courseIds = Array.Empty<Guid>(), limit = 8 } }));
        Assert.Contains("footer", await Refused(body => body["footerGroups"] = new[] { new { title = "Links", links = new[] { new { label = "Bad", url = "http://insecure.example.org" } } } }));
        Assert.Contains("at most 12 questions", await Refused(body => body["faq"] = Enumerable.Range(0, 13).Select(i => new { question = $"Q{i}", answer = "A" }).ToArray()));
        Assert.True((await t.Admin.GetAsync("/api/v1/tenant/landing")).IsSuccessStatusCode);
        Assert.True((await ReadAsync(await t.Admin.GetAsync("/api/v1/tenant/landing"))).GetProperty("isDefault").GetBoolean());   // none of it was saved
    }

    [Fact]
    public async Task Odd_values_are_corrected_rather_than_trusted()
    {
        var t = await _world.NewTenantAsync();
        var body = Edit(await ContentAsync(t), content =>
        {
            content["banners"] = new[] { new { id = "", title = "Spring", text = "x", buttonLabel = "", link = "javascript:alert(1)", theme = "hot-pink" } };   // no button, so its link is dropped, not checked
            content["features"] = new[] { new { title = "Live", text = "Classes", icon = "not-an-icon" } };
            content["rows"] = new[] { new { id = "", title = "New", subtitle = "", mode = "unknown", categoryId = (Guid?)Guid.NewGuid(), courseIds = new[] { Guid.NewGuid() }, limit = 500 } };
        });
        Assert.Equal(HttpStatusCode.OK, (await t.Admin.PutAsJsonAsync("/api/v1/tenant/landing", body)).StatusCode);
        var saved = await ContentAsync(t);
        var banner = saved.GetProperty("banners")[0];
        Assert.Equal("blue", banner.GetProperty("theme").GetString());
        Assert.Equal("", banner.GetProperty("link").GetString());
        Assert.False(string.IsNullOrEmpty(banner.GetProperty("id").GetString()));
        Assert.Equal("star", saved.GetProperty("features")[0].GetProperty("icon").GetString());
        var row = saved.GetProperty("rows")[0];
        Assert.Equal("newest", row.GetProperty("mode").GetString());
        Assert.Equal(24, row.GetProperty("limit").GetInt32());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("categoryId").ValueKind);
        Assert.Equal(0, row.GetProperty("courseIds").GetArrayLength());
    }

    [Fact]
    public async Task Only_administrators_can_change_the_page_and_one_organization_never_changes_anothers()
    {
        var t = await _world.NewTenantAsync();
        var teacher = await _world.AddPersonAsync(t, "Tara", "TEACHER");
        var other = await _world.NewTenantAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await teacher.Client.GetAsync("/api/v1/tenant/landing")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await teacher.Client.PutAsJsonAsync("/api/v1/tenant/landing", await ContentAsync(t))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await teacher.Client.PostAsync("/api/v1/tenant/landing/reset", null)).StatusCode);

        var body = Edit(await ContentAsync(t), content => content["hero"] = new { title = "Only for T", subtitle = "", primaryLabel = "", primaryLink = "", searchPlaceholder = "" });
        await t.Admin.PutAsJsonAsync("/api/v1/tenant/landing", body);
        Assert.Equal("Only for T", (await LandingAsync(t)).GetProperty("content").GetProperty("hero").GetProperty("title").GetString());
        Assert.Equal("Learn without limits", (await LandingAsync(other)).GetProperty("content").GetProperty("hero").GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_stored_page_that_is_damaged_falls_back_to_the_default_instead_of_breaking_the_front_page()
    {
        var t = await _world.NewTenantAsync();
        await _world.WithDbAsync(t.Slug, async db =>
        {
            var tenantId = (await db.Users.FirstAsync()).Id == Guid.Empty ? Guid.Empty : (await db.Tenants.SingleAsync(item => item.Slug == t.Slug)).Id;
            db.LandingPages.Add(new LandingPage { Id = Guid.NewGuid(), TenantId = tenantId, ContentJson = "{ this is not json", UpdatedAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        });
        Assert.Equal(HttpStatusCode.OK, (await Visitor().GetAsync(Public(t, "/landing"))).StatusCode);
        Assert.Equal("Learn without limits", (await LandingAsync(t)).GetProperty("content").GetProperty("hero").GetProperty("title").GetString());
    }
}
