using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

/// <summary>Courses belong to a category chosen when they are created or edited.</summary>
public sealed class CourseCategoryTests : IClassFixture<LmsApiFactory>
{
    private readonly TestWorld _world;

    public CourseCategoryTests(LmsApiFactory factory) { _world = new TestWorld(factory); }

    private static async Task<Guid> NewCategoryAsync(Tenant t, string name)
    {
        var response = await t.Admin.PostAsJsonAsync("/api/v1/tenant/catalog/categories", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> CreateCourseAsync(Tenant t, string code, Guid? categoryId)
    {
        var response = await t.Admin.PostAsJsonAsync("/api/v1/tenant/courses", new { code, title = $"Course {code}", categoryId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync(response);
    }

    private static async Task<JsonElement> CategoryRowAsync(Tenant t, Guid id)
        => (await ReadAsync(await t.Admin.GetAsync("/api/v1/tenant/catalog/categories"))).EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == id);

    [Fact]
    public async Task A_course_can_be_created_in_a_category_and_shows_it_everywhere()
    {
        var t = await _world.NewTenantAsync();
        var science = await NewCategoryAsync(t, "Science");
        var created = await CreateCourseAsync(t, "CAT-1", science);
        Assert.Equal(science, created.GetProperty("course").GetProperty("categoryId").GetGuid());
        Assert.Equal("Science", created.GetProperty("course").GetProperty("categoryName").GetString());

        var list = await ReadAsync(await t.Admin.GetAsync("/api/v1/tenant/courses"));
        Assert.Equal("Science", list[0].GetProperty("categoryName").GetString());
        var detail = await ReadAsync(await t.Admin.GetAsync($"/api/v1/tenant/courses/{created.GetProperty("course").GetProperty("id").GetGuid()}"));
        Assert.Equal("Science", detail.GetProperty("course").GetProperty("categoryName").GetString());
        Assert.Equal(1, (await CategoryRowAsync(t, science)).GetProperty("courseCount").GetInt32());
    }

    [Fact]
    public async Task A_course_without_a_category_has_none()
    {
        var t = await _world.NewTenantAsync();
        var created = await CreateCourseAsync(t, "CAT-2", null);
        Assert.Equal(JsonValueKind.Null, created.GetProperty("course").GetProperty("categoryId").ValueKind);
        Assert.Equal(JsonValueKind.Null, created.GetProperty("course").GetProperty("categoryName").ValueKind);
    }

    [Fact]
    public async Task The_category_can_be_changed_or_cleared_on_a_draft_and_counts_follow()
    {
        var t = await _world.NewTenantAsync();
        var science = await NewCategoryAsync(t, "Science");
        var arts = await NewCategoryAsync(t, "Arts");
        var created = await CreateCourseAsync(t, "CAT-3", science);
        var id = created.GetProperty("course").GetProperty("id").GetGuid();

        var moved = await ReadAsync(await t.Admin.PutAsJsonAsync($"/api/v1/tenant/courses/{id}", new { title = "Course CAT-3", categoryId = arts }));
        Assert.Equal("Arts", moved.GetProperty("course").GetProperty("categoryName").GetString());
        Assert.Equal(0, (await CategoryRowAsync(t, science)).GetProperty("courseCount").GetInt32());
        Assert.Equal(1, (await CategoryRowAsync(t, arts)).GetProperty("courseCount").GetInt32());

        var cleared = await ReadAsync(await t.Admin.PutAsJsonAsync($"/api/v1/tenant/courses/{id}", new { title = "Course CAT-3" }));
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("course").GetProperty("categoryId").ValueKind);
    }

    [Fact]
    public async Task A_category_that_does_not_exist_or_belongs_to_another_organization_is_refused()
    {
        var a = await _world.NewTenantAsync();
        var b = await _world.NewTenantAsync();
        var foreign = await NewCategoryAsync(b, "Their category");
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Admin.PostAsJsonAsync("/api/v1/tenant/courses", new { code = "CAT-4", title = "x", categoryId = foreign })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Admin.PostAsJsonAsync("/api/v1/tenant/courses", new { code = "CAT-4", title = "x", categoryId = Guid.NewGuid() })).StatusCode);

        var created = await CreateCourseAsync(a, "CAT-5", null);
        var id = created.GetProperty("course").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Admin.PutAsJsonAsync($"/api/v1/tenant/courses/{id}", new { title = "x", categoryId = foreign })).StatusCode);
        // A refused request leaves nothing behind: the code is still free.
        Assert.Equal(HttpStatusCode.Created, (await a.Admin.PostAsJsonAsync("/api/v1/tenant/courses", new { code = "CAT-4", title = "x" })).StatusCode);
    }

    [Fact]
    public async Task A_category_in_use_cannot_be_deleted_until_its_courses_move_away()
    {
        var t = await _world.NewTenantAsync();
        var science = await NewCategoryAsync(t, "Science");
        var created = await CreateCourseAsync(t, "CAT-6", science);
        var id = created.GetProperty("course").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await t.Admin.DeleteAsync($"/api/v1/tenant/catalog/categories/{science}")).StatusCode);
        await t.Admin.PutAsJsonAsync($"/api/v1/tenant/courses/{id}", new { title = "Course CAT-6" });
        Assert.Equal(HttpStatusCode.NoContent, (await t.Admin.DeleteAsync($"/api/v1/tenant/catalog/categories/{science}")).StatusCode);
    }

    [Fact]
    public async Task Learners_see_the_category_of_published_courses()
    {
        var t = await _world.NewTenantAsync();
        var science = await NewCategoryAsync(t, "Science");
        var created = await CreateCourseAsync(t, "CAT-7", science);
        var id = created.GetProperty("course").GetProperty("id").GetGuid();
        var moduleId = (await ReadAsync(await t.Admin.PostAsJsonAsync($"/api/v1/tenant/courses/{id}/modules", new { title = "M" }))).GetProperty("id").GetGuid();
        await t.Admin.PostAsJsonAsync($"/api/v1/tenant/courses/{id}/modules/{moduleId}/lessons", new { title = "L" });
        await t.Admin.PostAsync($"/api/v1/tenant/courses/{id}/submit-review", null);
        await t.Admin.PostAsync($"/api/v1/tenant/courses/{id}/publish", null);
        var ada = await _world.AddLearnerAsync(t, "Ada");
        var list = await ReadAsync(await ada.Client.GetAsync("/api/v1/tenant/courses"));
        Assert.Equal("Science", list[0].GetProperty("categoryName").GetString());
    }
}
