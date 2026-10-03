using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lms.Api.Infrastructure.Tenancy;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

public sealed class TenantHostsTests
{
    [Theory]
    [InlineData("Learn.School.EDU", "learn.school.edu")]
    [InlineData("learn.school.edu:8443", "learn.school.edu")]
    [InlineData("learn.school.edu.", "learn.school.edu")]
    [InlineData("localhost:5173", "localhost")]
    [InlineData("https://learn.school.edu", null)]
    [InlineData("learn.school.edu/path", null)]
    [InlineData("a b.com", null)]
    [InlineData("-bad.example.com", null)]
    [InlineData("", null)]
    public void Addresses_are_cleaned_up_or_refused(string input, string? expected) => Assert.Equal(expected, TenantHosts.Normalize(input));

    [Theory]
    [InlineData("learn.school.edu", true)]
    [InlineData("school.edu", true)]
    [InlineData("localhost", false)]
    [InlineData("10.0.0.5", false)]
    [InlineData("singlelabel", false)]
    public void Only_real_host_names_can_be_given_to_an_organization(string input, bool allowed) => Assert.Equal(allowed, TenantHosts.IsCustomDomain(input));
}

/// <summary>The two ways to host: an organization's own website (its address says which organization) and the shared sign-in portal.</summary>
public sealed class TenantSiteTests : IClassFixture<LmsApiFactory>
{
    private readonly LmsApiFactory _factory;
    private readonly TestWorld _world;
    public TenantSiteTests(LmsApiFactory factory) { _factory = factory; _world = new TestWorld(factory); }

    private HttpClient Platform()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Platform-Key", LmsApiFactory.PlatformKey);
        return client;
    }

    private Task<HttpResponseMessage> AddDomain(Tenant t, string host) => Platform().PutAsJsonAsync($"/api/v1/platform/tenants/{t.Slug}/domains", new { host });
    private async Task<JsonElement> SiteAsync(string host) => await ReadAsync(await _factory.CreateClient().GetAsync($"/api/v1/public/site?host={Uri.EscapeDataString(host)}"));
    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..14] + ".example.org";

    [Fact]
    public async Task An_address_nobody_owns_is_the_shared_portal()
    {
        var site = await SiteAsync(Unique("nobody"));
        Assert.Equal("portal", site.GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, site.GetProperty("organization").ValueKind);
        Assert.Equal("portal", (await SiteAsync("localhost:5173")).GetProperty("mode").GetString());
        Assert.Equal("portal", (await SiteAsync("")).GetProperty("mode").GetString());
    }

    [Fact]
    public async Task An_organizations_own_address_is_its_website_whatever_the_case_or_port()
    {
        var t = await _world.NewTenantAsync();
        var host = Unique("learn");
        Assert.Equal(HttpStatusCode.Created, (await AddDomain(t, host)).StatusCode);

        foreach (var typed in new[] { host, host.ToUpperInvariant(), host + ":8443" })
        {
            var site = await SiteAsync(typed);
            Assert.Equal("tenant", site.GetProperty("mode").GetString());
            Assert.Equal(t.Slug, site.GetProperty("organization").GetProperty("slug").GetString());
        }
    }

    [Fact]
    public async Task A_request_to_an_organizations_own_address_is_for_that_organization_without_any_header()
    {
        var t = await _world.NewTenantAsync();
        var host = Unique("api");
        await AddDomain(t, host);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/public/organizations/default");
        request.Headers.Host = host;
        var response = await _factory.CreateClient().SendAsync(request);
        Assert.True(response.Headers.TryGetValues("X-Tenant-Id", out var ids));
        Assert.NotEmpty(ids!);
    }

    [Fact]
    public async Task A_subdomain_of_the_platform_names_the_organization_except_for_portal_addresses()
    {
        var t = await _world.NewTenantAsync();
        Assert.Equal(t.Slug, (await SiteAsync($"{t.Slug}.lms.example.org")).GetProperty("organization").GetProperty("slug").GetString());
        Assert.Equal("portal", (await SiteAsync($"www.lms.example.org")).GetProperty("mode").GetString());
        Assert.Equal("portal", (await SiteAsync($"{t.Slug}.com")).GetProperty("mode").GetString());       // two labels: not a subdomain

        using var factory = new LmsApiFactory { ExtraSettings = new() { ["Tenancy:PortalHosts"] = "app.lms.example.org" } };
        Assert.Equal("portal", (await ReadAsync(await factory.CreateClient().GetAsync("/api/v1/public/site?host=app.lms.example.org"))).GetProperty("mode").GetString());
    }

    [Fact]
    public async Task An_installation_can_be_set_to_one_organization_so_every_address_shows_its_website()
    {
        using var factory = new LmsApiFactory { ExtraSettings = new() { ["Public:DefaultTenantSlug"] = "only-school" } };
        var platform = factory.CreateClient();
        platform.DefaultRequestHeaders.Add("X-Platform-Key", LmsApiFactory.PlatformKey);
        Assert.True((await platform.PostAsJsonAsync("/api/v1/platform/tenants", new { name = "Only School", slug = "only-school" })).IsSuccessStatusCode);
        var site = await ReadAsync(await factory.CreateClient().GetAsync("/api/v1/public/site?host=localhost:5173"));
        Assert.Equal("tenant", site.GetProperty("mode").GetString());
        Assert.Equal("Only School", site.GetProperty("organization").GetProperty("name").GetString());
    }

    [Fact]
    public async Task An_address_belongs_to_one_organization_and_the_operator_must_hold_the_platform_key()
    {
        var a = await _world.NewTenantAsync();
        var b = await _world.NewTenantAsync();
        var host = Unique("shared");
        Assert.Equal(HttpStatusCode.Created, (await AddDomain(a, host)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AddDomain(a, host.ToUpperInvariant())).StatusCode);   // again is fine
        Assert.Equal(HttpStatusCode.Conflict, (await AddDomain(b, host)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AddDomain(a, "https://nope.example.org/x")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AddDomain(a, "localhost")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Platform().PutAsJsonAsync("/api/v1/platform/tenants/no-such-org/domains", new { host = Unique("x") })).StatusCode);

        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync($"/api/v1/platform/tenants/{a.Slug}/domains", new { host = Unique("y") })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/platform/tenants/{a.Slug}/domains")).StatusCode);

        var listed = await ReadAsync(await Platform().GetAsync($"/api/v1/platform/tenants/{a.Slug}/domains"));
        Assert.Contains(host, listed.EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task Removing_an_address_returns_it_to_the_portal()
    {
        var t = await _world.NewTenantAsync();
        var host = Unique("gone");
        await AddDomain(t, host);
        Assert.Equal("tenant", (await SiteAsync(host)).GetProperty("mode").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await Platform().DeleteAsync($"/api/v1/platform/tenants/{t.Slug}/domains/{host}")).StatusCode);
        Assert.Equal("portal", (await SiteAsync(host)).GetProperty("mode").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await Platform().DeleteAsync($"/api/v1/platform/tenants/{t.Slug}/domains/{host}")).StatusCode);
    }
}
