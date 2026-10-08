using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Lms.Api.Tests;

/// <summary>A signed-in person changes their own password.</summary>
public sealed class ChangePasswordTests : IClassFixture<LmsApiFactory>
{
    private const string NewPassword = "Another-Passw0rd!";
    private readonly LmsApiFactory _factory;
    public ChangePasswordTests(LmsApiFactory factory) => _factory = factory;

    private async Task<JsonElement> SignInAsync(string slug, string email, string password)
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { tenantSlug = slug, email, password });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private Task<HttpResponseMessage> ChangeAsync(string slug, string token, string? current, string? next)
        => _factory.CreateTenantClient(slug, token).PostAsJsonAsync("/api/v1/tenant/me/password", new { currentPassword = current, newPassword = next });

    [Fact]
    public async Task Changing_the_password_needs_a_signed_in_person()
    {
        var (slug, _, _) = await _factory.ProvisionTenantWithAdminAsync();
        var response = await _factory.CreateTenantClient(slug).PostAsJsonAsync("/api/v1/tenant/me/password", new { currentPassword = LmsApiFactory.AdminPassword, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_new_password_works_and_the_old_one_does_not()
    {
        var (slug, email, token) = await _factory.ProvisionTenantWithAdminAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await ChangeAsync(slug, token, LmsApiFactory.AdminPassword, NewPassword)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { tenantSlug = slug, email, password = LmsApiFactory.AdminPassword })).StatusCode);
        Assert.False(string.IsNullOrEmpty((await SignInAsync(slug, email, NewPassword)).GetProperty("accessToken").GetString()));
    }

    [Fact]
    public async Task A_wrong_current_password_changes_nothing_and_is_not_a_sign_out()
    {
        var (slug, email, token) = await _factory.ProvisionTenantWithAdminAsync();
        var response = await ChangeAsync(slug, token, "not-the-password", NewPassword);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);   // never 401: the app would treat the session as expired
        Assert.Contains("current password", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
        await SignInAsync(slug, email, LmsApiFactory.AdminPassword);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData(LmsApiFactory.AdminPassword)]
    public async Task The_new_password_must_be_valid_and_different(string? next)
    {
        var (slug, email, token) = await _factory.ProvisionTenantWithAdminAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await ChangeAsync(slug, token, LmsApiFactory.AdminPassword, next)).StatusCode);
        await SignInAsync(slug, email, LmsApiFactory.AdminPassword);
    }

    [Fact]
    public async Task Other_sessions_are_signed_out_but_this_one_stays()
    {
        var (slug, email, _) = await _factory.ProvisionTenantWithAdminAsync();
        var thisSession = await SignInAsync(slug, email, LmsApiFactory.AdminPassword);
        var otherSession = await SignInAsync(slug, email, LmsApiFactory.AdminPassword);

        var change = await ChangeAsync(slug, thisSession.GetProperty("accessToken").GetString()!, LmsApiFactory.AdminPassword, NewPassword);
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        var kept = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/refresh", new { tenantSlug = slug, refreshToken = thisSession.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        var ended = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/refresh", new { tenantSlug = slug, refreshToken = otherSession.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.Unauthorized, ended.StatusCode);
    }

    [Fact]
    public async Task Someone_elses_password_is_untouched()
    {
        var (slug, email, token) = await _factory.ProvisionTenantWithAdminAsync();
        var other = await _factory.ProvisionTenantWithAdminAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await ChangeAsync(slug, token, LmsApiFactory.AdminPassword, NewPassword)).StatusCode);
        await SignInAsync(other.Slug, other.Email, LmsApiFactory.AdminPassword);
        await SignInAsync(slug, email, NewPassword);
    }
}
