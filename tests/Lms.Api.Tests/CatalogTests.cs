using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Lms.Api.Tests;

public sealed class CatalogTests : IClassFixture<LmsApiFactory>
{
    private readonly LmsApiFactory _factory;
    public CatalogTests(LmsApiFactory factory) => _factory = factory;

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    [Fact]
    public async Task Category_can_be_created_listed_and_duplicates_are_rejected()
    {
        var (slug, _, token) = await _factory.ProvisionTenantWithAdminAsync();
        var client = _factory.CreateTenantClient(slug, token);

        var created = await client.PostAsJsonAsync("/api/v1/tenant/catalog/categories", new { name = "Mathematics" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var duplicate = await client.PostAsJsonAsync("/api/v1/tenant/catalog/categories", new { name = "mathematics" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var list = await ReadAsync(await client.GetAsync("/api/v1/tenant/catalog/categories"));
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal("mathematics", list[0].GetProperty("slug").GetString());
    }

    [Fact]
    public async Task Category_validation_rejects_blank_names()
    {
        var (slug, _, token) = await _factory.ProvisionTenantWithAdminAsync();
        var response = await _factory.CreateTenantClient(slug, token).PostAsJsonAsync("/api/v1/tenant/catalog/categories", new { name = " " });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Category_is_deleted_when_unused()
    {
        var (slug, _, token) = await _factory.ProvisionTenantWithAdminAsync();
        var client = _factory.CreateTenantClient(slug, token);
        var created = await ReadAsync(await client.PostAsJsonAsync("/api/v1/tenant/catalog/categories", new { name = "Science" }));
        var delete = await client.DeleteAsync($"/api/v1/tenant/catalog/categories/{created.GetProperty("id").GetGuid()}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Fact]
    public async Task Learning_path_requires_existing_courses()
    {
        var (slug, _, token) = await _factory.ProvisionTenantWithAdminAsync();
        var client = _factory.CreateTenantClient(slug, token);

        var empty = await client.PostAsJsonAsync("/api/v1/tenant/catalog/paths", new { title = "Starter", courseIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var unknown = await client.PostAsJsonAsync("/api/v1/tenant/catalog/paths", new { title = "Starter", courseIds = new[] { Guid.NewGuid() } });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task Learning_path_lists_its_courses_in_order()
    {
        var (slug, _, token) = await _factory.ProvisionTenantWithAdminAsync();
        var client = _factory.CreateTenantClient(slug, token);
        var first = await ReadAsync(await client.PostAsJsonAsync("/api/v1/tenant/courses", new { code = "ART-1", title = "Art one" }));
        var second = await ReadAsync(await client.PostAsJsonAsync("/api/v1/tenant/courses", new { code = "ART-2", title = "Art two" }));
        var firstId = first.GetProperty("course").GetProperty("id").GetGuid();
        var secondId = second.GetProperty("course").GetProperty("id").GetGuid();

        var created = await client.PostAsJsonAsync("/api/v1/tenant/catalog/paths", new { title = "Art track", courseIds = new[] { secondId, firstId } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var paths = await ReadAsync(await client.GetAsync("/api/v1/tenant/catalog/paths"));
        var courses = paths[0].GetProperty("courses");
        Assert.Equal("ART-2", courses[0].GetProperty("code").GetString());
        Assert.Equal("ART-1", courses[1].GetProperty("code").GetString());
    }

    [Fact]
    public async Task Resource_requires_a_valid_http_url_and_type()
    {
        var (slug, _, token) = await _factory.ProvisionTenantWithAdminAsync();
        var client = _factory.CreateTenantClient(slug, token);

        var badUrl = await client.PostAsJsonAsync("/api/v1/tenant/catalog/resources", new { type = "Link", title = "Docs", url = "javascript:alert(1)" });
        Assert.Equal(HttpStatusCode.BadRequest, badUrl.StatusCode);

        var badType = await client.PostAsJsonAsync("/api/v1/tenant/catalog/resources", new { type = "Nope", title = "Docs", url = "https://example.org" });
        Assert.Equal(HttpStatusCode.BadRequest, badType.StatusCode);

        var ok = await client.PostAsJsonAsync("/api/v1/tenant/catalog/resources", new { type = "Link", title = "Docs", url = "https://example.org/docs" });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);

        var list = await ReadAsync(await client.GetAsync("/api/v1/tenant/catalog/resources?type=Link"));
        Assert.Equal("https://example.org/docs", list[0].GetProperty("url").GetString());
    }

    [Fact]
    public async Task Catalog_data_is_isolated_between_tenants()
    {
        var (slugA, _, tokenA) = await _factory.ProvisionTenantWithAdminAsync();
        var (slugB, _, tokenB) = await _factory.ProvisionTenantWithAdminAsync();
        (await _factory.CreateTenantClient(slugA, tokenA).PostAsJsonAsync("/api/v1/tenant/catalog/categories", new { name = "OnlyInA" })).EnsureSuccessStatusCode();

        var listB = await ReadAsync(await _factory.CreateTenantClient(slugB, tokenB).GetAsync("/api/v1/tenant/catalog/categories"));
        Assert.Equal(0, listB.GetArrayLength());
    }

    [Fact]
    public async Task Learner_can_read_but_not_manage_the_catalog()
    {
        var (slug, _, adminToken) = await _factory.ProvisionTenantWithAdminAsync();
        var admin = _factory.CreateTenantClient(slug, adminToken);
        var learnerEmail = $"learner@{slug}.test";
        (await admin.PostAsJsonAsync("/api/v1/tenant/users", new { email = learnerEmail, displayName = "Learner", password = LmsApiFactory.AdminPassword, roleCode = "LEARNER" })).EnsureSuccessStatusCode();
        var learner = _factory.CreateTenantClient(slug, await _factory.LoginAsync(slug, learnerEmail, LmsApiFactory.AdminPassword));

        Assert.Equal(HttpStatusCode.OK, (await learner.GetAsync("/api/v1/tenant/catalog/categories")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await learner.PostAsJsonAsync("/api/v1/tenant/catalog/categories", new { name = "Blocked" })).StatusCode);
    }
}
