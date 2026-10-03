using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Lms.Api.Infrastructure.Email;
using Microsoft.Extensions.DependencyInjection;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

/// <summary>Forgotten passwords: emailed single-use codes.</summary>
public sealed class PasswordResetTests : IClassFixture<LmsApiFactory>
{
    private const string NewPassword = "Brand-new-pass-789";
    private readonly LmsApiFactory _factory;
    private readonly TestWorld _world;

    public PasswordResetTests(LmsApiFactory factory) { _factory = factory; _world = new TestWorld(factory); }

    private static Task<HttpResponseMessage> EnableEmail(Tenant t)
        => t.Admin.PutAsJsonAsync("/api/v1/tenant/integrations/email", new { provider = "Log", enabled = true, fromAddress = "noreply@school.test", fromName = "School", smtpPort = 587, smtpUseSsl = true });

    private HttpClient Anon() => _factory.CreateClient();

    private static Task<HttpResponseMessage> Request(HttpClient c, string slug, string email, string? origin = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/password-reset/request") { Content = JsonContent.Create(new { tenantSlug = slug, email }) };
        if (origin is not null) message.Headers.Add("Origin", origin);
        return c.SendAsync(message);
    }

    private static Task<HttpResponseMessage> Confirm(HttpClient c, string slug, string? token, string password = NewPassword)
        => c.PostAsJsonAsync("/api/v1/auth/password-reset/confirm", new { tenantSlug = slug, token, newPassword = password });

    private IReadOnlyList<OutgoingEmail> Mail(string to) => _factory.Services.GetRequiredService<EmailOutbox>().Snapshot().Where(item => item.To == to).ToList();

    /// <summary>The code from the most recent reset email to an address.</summary>
    private string CodeFor(string email)
    {
        var body = Mail(email).Last(item => item.Subject.StartsWith("Reset your")).Body;
        return Regex.Matches(body, @"[A-Za-z0-9_-]{40,}").Last().Value;
    }

    [Fact]
    public async Task A_reset_code_is_emailed_and_sets_a_new_password_once()
    {
        var t = await _world.NewTenantAsync();
        await EnableEmail(t);
        var ada = await _world.AddLearnerAsync(t, "Ada");

        var response = await Request(Anon(), t.Slug, ada.Email);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var code = CodeFor(ada.Email);
        Assert.Contains("60 minutes", Mail(ada.Email).Last().Body);

        Assert.Equal(HttpStatusCode.NoContent, (await Confirm(Anon(), t.Slug, code)).StatusCode);
        await _factory.LoginAsync(t.Slug, ada.Email, NewPassword);                                  // the new password works
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anon().PostAsJsonAsync("/api/v1/auth/login", new { tenantSlug = t.Slug, email = ada.Email, password = LmsApiFactory.AdminPassword })).StatusCode); // the old one does not
        Assert.Equal(HttpStatusCode.BadRequest, (await Confirm(Anon(), t.Slug, code, "Yet-another-pass-1")).StatusCode); // single use
    }

    [Fact]
    public async Task The_answer_is_the_same_whether_or_not_the_account_exists_and_nothing_is_sent_for_strangers()
    {
        var t = await _world.NewTenantAsync();
        await EnableEmail(t);
        var known = await Request(Anon(), t.Slug, t.AdminEmail);
        var unknown = await Request(Anon(), t.Slug, $"nobody@{t.Slug}.test");
        var noTenant = await Request(Anon(), "no-such-organization", t.AdminEmail);
        Assert.Equal(HttpStatusCode.Accepted, known.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, noTenant.StatusCode);
        Assert.Equal(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        Assert.Equal(await known.Content.ReadAsStringAsync(), await noTenant.Content.ReadAsStringAsync());
        Assert.Empty(Mail($"nobody@{t.Slug}.test"));
        Assert.Equal(HttpStatusCode.BadRequest, (await Request(Anon(), t.Slug, "not-an-email")).StatusCode);
    }

    [Fact]
    public async Task Nothing_is_sent_when_the_organization_has_email_off_and_a_member_of_another_organization_cannot_ask()
    {
        var a = await _world.NewTenantAsync();
        var b = await _world.NewTenantAsync();
        await EnableEmail(b);
        var adaA = await _world.AddLearnerAsync(a, "Ada");
        await Request(Anon(), a.Slug, adaA.Email);          // email off in a
        Assert.Empty(Mail(adaA.Email));
        await Request(Anon(), b.Slug, adaA.Email);          // she is not a member of b
        Assert.Empty(Mail(adaA.Email));
    }

    [Fact]
    public async Task The_link_in_the_email_never_follows_a_request_header()
    {
        var t = await _world.NewTenantAsync();
        await EnableEmail(t);
        var ada = await _world.AddLearnerAsync(t, "Ada");
        await Request(Anon(), t.Slug, ada.Email, "https://evil.example");
        var body = Mail(ada.Email).Last().Body;
        Assert.DoesNotContain("evil.example", body);
        Assert.Contains("Forgot your password?", body);   // no configured public address, so the email carries the code only
    }

    [Fact]
    public async Task Codes_are_throttled_replaced_expired_and_checked_per_organization()
    {
        var a = await _world.NewTenantAsync();
        var b = await _world.NewTenantAsync();
        await EnableEmail(a);
        var ada = await _world.AddLearnerAsync(a, "Ada");

        await Request(Anon(), a.Slug, ada.Email);
        var first = CodeFor(ada.Email);
        await Request(Anon(), a.Slug, ada.Email);                       // inside a minute: no second message
        Assert.Single(Mail(ada.Email), item => item.Subject.StartsWith("Reset your"));

        Assert.Equal(HttpStatusCode.BadRequest, (await Confirm(Anon(), b.Slug, first)).StatusCode);   // wrong organization
        Assert.Equal(HttpStatusCode.BadRequest, (await Confirm(Anon(), a.Slug, "nonsense")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Confirm(Anon(), a.Slug, null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Confirm(Anon(), a.Slug, first, "short")).StatusCode); // weak password is refused...

        await _world.WithDbAsync(a.Slug, async db =>
        {
            foreach (var token in db.PasswordResetTokens.ToList()) token.ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        });
        Assert.Equal(HttpStatusCode.BadRequest, (await Confirm(Anon(), a.Slug, first)).StatusCode);   // ...and so is an expired code
    }

    [Fact]
    public async Task A_weak_password_does_not_use_up_the_code_and_a_reset_signs_the_person_out_everywhere()
    {
        var t = await _world.NewTenantAsync();
        await EnableEmail(t);
        var ada = await _world.AddLearnerAsync(t, "Ada");
        var login = await ReadAsync(await Anon().PostAsJsonAsync("/api/v1/auth/login", new { tenantSlug = t.Slug, email = ada.Email, password = LmsApiFactory.AdminPassword }));
        var refresh = login.GetProperty("refreshToken").GetString();

        await Request(Anon(), t.Slug, ada.Email);
        var code = CodeFor(ada.Email);
        Assert.Equal(HttpStatusCode.BadRequest, (await Confirm(Anon(), t.Slug, code, "short")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Confirm(Anon(), t.Slug, code)).StatusCode);

        var refreshed = await _factory.CreateTenantClient(t.Slug).PostAsJsonAsync("/api/v1/auth/refresh", new { tenantSlug = t.Slug, refreshToken = refresh });
        Assert.Equal(HttpStatusCode.Unauthorized, refreshed.StatusCode);
    }

    [Fact]
    public async Task Only_a_hash_of_the_code_is_stored()
    {
        var t = await _world.NewTenantAsync();
        await EnableEmail(t);
        var ada = await _world.AddLearnerAsync(t, "Ada");
        await Request(Anon(), t.Slug, ada.Email);
        var code = CodeFor(ada.Email);
        await _world.WithDbAsync(t.Slug, db =>
        {
            Assert.DoesNotContain(db.PasswordResetTokens.ToList(), item => item.TokenHash == code);
            Assert.Single(db.PasswordResetTokens.ToList());
            return Task.CompletedTask;
        });
    }
}
