using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Lms.Api.Tests;

/// <summary>Platform operator management of organizations: list, inspect, rename, suspend, activate and archive.</summary>
public sealed class PlatformTenantTests : IClassFixture<LmsApiFactory>
{
    private readonly LmsApiFactory _factory;
    public PlatformTenantTests(LmsApiFactory factory) => _factory = factory;

    private HttpClient Platform()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Platform-Key", LmsApiFactory.PlatformKey);
        return client;
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Every_platform_call_needs_the_platform_key()
    {
        var (slug, _, _) = await _factory.ProvisionTenantWithAdminAsync();
        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/platform/tenants")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/platform/tenants/{slug}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync($"/api/v1/platform/tenants/{slug}", new { name = "Nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"/api/v1/platform/tenants/{slug}/suspend", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"/api/v1/platform/tenants/{slug}/archive", null)).StatusCode);

        // A tenant administrator's own token is not platform access.
        var (_, _, token) = await _factory.ProvisionTenantWithAdminAsync();
        var tenantClient = _factory.CreateTenantClient(slug, token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await tenantClient.GetAsync("/api/v1/platform/tenants")).StatusCode);
    }

    [Fact]
    public async Task The_list_shows_organizations_with_their_size_and_can_be_filtered()
    {
        var (slug, _, _) = await _factory.ProvisionTenantWithAdminAsync();
        var list = await ReadAsync(await Platform().GetAsync($"/api/v1/platform/tenants?q={slug}"));
        var row = Assert.Single(list.EnumerateArray());
        Assert.Equal(slug, row.GetProperty("slug").GetString());
        Assert.Equal("Active", row.GetProperty("status").GetString());
        Assert.Equal(1, row.GetProperty("members").GetInt32());

        Assert.Empty((await ReadAsync(await Platform().GetAsync($"/api/v1/platform/tenants?q={slug}&status=Suspended"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await Platform().GetAsync("/api/v1/platform/tenants?status=Bogus")).StatusCode);
    }

    [Fact]
    public async Task Detail_lists_administrators_and_addresses()
    {
        var (slug, email, _) = await _factory.ProvisionTenantWithAdminAsync();
        var host = $"learn{Guid.NewGuid():N}"[..14] + ".example.org";
        Assert.True((await Platform().PutAsJsonAsync($"/api/v1/platform/tenants/{slug}/domains", new { host })).IsSuccessStatusCode);

        var detail = await ReadAsync(await Platform().GetAsync($"/api/v1/platform/tenants/{slug}"));
        Assert.Equal(slug, detail.GetProperty("slug").GetString());
        Assert.Equal(email, Assert.Single(detail.GetProperty("admins").EnumerateArray()).GetProperty("email").GetString());
        Assert.Equal(host, Assert.Single(detail.GetProperty("domains").EnumerateArray()).GetString());
        Assert.Equal(0, detail.GetProperty("courses").GetInt32());

        Assert.Equal(HttpStatusCode.NotFound, (await Platform().GetAsync("/api/v1/platform/tenants/no-such-org")).StatusCode);
    }

    [Fact]
    public async Task An_organization_can_be_renamed_but_not_to_nothing()
    {
        var (slug, _, _) = await _factory.ProvisionTenantWithAdminAsync();
        var renamed = await ReadAsync(await Platform().PutAsJsonAsync($"/api/v1/platform/tenants/{slug}", new { name = "  Renamed School  " }));
        Assert.Equal("Renamed School", renamed.GetProperty("name").GetString());
        Assert.Equal(slug, renamed.GetProperty("slug").GetString());   // the address people use never changes
        Assert.Equal(HttpStatusCode.BadRequest, (await Platform().PutAsJsonAsync($"/api/v1/platform/tenants/{slug}", new { name = "  " })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Platform().PutAsJsonAsync("/api/v1/platform/tenants/no-such-org", new { name = "x" })).StatusCode);
    }

    [Fact]
    public async Task A_suspended_organization_is_locked_out_and_comes_back_when_activated()
    {
        var (slug, email, token) = await _factory.ProvisionTenantWithAdminAsync();
        var tenantClient = () => _factory.CreateTenantClient(slug, token);
        Assert.Equal(HttpStatusCode.OK, (await tenantClient().GetAsync("/api/v1/tenant/context")).StatusCode);

        var suspended = await ReadAsync(await Platform().PostAsync($"/api/v1/platform/tenants/{slug}/suspend", null));
        Assert.Equal("Suspended", suspended.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await tenantClient().GetAsync("/api/v1/tenant/context")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { tenantSlug = slug, email, password = LmsApiFactory.AdminPassword })).StatusCode);

        var active = await ReadAsync(await Platform().PostAsync($"/api/v1/platform/tenants/{slug}/activate", null));
        Assert.Equal("Active", active.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await tenantClient().GetAsync("/api/v1/tenant/context")).StatusCode);
    }

    [Fact]
    public async Task Suspending_signs_everyone_out_for_good()
    {
        var (slug, email, _) = await _factory.ProvisionTenantWithAdminAsync();
        var login = await ReadAsync(await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { tenantSlug = slug, email, password = LmsApiFactory.AdminPassword }));
        var refreshToken = login.GetProperty("refreshToken").GetString();

        Assert.True((await Platform().PostAsync($"/api/v1/platform/tenants/{slug}/suspend", null)).IsSuccessStatusCode);
        Assert.True((await Platform().PostAsync($"/api/v1/platform/tenants/{slug}/activate", null)).IsSuccessStatusCode);

        var refreshed = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/refresh", new { tenantSlug = slug, refreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refreshed.StatusCode);
    }

    [Fact]
    public async Task Archiving_closes_the_organization_and_repeating_a_change_is_harmless()
    {
        var (slug, _, token) = await _factory.ProvisionTenantWithAdminAsync();
        Assert.True((await Platform().PostAsync($"/api/v1/platform/tenants/{slug}/archive", null)).IsSuccessStatusCode);
        Assert.True((await Platform().PostAsync($"/api/v1/platform/tenants/{slug}/archive", null)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.CreateTenantClient(slug, token).GetAsync("/api/v1/tenant/context")).StatusCode);

        // An archived organization is not suspended by accident; it is reactivated first.
        Assert.Equal(HttpStatusCode.Conflict, (await Platform().PostAsync($"/api/v1/platform/tenants/{slug}/suspend", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Platform().PostAsync("/api/v1/platform/tenants/no-such-org/suspend", null)).StatusCode);

        var archived = await ReadAsync(await Platform().GetAsync($"/api/v1/platform/tenants?q={slug}&status=Archived"));
        Assert.Single(archived.EnumerateArray());
    }

    [Fact]
    public async Task A_suspended_organization_does_not_affect_others()
    {
        var first = await _factory.ProvisionTenantWithAdminAsync();
        var second = await _factory.ProvisionTenantWithAdminAsync();
        Assert.True((await Platform().PostAsync($"/api/v1/platform/tenants/{first.Slug}/suspend", null)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateTenantClient(second.Slug, second.Token).GetAsync("/api/v1/tenant/context")).StatusCode);
    }
}
