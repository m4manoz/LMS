using System.Net;
using System.Net.Http.Json;

namespace Lms.Api.Tests;

public sealed class TenantIsolationTests : IClassFixture<LmsApiFactory>
{
    private readonly LmsApiFactory _factory;
    public TenantIsolationTests(LmsApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Provisioning_requires_platform_key()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/v1/platform/tenants", new { name = "x", slug = "nokey" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_with_wrong_password_is_unauthorized()
    {
        var (slug, email, _) = await _factory.ProvisionTenantWithAdminAsync();
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { tenantSlug = slug, email, password = "wrong-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Tenant_endpoints_require_authentication()
    {
        var (slug, _, _) = await _factory.ProvisionTenantWithAdminAsync();
        var response = await _factory.CreateTenantClient(slug).GetAsync("/api/v1/tenant/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Admin_can_read_own_tenant()
    {
        var (slug, _, token) = await _factory.ProvisionTenantWithAdminAsync();
        var response = await _factory.CreateTenantClient(slug, token).GetAsync("/api/v1/tenant/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Token_from_one_tenant_is_rejected_for_another_tenant()
    {
        var (_, _, tokenA) = await _factory.ProvisionTenantWithAdminAsync();
        var (slugB, _, _) = await _factory.ProvisionTenantWithAdminAsync();
        var response = await _factory.CreateTenantClient(slugB, tokenA).GetAsync("/api/v1/tenant/users");
        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"Got {response.StatusCode}");
    }

    [Fact]
    public async Task User_list_never_contains_other_tenants_users()
    {
        var (slugA, emailA, tokenA) = await _factory.ProvisionTenantWithAdminAsync();
        var (_, emailB, _) = await _factory.ProvisionTenantWithAdminAsync();
        var body = await _factory.CreateTenantClient(slugA, tokenA).GetStringAsync("/api/v1/tenant/users");
        Assert.Contains(emailA, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(emailB, body, StringComparison.OrdinalIgnoreCase);
    }
}
