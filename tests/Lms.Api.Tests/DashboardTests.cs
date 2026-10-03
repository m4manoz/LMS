using System.Net;
using System.Text.Json;

namespace Lms.Api.Tests;

public sealed class DashboardTests : IClassFixture<LmsApiFactory>
{
    private readonly LmsApiFactory _factory;
    public DashboardTests(LmsApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Dashboard_requires_authentication()
    {
        var (slug, _, _) = await _factory.ProvisionTenantWithAdminAsync();
        var response = await _factory.CreateTenantClient(slug).GetAsync("/api/v1/tenant/dashboard");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Admin_dashboard_includes_teaching_and_admin_summaries()
    {
        var (slug, _, token) = await _factory.ProvisionTenantWithAdminAsync();
        var response = await _factory.CreateTenantClient(slug, token).GetAsync("/api/v1/tenant/dashboard");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(0, json.GetProperty("learning").GetProperty("activeEnrollments").GetInt32());
        Assert.Equal(0, json.GetProperty("teaching").GetProperty("attemptsToGrade").GetInt32());
        Assert.Equal(0, json.GetProperty("admin").GetProperty("publishedCourses").GetInt32());
    }
}
