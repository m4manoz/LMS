using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lms.Api.Infrastructure.Email;
using Microsoft.Extensions.DependencyInjection;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

/// <summary>Inviting people who have no account yet: the emailed code, and creating an account with it.</summary>
public sealed class InvitationSignupTests : IClassFixture<LmsApiFactory>
{
    private const string Password = "A-strong-pass-123";
    private readonly LmsApiFactory _factory;
    private readonly TestWorld _world;

    public InvitationSignupTests(LmsApiFactory factory) { _factory = factory; _world = new TestWorld(factory); }

    private static Task<HttpResponseMessage> EnableEmail(Tenant t)
        => t.Admin.PutAsJsonAsync("/api/v1/tenant/integrations/email", new { provider = "Log", enabled = true, fromAddress = "noreply@school.test", fromName = "School", smtpPort = 587, smtpUseSsl = true });

    private static async Task<JsonElement> InviteAsync(Tenant t, CourseInfo course, string email, string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tenant/invitations") { Content = JsonContent.Create(new { courseId = course.Id, email, message = "Welcome aboard" }) };
        if (origin is not null) request.Headers.Add("Origin", origin);
        var response = await t.Admin.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync(response);
    }

    private HttpClient Anonymous(Tenant t) => _factory.CreateTenantClient(t.Slug);

    private static Task<HttpResponseMessage> Lookup(HttpClient client, string? token) => client.PostAsJsonAsync("/api/v1/tenant/invitations/public/lookup", new { token });

    private static Task<HttpResponseMessage> Register(HttpClient client, string? token, string name = "Nia", string password = Password)
        => client.PostAsJsonAsync("/api/v1/tenant/invitations/public/register", new { token, displayName = name, password });

    private IReadOnlyList<OutgoingEmail> Mail(string to) => _factory.Services.GetRequiredService<EmailOutbox>().Snapshot().Where(item => item.To == to).ToList();

    // ---------- emailing the invitation ----------
    [Fact]
    public async Task An_invitation_to_a_new_address_is_emailed_with_the_code_and_a_sign_up_link()
    {
        var t = await _world.NewTenantAsync();
        await EnableEmail(t);
        var course = await _world.NewCourseAsync(t, "SU-1");
        var address = $"nia@{t.Slug}.test";

        var created = await InviteAsync(t, course, address, "https://learn.example.org");
        Assert.Equal("Sent", created.GetProperty("emailStatus").GetString());
        var token = created.GetProperty("token").GetString()!;
        var link = created.GetProperty("link").GetString()!;
        Assert.Equal($"https://learn.example.org/#invite={Uri.EscapeDataString(token)}&tenant={t.Slug}", link);

        var mail = Assert.Single(Mail(address));
        Assert.Contains(link, mail.Body);
        Assert.Contains(token, mail.Body);
        Assert.Contains("Welcome aboard", mail.Body);
        Assert.Contains(course.Title, mail.Subject);

        var sent = await ReadAsync(await t.Admin.GetAsync("/api/v1/tenant/invitations"));
        Assert.Equal("Sent", sent[0].GetProperty("emailStatus").GetString());
    }

    [Fact]
    public async Task Without_email_set_up_the_invitation_still_works_and_staff_are_told_to_share_the_code()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "SU-2");
        var created = await InviteAsync(t, course, $"nia@{t.Slug}.test");
        Assert.Equal("NotConfigured", created.GetProperty("emailStatus").GetString());
        Assert.False(string.IsNullOrEmpty(created.GetProperty("token").GetString()));
        Assert.Empty(Mail($"nia@{t.Slug}.test"));
    }

    [Fact]
    public async Task People_who_already_have_an_account_get_an_in_app_notice_and_no_emailed_code()
    {
        var t = await _world.NewTenantAsync();
        await EnableEmail(t);
        var course = await _world.NewCourseAsync(t, "SU-3");
        var ada = await _world.AddLearnerAsync(t, "Ada");
        var created = await InviteAsync(t, course, ada.Email);
        Assert.True(created.GetProperty("hasAccount").GetBoolean());
        Assert.Equal("NotNeeded", created.GetProperty("emailStatus").GetString());
        var token = created.GetProperty("token").GetString()!;
        Assert.DoesNotContain(Mail(ada.Email), mail => mail.Body.Contains(token));
    }

    // ---------- looking up an invitation ----------
    [Fact]
    public async Task The_code_shows_what_the_invitation_is_for_without_needing_to_sign_in()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "SU-4");
        var address = $"nia@{t.Slug}.test";
        var token = (await InviteAsync(t, course, address)).GetProperty("token").GetString()!;

        var preview = await ReadAsync(await Lookup(Anonymous(t), token));
        Assert.Equal(course.Title, preview.GetProperty("courseTitle").GetString());
        Assert.Equal(address, preview.GetProperty("email").GetString());
        Assert.False(preview.GetProperty("hasAccount").GetBoolean());
        Assert.Equal("Welcome aboard", preview.GetProperty("message").GetString());

        // A wrong code and a revoked code look the same.
        Assert.Equal(HttpStatusCode.NotFound, (await Lookup(Anonymous(t), "not-a-real-code")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Lookup(Anonymous(t), null)).StatusCode);
        var id = (await ReadAsync(await t.Admin.GetAsync("/api/v1/tenant/invitations")))[0].GetProperty("id").GetGuid();
        await t.Admin.PostAsync($"/api/v1/tenant/invitations/{id}/revoke", null);
        Assert.Equal(HttpStatusCode.NotFound, (await Lookup(Anonymous(t), token)).StatusCode);
    }

    [Fact]
    public async Task A_code_only_works_in_the_organization_that_issued_it()
    {
        var a = await _world.NewTenantAsync();
        var b = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(a, "SU-5");
        var token = (await InviteAsync(a, course, $"nia@{a.Slug}.test")).GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.NotFound, (await Lookup(Anonymous(b), token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Register(Anonymous(b), token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Lookup(Anonymous(a), token)).StatusCode);
    }

    [Fact]
    public async Task An_expired_code_is_refused()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "SU-6");
        var token = (await InviteAsync(t, course, $"nia@{t.Slug}.test")).GetProperty("token").GetString()!;
        await _world.WithDbAsync(t.Slug, async db =>
        {
            foreach (var invitation in db.CourseInvitations.ToList()) invitation.ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        });
        Assert.Equal(HttpStatusCode.NotFound, (await Lookup(Anonymous(t), token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Register(Anonymous(t), token)).StatusCode);
    }

    // ---------- registering ----------
    [Fact]
    public async Task Registering_with_the_code_creates_the_account_for_the_invited_address_and_enrolls_the_person()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "SU-7");
        var address = $"nia@{t.Slug}.test";
        var token = (await InviteAsync(t, course, address)).GetProperty("token").GetString()!;

        var response = await Register(Anonymous(t), token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal(address, body.GetProperty("email").GetString());
        Assert.Equal("Enrolled", body.GetProperty("outcome").GetString());

        // They can sign in straight away as a learner, and are enrolled.
        var nia = _factory.CreateTenantClient(t.Slug, await _factory.LoginAsync(t.Slug, address, Password));
        var me = await ReadAsync(await nia.GetAsync("/api/v1/tenant/me"));
        Assert.Equal("Nia", me.GetProperty("displayName").GetString());
        var enrollments = await ReadAsync(await nia.GetAsync("/api/v1/tenant/enrollments"));
        Assert.Equal("Invitation", enrollments[0].GetProperty("source").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await nia.PostAsJsonAsync("/api/v1/tenant/courses", new { code = "NOPE", title = "Nope" })).StatusCode); // a learner, nothing more

        // The code is single-use.
        Assert.Equal(HttpStatusCode.NotFound, (await Register(Anonymous(t), token, "Someone Else", "Another-pass-456")).StatusCode);
        var sent = await ReadAsync(await t.Admin.GetAsync("/api/v1/tenant/invitations"));
        Assert.Equal("Accepted", sent[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Registration_checks_the_name_and_password()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "SU-8");
        var token = (await InviteAsync(t, course, $"nia@{t.Slug}.test")).GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.BadRequest, (await Register(Anonymous(t), token, "Nia", "short")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Register(Anonymous(t), token, "  ")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Register(Anonymous(t), token, new string('x', 201))).StatusCode);
        // A refused attempt does not use up the code.
        Assert.Equal(HttpStatusCode.Created, (await Register(Anonymous(t), token)).StatusCode);
    }

    [Fact]
    public async Task Someone_who_already_has_an_account_is_sent_to_sign_in_and_nothing_is_changed()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "SU-9");
        var ada = await _world.AddLearnerAsync(t, "Ada");
        var token = (await InviteAsync(t, course, ada.Email)).GetProperty("token").GetString()!;

        Assert.True((await ReadAsync(await Lookup(Anonymous(t), token))).GetProperty("hasAccount").GetBoolean());
        var response = await Register(Anonymous(t), token, "Imposter", "Another-pass-456");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True((await ReadAsync(response)).GetProperty("accountExists").GetBoolean());
        // The real owner's password is untouched and the invitation is still usable from inside.
        await _factory.LoginAsync(t.Slug, ada.Email, LmsApiFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.OK, (await ada.Client.PostAsJsonAsync("/api/v1/tenant/invitations/accept", new { token })).StatusCode);
    }

    [Fact]
    public async Task Sending_again_replaces_the_earlier_code()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "SU-10");
        var address = $"nia@{t.Slug}.test";
        var first = (await InviteAsync(t, course, address)).GetProperty("token").GetString()!;
        var second = (await InviteAsync(t, course, address)).GetProperty("token").GetString()!;
        Assert.NotEqual(first, second);
        Assert.Equal(HttpStatusCode.NotFound, (await Register(Anonymous(t), first)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Register(Anonymous(t), second)).StatusCode);
    }

    [Fact]
    public async Task A_full_course_waitlists_the_new_person_and_missing_prerequisites_keep_the_invitation_open()
    {
        var t = await _world.NewTenantAsync();
        var full = await _world.NewCourseAsync(t, "SU-11", capacity: 1);
        var ada = await _world.AddLearnerAsync(t, "Ada");
        await EnrollAsync(ada, full);
        var waitToken = (await InviteAsync(t, full, $"nia@{t.Slug}.test")).GetProperty("token").GetString()!;
        var waitlisted = await ReadAsync(await Register(Anonymous(t), waitToken));
        Assert.Equal("Waitlisted", waitlisted.GetProperty("outcome").GetString());

        var basics = await _world.NewCourseAsync(t, "SU-12");
        var advanced = await _world.NewCourseAsync(t, "SU-13");
        Assert.Equal(HttpStatusCode.OK, (await t.Admin.PutAsJsonAsync($"/api/v1/tenant/courses/{advanced.Id}/prerequisites", new { courseIds = new[] { basics.Id } })).StatusCode);
        var gatedToken = (await InviteAsync(t, advanced, $"omar@{t.Slug}.test")).GetProperty("token").GetString()!;
        var gated = await ReadAsync(await Register(Anonymous(t), gatedToken, "Omar"));
        Assert.Equal("NotEnrolled", gated.GetProperty("outcome").GetString());
        Assert.False(string.IsNullOrEmpty(gated.GetProperty("message").GetString()));
        // The account exists and the invitation is still waiting for them.
        var omar = _factory.CreateTenantClient(t.Slug, await _factory.LoginAsync(t.Slug, $"omar@{t.Slug}.test", Password));
        Assert.Single((await ReadAsync(await omar.GetAsync("/api/v1/tenant/invitations/mine"))).EnumerateArray());
    }

    [Fact]
    public async Task The_stored_code_is_a_hash_and_staff_without_enrollment_rights_cannot_invite()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "SU-14");
        var token = (await InviteAsync(t, course, $"nia@{t.Slug}.test")).GetProperty("token").GetString()!;
        await _world.WithDbAsync(t.Slug, db =>
        {
            Assert.DoesNotContain(db.CourseInvitations.ToList(), item => item.TokenHash == token || (item.EmailError ?? "").Contains(token));
            return Task.CompletedTask;
        });
        var ada = await _world.AddLearnerAsync(t, "Ada");
        Assert.Equal(HttpStatusCode.Forbidden, (await ada.Client.PostAsJsonAsync("/api/v1/tenant/invitations", new { courseId = course.Id, email = "x@y.test" })).StatusCode);
    }
}
