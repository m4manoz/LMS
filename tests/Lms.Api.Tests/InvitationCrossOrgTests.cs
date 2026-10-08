using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

/// <summary>A person whose email already has an account in another organization can still join this one with an invitation.</summary>
public sealed class InvitationCrossOrgTests : IClassFixture<LmsApiFactory>
{
    private readonly LmsApiFactory _factory;
    private readonly TestWorld _world;
    public InvitationCrossOrgTests(LmsApiFactory factory) { _factory = factory; _world = new TestWorld(factory); }

    private async Task<string> InviteAsync(Tenant tenant, CourseInfo course, string email)
    {
        var response = await tenant.Admin.PostAsJsonAsync("/api/v1/tenant/invitations", new { courseId = course.Id, email });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("token").GetString()!;
    }

    private HttpClient Anonymous(Tenant tenant) => _factory.CreateTenantClient(tenant.Slug);
    private static Task<HttpResponseMessage> Lookup(HttpClient client, string token) => client.PostAsJsonAsync("/api/v1/tenant/invitations/public/lookup", new { token });
    private static Task<HttpResponseMessage> Register(HttpClient client, string token, string? password, string? name = null) => client.PostAsJsonAsync("/api/v1/tenant/invitations/public/register", new { token, displayName = name, password });

    /// <summary>Ada belongs to the first organization; the second invites her by the same address.</summary>
    private async Task<(Tenant Home, Person Ada, Tenant Other, CourseInfo Course, string Token)> NewSituationAsync()
    {
        var home = await _world.NewTenantAsync();
        var ada = await _world.AddLearnerAsync(home, "Ada");
        var other = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(other, "XORG");
        return (home, ada, other, course, await InviteAsync(other, course, ada.Email));
    }

    [Fact]
    public async Task The_invitation_says_the_account_exists_elsewhere_and_that_she_is_not_a_member_here()
    {
        var (_, _, other, _, token) = await NewSituationAsync();
        var preview = await ReadAsync(await Lookup(Anonymous(other), token));
        Assert.True(preview.GetProperty("hasAccount").GetBoolean());
        Assert.False(preview.GetProperty("isMember").GetBoolean());
    }

    [Fact]
    public async Task The_existing_password_lets_her_join_and_be_enrolled_with_the_same_account()
    {
        var (home, ada, other, course, token) = await NewSituationAsync();
        var joined = await Register(Anonymous(other), token, LmsApiFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.Created, joined.StatusCode);
        Assert.Equal("Enrolled", (await ReadAsync(joined)).GetProperty("outcome").GetString());

        // She signs in to the new organization with the password she already had, as the same person.
        var client = _factory.CreateTenantClient(other.Slug, await _factory.LoginAsync(other.Slug, ada.Email, LmsApiFactory.AdminPassword));
        Assert.Equal((await ReadAsync(await ada.Client.GetAsync("/api/v1/tenant/me"))).GetProperty("userId").GetString(),
            (await ReadAsync(await client.GetAsync("/api/v1/tenant/me"))).GetProperty("userId").GetString());
        var enrollments = await ReadAsync(await client.GetAsync("/api/v1/tenant/enrollments"));
        Assert.Contains(enrollments.EnumerateArray(), item => item.GetProperty("courseId").GetGuid() == course.Id);

        // She is a learner there (not an administrator) and her home organization is untouched.
        var roles = (await ReadAsync(await client.GetAsync("/api/v1/tenant/me"))).GetProperty("roles").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Equal(new[] { "LEARNER" }, roles);
        Assert.Equal(HttpStatusCode.OK, (await ada.Client.GetAsync("/api/v1/tenant/me")).StatusCode);
        Assert.Contains("Ada", (await ReadAsync(await home.Admin.GetAsync("/api/v1/tenant/users"))).EnumerateArray().Select(item => item.GetProperty("displayName").GetString()));
    }

    [Fact]
    public async Task A_wrong_or_missing_password_adds_nothing()
    {
        var (_, ada, other, _, token) = await NewSituationAsync();
        foreach (var attempt in new string?[] { "Not-her-password-1", null, "" })
        {
            var refused = await Register(Anonymous(other), token, attempt);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.True((await ReadAsync(refused)).GetProperty("needsExistingPassword").GetBoolean());
        }
        var login = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { tenantSlug = other.Slug, email = ada.Email, password = LmsApiFactory.AdminPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);   // still not a member of the second organization
        Assert.Empty((await ReadAsync(await other.Admin.GetAsync("/api/v1/tenant/users"))).EnumerateArray().Where(item => item.GetProperty("email").GetString() == ada.Email));
    }

    [Fact]
    public async Task The_code_works_only_once_and_a_name_typed_in_is_ignored()
    {
        var (_, ada, other, _, token) = await NewSituationAsync();
        Assert.Equal(HttpStatusCode.Created, (await Register(Anonymous(other), token, LmsApiFactory.AdminPassword, "Someone Else")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Register(Anonymous(other), token, LmsApiFactory.AdminPassword)).StatusCode);   // used up
        var mine = await ReadAsync(await ada.Client.GetAsync("/api/v1/tenant/me"));
        Assert.Equal("Ada", mine.GetProperty("displayName").GetString());   // her name was not replaced
    }

    [Fact]
    public async Task Someone_already_in_the_organization_is_still_told_to_sign_in_and_cannot_be_taken_over()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "MEMBER");
        var ada = await _world.AddLearnerAsync(t, "Ada");
        var token = await InviteAsync(t, course, ada.Email);
        var preview = await ReadAsync(await Lookup(Anonymous(t), token));
        Assert.True(preview.GetProperty("isMember").GetBoolean());
        var response = await Register(Anonymous(t), token, "Another-pass-456", "Imposter");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True((await ReadAsync(response)).GetProperty("accountExists").GetBoolean());
        await _factory.LoginAsync(t.Slug, ada.Email, LmsApiFactory.AdminPassword);   // her password is untouched
    }

    [Fact]
    public async Task A_brand_new_address_still_creates_an_account_as_before()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "NEWBIE");
        var address = $"nia@{t.Slug}.test";
        var token = await InviteAsync(t, course, address);
        var preview = await ReadAsync(await Lookup(Anonymous(t), token));
        Assert.False(preview.GetProperty("hasAccount").GetBoolean());
        Assert.Equal(HttpStatusCode.Created, (await Register(Anonymous(t), token, "Nia-pass-12345", "Nia")).StatusCode);
        await _factory.LoginAsync(t.Slug, address, "Nia-pass-12345");
    }
}
