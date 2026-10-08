using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lms.Api.Features.Landing;
using Lms.Api.Infrastructure.Landing;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

public sealed class RatingUnitTests
{
    [Theory]
    [InlineData("Lena Kapoor", "Lena K.")]
    [InlineData("lena", "lena")]
    [InlineData("Ada de la Cruz", "Ada C.")]
    [InlineData("  ", "A learner")]
    [InlineData(null, "A learner")]
    public void Reviews_show_a_first_name_and_initial_only(string? name, string expected) => Assert.Equal(expected, RatingEndpoints.PublicName(name));

    [Theory]
    [InlineData("not-an-id", "")]
    [InlineData("", "")]
    [InlineData("5F0C7A12-3B64-4E9D-9C11-0A1B2C3D4E5F", "5f0c7a12-3b64-4e9d-9c11-0a1b2c3d4e5f")]
    public void A_picture_id_is_kept_only_when_it_is_one(string input, string expected) => Assert.Equal(expected, LandingContentRules.CleanImageId(input));
}

/// <summary>Learners rate courses; staff moderate; the public page shows the result.</summary>
public sealed class RatingTests : IClassFixture<LmsApiFactory>
{
    private readonly LmsApiFactory _factory;
    private readonly TestWorld _world;
    public RatingTests(LmsApiFactory factory) { _factory = factory; _world = new TestWorld(factory); }

    private sealed record Setup(Tenant Tenant, CourseInfo Course);

    private async Task<Setup> NewSetupAsync()
    {
        var tenant = await _world.NewTenantAsync();
        return new Setup(tenant, await _world.NewCourseAsync(tenant, "RATE", lessonsPerModule: 2));
    }

    /// <summary>A learner who is enrolled and has finished one lesson, so they are allowed to rate.</summary>
    private async Task<Person> StartedLearnerAsync(Setup s, string name)
    {
        var learner = await _world.AddLearnerAsync(s.Tenant, name);
        Assert.True((await EnrollAsync(learner, s.Course)).IsSuccessStatusCode);
        await CompleteLessonAsync(learner, s.Course, s.Course.LessonIds[0][0]);
        return learner;
    }

    private static Task<HttpResponseMessage> Rate(Person who, Setup s, int stars, string? review = null) => who.Client.PutAsJsonAsync($"/api/v1/tenant/courses/{s.Course.Id}/rating", new { stars, review });
    private static async Task<JsonElement> PublicDetailAsync(LmsApiFactory factory, Setup s) => await ReadAsync(await factory.CreateClient().GetAsync($"/api/v1/public/{s.Tenant.Slug}/courses/{s.Course.Id}"));
    private static async Task<JsonElement> PublicCardAsync(LmsApiFactory factory, Setup s)
        => (await ReadAsync(await factory.CreateClient().GetAsync($"/api/v1/public/{s.Tenant.Slug}/landing"))).GetProperty("courses").EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == s.Course.Id);

    [Fact]
    public async Task Only_people_who_have_started_the_course_can_rate_it()
    {
        var s = await NewSetupAsync();
        var outsider = await _world.AddLearnerAsync(s.Tenant, "Olga");
        var refused = await Rate(outsider, s, 5);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("taking this course", await refused.Content.ReadAsStringAsync());
        Assert.Contains("taking this course", (await ReadAsync(await outsider.Client.GetAsync($"/api/v1/tenant/courses/{s.Course.Id}/rating"))).GetProperty("cannotRateBecause").GetString());

        var fresh = await _world.AddLearnerAsync(s.Tenant, "Fred");
        await EnrollAsync(fresh, s.Course);
        var early = await Rate(fresh, s, 5);
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Contains("Start the course", await early.Content.ReadAsStringAsync());

        var started = await StartedLearnerAsync(s, "Sam");
        Assert.Equal(HttpStatusCode.OK, (await Rate(started, s, 4, "Good")).StatusCode);

        var guardian = await _world.AddPersonAsync(s.Tenant, "Gail", "GUARDIAN");
        Assert.Equal(HttpStatusCode.Conflict, (await Rate(guardian, s, 5)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateTenantClient(s.Tenant.Slug).PutAsJsonAsync($"/api/v1/tenant/courses/{s.Course.Id}/rating", new { stars = 5 })).StatusCode);
    }

    [Fact]
    public async Task A_rating_is_checked_and_one_per_learner_that_they_can_change_or_withdraw()
    {
        var s = await NewSetupAsync();
        var lena = await StartedLearnerAsync(s, "Lena");
        Assert.Equal(HttpStatusCode.BadRequest, (await Rate(lena, s, 0)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Rate(lena, s, 6)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Rate(lena, s, 3, new string('x', 1001))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await lena.Client.PutAsJsonAsync($"/api/v1/tenant/courses/{Guid.NewGuid()}/rating", new { stars = 3 })).StatusCode);

        await Rate(lena, s, 3, "  Fine  ");
        await Rate(lena, s, 5, "Much better on a second look");
        var mine = (await ReadAsync(await lena.Client.GetAsync($"/api/v1/tenant/courses/{s.Course.Id}/rating"))).GetProperty("rating");
        Assert.Equal((5, "Much better on a second look"), (mine.GetProperty("stars").GetInt32(), mine.GetProperty("review").GetString()));
        Assert.Equal(1, (await PublicCardAsync(_factory, s)).GetProperty("ratingCount").GetInt32());   // a change, not a second rating

        Assert.Equal(HttpStatusCode.NoContent, (await lena.Client.DeleteAsync($"/api/v1/tenant/courses/{s.Course.Id}/rating")).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await ReadAsync(await lena.Client.GetAsync($"/api/v1/tenant/courses/{s.Course.Id}/rating"))).GetProperty("rating").ValueKind);
        Assert.Equal(0, (await PublicCardAsync(_factory, s)).GetProperty("ratingCount").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await lena.Client.DeleteAsync($"/api/v1/tenant/courses/{s.Course.Id}/rating")).StatusCode);   // withdrawing nothing is harmless
    }

    [Fact]
    public async Task The_public_page_shows_the_average_the_count_and_reviews_with_first_names_only()
    {
        var s = await NewSetupAsync();
        var lena = await StartedLearnerAsync(s, "Lena");
        var otto = await StartedLearnerAsync(s, "Otto");
        var cleo = await StartedLearnerAsync(s, "Cleo");
        await Rate(lena, s, 5, "Clear and well paced");
        await Rate(otto, s, 4);
        await Rate(cleo, s, 3, "Good but long");

        var card = await PublicCardAsync(_factory, s);
        Assert.Equal((4.0, 3), (card.GetProperty("ratingAverage").GetDouble(), card.GetProperty("ratingCount").GetInt32()));

        var detail = await PublicDetailAsync(_factory, s);
        Assert.Equal(new[] { 0, 0, 1, 1, 1 }, detail.GetProperty("rating").GetProperty("distribution").EnumerateArray().Select(item => item.GetInt32()).ToArray());
        var reviews = detail.GetProperty("reviews").EnumerateArray().ToList();
        Assert.Equal(2, reviews.Count);   // Otto wrote no words, so there is no review to show
        Assert.Equal(new[] { "Cleo", "Lena" }, reviews.Select(item => item.GetProperty("author").GetString()!.Split(' ')[0]).Order().ToArray());
        Assert.DoesNotContain("@", detail.GetRawText());
    }

    [Fact]
    public async Task A_course_nobody_rated_shows_no_rating()
    {
        var s = await NewSetupAsync();
        var card = await PublicCardAsync(_factory, s);
        Assert.Equal(JsonValueKind.Null, card.GetProperty("ratingAverage").ValueKind);
        Assert.Equal(0, card.GetProperty("ratingCount").GetInt32());
        Assert.Empty((await PublicDetailAsync(_factory, s)).GetProperty("reviews").EnumerateArray());
    }

    [Fact]
    public async Task Staff_can_hide_a_rating_and_it_stops_counting_even_if_its_author_edits_it()
    {
        var s = await NewSetupAsync();
        var lena = await StartedLearnerAsync(s, "Lena");
        var spam = await StartedLearnerAsync(s, "Spam");
        await Rate(lena, s, 5, "Lovely");
        await Rate(spam, s, 1, "Buy my pills");

        var staff = await ReadAsync(await s.Tenant.Admin.GetAsync($"/api/v1/tenant/courses/{s.Course.Id}/ratings"));
        var bad = staff.GetProperty("ratings").EnumerateArray().Single(item => item.GetProperty("learnerName").GetString() == "Spam").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await lena.Client.PostAsync($"/api/v1/tenant/ratings/{bad}/hide", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await lena.Client.GetAsync($"/api/v1/tenant/courses/{s.Course.Id}/ratings")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await s.Tenant.Admin.PostAsync($"/api/v1/tenant/ratings/{bad}/hide", null)).StatusCode);
        var card = await PublicCardAsync(_factory, s);
        Assert.Equal((5.0, 1), (card.GetProperty("ratingAverage").GetDouble(), card.GetProperty("ratingCount").GetInt32()));
        Assert.Equal("Lena", (await PublicDetailAsync(_factory, s)).GetProperty("reviews").EnumerateArray().Single().GetProperty("author").GetString()!.Split(' ')[0]);

        await Rate(spam, s, 1, "Buy my pills, now cheaper");   // editing does not bring it back
        Assert.Equal(1, (await PublicCardAsync(_factory, s)).GetProperty("ratingCount").GetInt32());
        Assert.True((await ReadAsync(await spam.Client.GetAsync($"/api/v1/tenant/courses/{s.Course.Id}/rating"))).GetProperty("rating").GetProperty("isHidden").GetBoolean());
        var listed = (await ReadAsync(await s.Tenant.Admin.GetAsync($"/api/v1/tenant/courses/{s.Course.Id}/ratings"))).GetProperty("ratings").EnumerateArray().ToList();
        Assert.Equal(2, listed.Count);   // staff still see it, marked hidden

        Assert.Equal(HttpStatusCode.NoContent, (await s.Tenant.Admin.PostAsync($"/api/v1/tenant/ratings/{bad}/show", null)).StatusCode);
        Assert.Equal(2, (await PublicCardAsync(_factory, s)).GetProperty("ratingCount").GetInt32());
        var audit = (await ReadAsync(await s.Tenant.Admin.GetAsync("/api/v1/tenant/security/audit-events"))).EnumerateArray().Select(item => item.GetProperty("action").GetString()).ToList();
        Assert.Contains("rating.hidden", audit);
        Assert.Contains("rating.shown", audit);
    }

    [Fact]
    public async Task Ratings_stay_inside_their_organization()
    {
        var s = await NewSetupAsync();
        var lena = await StartedLearnerAsync(s, "Lena");
        await Rate(lena, s, 5, "Great");
        var other = await _world.NewTenantAsync();
        var foreign = (await ReadAsync(await other.Admin.GetAsync($"/api/v1/tenant/courses/{s.Course.Id}/ratings"))).GetProperty("ratings");
        Assert.Equal(0, foreign.GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.CreateClient().GetAsync($"/api/v1/public/{other.Slug}/courses/{s.Course.Id}")).StatusCode);
    }
}

/// <summary>The pictures on the public page.</summary>
public sealed class LandingImageTests : IClassFixture<LmsApiFactory>
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52, 1, 2, 3, 4];
    private readonly LmsApiFactory _factory;
    private readonly TestWorld _world;
    public LandingImageTests(LmsApiFactory factory) { _factory = factory; _world = new TestWorld(factory); }

    private static MultipartFormDataContent Picture(string name, byte[] bytes, string contentType = "image/png")
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        content.Add(file, "file", name);
        return content;
    }

    private static async Task<Guid> UploadAsync(Tenant t, string name = "logo.png")
    {
        var response = await t.Admin.PostAsync("/api/v1/tenant/landing/images", Picture(name, Png));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task A_picture_uploaded_by_staff_is_served_to_anyone_with_safe_headers()
    {
        var t = await _world.NewTenantAsync();
        var id = await UploadAsync(t);
        Assert.Equal("logo.png", Assert.Single((await ReadAsync(await t.Admin.GetAsync("/api/v1/tenant/landing/images"))).EnumerateArray()).GetProperty("fileName").GetString());

        var response = await _factory.CreateClient().GetAsync($"/api/v1/public/{t.Slug}/landing-images/{id}");   // no sign-in
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("public", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Only_real_pictures_of_a_sensible_size_are_accepted_and_only_by_people_who_manage_the_organization()
    {
        var t = await _world.NewTenantAsync();
        var url = "/api/v1/tenant/landing/images";
        Assert.Equal(HttpStatusCode.BadRequest, (await t.Admin.PostAsync(url, Picture("logo.svg", "<svg onload=alert(1)/>"u8.ToArray(), "image/svg+xml"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await t.Admin.PostAsync(url, Picture("fake.png", "<script>alert(1)</script>"u8.ToArray(), "image/png"))).StatusCode);   // says PNG, is not
        Assert.Equal(HttpStatusCode.BadRequest, (await t.Admin.PostAsync(url, Picture("notes.txt", Png, "text/plain"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await t.Admin.PostAsync(url, Picture("big.png", [.. Png, .. new byte[3 * 1024 * 1024]]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await t.Admin.PostAsync(url, new MultipartFormDataContent())).StatusCode);

        var learner = await _world.AddLearnerAsync(t, "Lena");
        Assert.Equal(HttpStatusCode.Forbidden, (await learner.Client.PostAsync(url, Picture("logo.png", Png))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await learner.Client.GetAsync(url)).StatusCode);
        Assert.Empty((await ReadAsync(await t.Admin.GetAsync(url))).EnumerateArray());
    }

    [Fact]
    public async Task A_picture_belongs_to_one_organization_and_a_deleted_one_is_gone()
    {
        var t = await _world.NewTenantAsync();
        var other = await _world.NewTenantAsync();
        var id = await UploadAsync(t);
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.CreateClient().GetAsync($"/api/v1/public/{other.Slug}/landing-images/{id}")).StatusCode);   // asked for under someone else's address
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.CreateClient().GetAsync($"/api/v1/public/{t.Slug}/landing-images/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.DeleteAsync($"/api/v1/tenant/landing/images/{id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await t.Admin.DeleteAsync($"/api/v1/tenant/landing/images/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.CreateClient().GetAsync($"/api/v1/public/{t.Slug}/landing-images/{id}")).StatusCode);
    }

    private static async Task<JsonElement> ContentAsync(Tenant t) => (await ReadAsync(await t.Admin.GetAsync("/api/v1/tenant/landing"))).GetProperty("content");

    private static async Task<HttpResponseMessage> SaveAsync(Tenant t, Func<Dictionary<string, object?>, Dictionary<string, object?>> change)
    {
        var content = JsonSerializer.Deserialize<Dictionary<string, object?>>((await ContentAsync(t)).GetRawText())!;
        return await t.Admin.PutAsJsonAsync("/api/v1/tenant/landing", change(content));
    }

    [Fact]
    public async Task The_page_can_use_a_logo_and_pictures_and_the_public_page_returns_them()
    {
        var t = await _world.NewTenantAsync();
        var logo = await UploadAsync(t, "logo.png");
        var hero = await UploadAsync(t, "hero.png");
        var banner = await UploadAsync(t, "banner.png");
        var saved = await SaveAsync(t, content =>
        {
            content["logoImageId"] = logo.ToString();
            content["heroImageId"] = hero.ToString();
            var banners = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(JsonSerializer.Serialize(content["banners"]))!;
            banners[0]["imageId"] = banner.ToString();
            content["banners"] = banners;
            return content;
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var publicPage = (await ReadAsync(await _factory.CreateClient().GetAsync($"/api/v1/public/{t.Slug}/landing"))).GetProperty("content");
        Assert.Equal(logo.ToString(), publicPage.GetProperty("logoImageId").GetString());
        Assert.Equal(hero.ToString(), publicPage.GetProperty("heroImageId").GetString());
        Assert.Equal(banner.ToString(), publicPage.GetProperty("banners")[0].GetProperty("imageId").GetString());

        // Delete the logo: the saved page still points at it, but the public page shows none.
        await t.Admin.DeleteAsync($"/api/v1/tenant/landing/images/{logo}");
        var after = (await ReadAsync(await _factory.CreateClient().GetAsync($"/api/v1/public/{t.Slug}/landing"))).GetProperty("content");
        Assert.Equal("", after.GetProperty("logoImageId").GetString());
        Assert.Equal(hero.ToString(), after.GetProperty("heroImageId").GetString());
    }

    [Fact]
    public async Task A_page_cannot_point_at_a_picture_that_does_not_exist_or_belongs_to_someone_else()
    {
        var t = await _world.NewTenantAsync();
        var other = await _world.NewTenantAsync();
        var theirs = await UploadAsync(other);
        foreach (var id in new[] { Guid.NewGuid(), theirs })
        {
            var refused = await SaveAsync(t, content => { content["logoImageId"] = id.ToString(); return content; });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("picture", await refused.Content.ReadAsStringAsync());
        }
        Assert.Equal("", (await ContentAsync(t)).GetProperty("logoImageId").GetString());
        var junk = await SaveAsync(t, content => { content["logoImageId"] = "javascript:alert(1)"; return content; });
        Assert.Equal(HttpStatusCode.OK, junk.StatusCode);   // not an id: dropped, never stored
        Assert.Equal("", (await ContentAsync(t)).GetProperty("logoImageId").GetString());
    }
}
