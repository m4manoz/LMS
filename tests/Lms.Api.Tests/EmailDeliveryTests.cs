using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lms.Api.Infrastructure.Email;
using Lms.Api.Infrastructure.Notifications;
using Lms.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lms.Api.Tests;

public sealed class EmailDeliveryTests : IClassFixture<LmsApiFactory>
{
    private readonly LmsApiFactory _factory;
    public EmailDeliveryTests(LmsApiFactory factory) => _factory = factory;

    private sealed record Setup(string Slug, HttpClient Admin, HttpClient Learner, string AdminEmail, string LearnerEmail);

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<Setup> CreateAsync(LmsApiFactory? factory = null)
    {
        factory ??= _factory;
        var (slug, adminEmail, adminToken) = await factory.ProvisionTenantWithAdminAsync();
        var admin = factory.CreateTenantClient(slug, adminToken);
        var learnerEmail = $"lena@{slug}.test";
        (await admin.PostAsJsonAsync("/api/v1/tenant/users", new { email = learnerEmail, displayName = "Lena", password = LmsApiFactory.AdminPassword, roleCode = "LEARNER" })).EnsureSuccessStatusCode();
        var learner = factory.CreateTenantClient(slug, await factory.LoginAsync(slug, learnerEmail, LmsApiFactory.AdminPassword));
        return new Setup(slug, admin, learner, adminEmail, learnerEmail);
    }

    private static Task<HttpResponseMessage> SaveAsync(HttpClient admin, object body) => admin.PutAsJsonAsync("/api/v1/tenant/integrations/email", body);

    private static object LogSettings(bool enabled = true) => new { provider = "Log", enabled, fromAddress = "noreply@school.test", fromName = "School", smtpPort = 587, smtpUseSsl = true };

    private async Task<int> DispatchAsync(LmsApiFactory? factory = null)
    {
        using var scope = (factory ?? _factory).Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<NotificationDispatcher>().DispatchBatchAsync(scope.ServiceProvider.GetRequiredService<LmsDbContext>(), CancellationToken.None, 500);
    }

    private static async Task<JsonElement> OutboxAsync(HttpClient admin) => await ReadAsync(await admin.GetAsync("/api/v1/tenant/integrations/email/outbox"));

    // ---------- settings ----------
    [Fact]
    public async Task Defaults_are_off_and_only_administrators_can_see_or_change_settings()
    {
        var s = await CreateAsync();
        var defaults = await ReadAsync(await s.Admin.GetAsync("/api/v1/tenant/integrations/email"));
        Assert.False(defaults.GetProperty("enabled").GetBoolean());
        Assert.Equal("Log", defaults.GetProperty("provider").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await s.Learner.GetAsync("/api/v1/tenant/integrations/email")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SaveAsync(s.Learner, LogSettings())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Learner.PostAsync("/api/v1/tenant/integrations/email/test", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Learner.GetAsync("/api/v1/tenant/integrations/email/outbox")).StatusCode);
    }

    [Fact]
    public async Task Settings_are_validated()
    {
        var s = await CreateAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await SaveAsync(s.Admin, new { provider = "Carrier pigeon", enabled = true, fromAddress = "a@b.co", smtpPort = 587 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SaveAsync(s.Admin, new { provider = "Log", enabled = true, fromAddress = "not-an-email", smtpPort = 587 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SaveAsync(s.Admin, new { provider = "Smtp", enabled = true, fromAddress = "a@b.co", smtpPort = 587 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SaveAsync(s.Admin, new { provider = "Smtp", enabled = true, fromAddress = "a@b.co", smtpHost = "mail.example.org", smtpPort = 9999 })).StatusCode);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("192.168.0.20")]
    [InlineData("169.254.169.254")]
    [InlineData("db.internal")]
    public async Task Internal_smtp_hosts_are_rejected_to_prevent_server_side_request_forgery(string host)
    {
        var s = await CreateAsync();
        var response = await SaveAsync(s.Admin, new { provider = "Smtp", enabled = true, fromAddress = "a@b.co", smtpHost = host, smtpPort = 587 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void Private_address_ranges_are_recognised()
    {
        Assert.True(SmtpHostPolicy.IsPrivate(IPAddress.Parse("172.20.0.1")));
        Assert.True(SmtpHostPolicy.IsPrivate(IPAddress.Parse("::1")));
        Assert.True(SmtpHostPolicy.IsPrivate(IPAddress.Parse("fd00::1")));
        Assert.True(SmtpHostPolicy.IsPrivate(IPAddress.Parse("::ffff:10.0.0.1")));
        Assert.False(SmtpHostPolicy.IsPrivate(IPAddress.Parse("8.8.8.8")));
        Assert.False(SmtpHostPolicy.IsPrivate(IPAddress.Parse("172.32.0.1")));
    }

    [Fact]
    public async Task Password_is_stored_protected_never_returned_and_can_be_kept_or_cleared()
    {
        var s = await CreateAsync();
        var saved = await SaveAsync(s.Admin, new { provider = "Log", enabled = true, fromAddress = "a@b.co", smtpUsername = "mailer", smtpPassword = "s3cret-pass", smtpPort = 587 });
        var text = await saved.Content.ReadAsStringAsync();
        Assert.DoesNotContain("s3cret-pass", text);
        Assert.True(JsonDocument.Parse(text).RootElement.GetProperty("hasPassword").GetBoolean());

        var keep = await ReadAsync(await SaveAsync(s.Admin, new { provider = "Log", enabled = true, fromAddress = "a@b.co", smtpPort = 587 }));
        Assert.True(keep.GetProperty("hasPassword").GetBoolean());
        Assert.DoesNotContain("s3cret-pass", await (await s.Admin.GetAsync("/api/v1/tenant/integrations/email")).Content.ReadAsStringAsync());

        var cleared = await ReadAsync(await SaveAsync(s.Admin, new { provider = "Log", enabled = true, fromAddress = "a@b.co", smtpPassword = "", smtpPort = 587 }));
        Assert.False(cleared.GetProperty("hasPassword").GetBoolean());
    }

    [Fact]
    public async Task Settings_are_isolated_between_tenants()
    {
        var a = await CreateAsync();
        var b = await CreateAsync();
        (await SaveAsync(a.Admin, LogSettings())).EnsureSuccessStatusCode();
        var other = await ReadAsync(await b.Admin.GetAsync("/api/v1/tenant/integrations/email"));
        Assert.False(other.GetProperty("enabled").GetBoolean());
        Assert.Equal(string.Empty, other.GetProperty("fromAddress").GetString());
    }

    // ---------- test email ----------
    [Fact]
    public async Task Test_email_needs_saved_settings_and_then_reaches_the_administrator()
    {
        var s = await CreateAsync();
        var first = await ReadAsync(await s.Admin.PostAsync("/api/v1/tenant/integrations/email/test", null));
        Assert.False(first.GetProperty("success").GetBoolean());

        (await SaveAsync(s.Admin, LogSettings(enabled: false))).EnsureSuccessStatusCode(); // works even while delivery is off
        var second = await ReadAsync(await s.Admin.PostAsync("/api/v1/tenant/integrations/email/test", null));
        Assert.True(second.GetProperty("success").GetBoolean());
        Assert.Contains(_factory.Services.GetRequiredService<EmailOutbox>().Snapshot(), mail => mail.To == s.AdminEmail && mail.Subject.StartsWith("Test email"));
    }

    // ---------- delivery ----------
    [Fact]
    public async Task Announcement_is_emailed_to_members_and_the_in_app_inbox_is_unaffected()
    {
        var s = await CreateAsync();
        (await SaveAsync(s.Admin, LogSettings())).EnsureSuccessStatusCode();
        (await s.Admin.PostAsJsonAsync("/api/v1/tenant/community/announcements", new { title = "Exam timetable", body = "Exams start on Monday." })).EnsureSuccessStatusCode();
        await DispatchAsync();

        var mail = Assert.Single(_factory.Services.GetRequiredService<EmailOutbox>().Snapshot(), item => item.To == s.LearnerEmail);
        Assert.Equal("Exam timetable", mail.Subject);
        Assert.Equal("Exams start on Monday.", mail.Body);

        var log = await OutboxAsync(s.Admin);
        Assert.All(log.EnumerateArray(), item => Assert.Equal("Sent", item.GetProperty("status").GetString()));

        var inbox = await ReadAsync(await s.Learner.GetAsync("/api/v1/tenant/notifications"));
        Assert.Single(inbox.EnumerateArray()); // the email copy is not shown in the in-app list
    }

    [Fact]
    public async Task Nothing_is_emailed_while_email_is_off()
    {
        var s = await CreateAsync();
        (await SaveAsync(s.Admin, LogSettings(enabled: false))).EnsureSuccessStatusCode();
        (await s.Admin.PostAsJsonAsync("/api/v1/tenant/community/announcements", new { title = "Quiet", body = "No mail" })).EnsureSuccessStatusCode();
        await DispatchAsync();
        Assert.Equal(0, (await OutboxAsync(s.Admin)).GetArrayLength());
    }

    [Fact]
    public async Task Learners_can_opt_out_of_email_for_one_notification_type()
    {
        var s = await CreateAsync();
        (await SaveAsync(s.Admin, LogSettings())).EnsureSuccessStatusCode();
        (await s.Learner.PutAsJsonAsync("/api/v1/tenant/notification-preferences", new { templateCode = "ANNOUNCEMENT", channel = "Email", enabled = false })).EnsureSuccessStatusCode();
        (await s.Admin.PostAsJsonAsync("/api/v1/tenant/community/announcements", new { title = "No email for Lena", body = "x" })).EnsureSuccessStatusCode();
        await DispatchAsync();

        Assert.DoesNotContain(_factory.Services.GetRequiredService<EmailOutbox>().Snapshot(), mail => mail.To == s.LearnerEmail && mail.Subject == "No email for Lena");
        var inbox = await ReadAsync(await s.Learner.GetAsync("/api/v1/tenant/notifications"));
        Assert.Single(inbox.EnumerateArray()); // still in the in-app inbox
    }

    [Fact]
    public async Task Line_breaks_in_subjects_cannot_inject_headers()
    {
        var s = await CreateAsync();
        (await SaveAsync(s.Admin, LogSettings())).EnsureSuccessStatusCode();
        (await s.Admin.PostAsJsonAsync("/api/v1/tenant/community/announcements", new { title = "Hello\r\nBcc: attacker@evil.test", body = "x" })).EnsureSuccessStatusCode();
        await DispatchAsync();
        var mail = _factory.Services.GetRequiredService<EmailOutbox>().Snapshot().Single(item => item.To == s.LearnerEmail);
        Assert.DoesNotContain('\n', mail.Subject);
        Assert.DoesNotContain('\r', mail.Subject);
    }

    [Fact]
    public async Task Turning_email_off_after_queueing_dead_letters_the_messages_without_retrying()
    {
        var s = await CreateAsync();
        (await SaveAsync(s.Admin, LogSettings())).EnsureSuccessStatusCode();
        (await s.Admin.PostAsJsonAsync("/api/v1/tenant/community/announcements", new { title = "Late switch-off", body = "x" })).EnsureSuccessStatusCode();
        (await SaveAsync(s.Admin, LogSettings(enabled: false))).EnsureSuccessStatusCode();
        await DispatchAsync();

        var log = await OutboxAsync(s.Admin);
        Assert.All(log.EnumerateArray(), item =>
        {
            Assert.Equal("DeadLetter", item.GetProperty("status").GetString());
            Assert.Contains("turned off", item.GetProperty("lastError").GetString());
        });
    }

    [Fact]
    public async Task Failed_smtp_delivery_is_retried_later_not_immediately()
    {
        // A real SmtpClient against a closed local port; private hosts are only allowed in this test host.
        await using var factory = new LmsApiFactory { AllowPrivateSmtpHosts = true };
        var s = await CreateAsync(factory);
        var admin = s.Admin;
        (await SaveAsync(admin, new { provider = "Smtp", enabled = true, fromAddress = "noreply@school.test", smtpHost = "127.0.0.1", smtpPort = 1, smtpUseSsl = false })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync("/api/v1/tenant/community/announcements", new { title = "Will fail", body = "x" })).EnsureSuccessStatusCode();

        await DispatchAsync(factory);
        var afterFirst = (await OutboxAsync(admin)).EnumerateArray().First();
        Assert.Equal("Failed", afterFirst.GetProperty("status").GetString());
        Assert.Equal(1, afterFirst.GetProperty("attemptCount").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(afterFirst.GetProperty("lastError").GetString()));

        await DispatchAsync(factory); // back-off: not due yet, so no second attempt
        Assert.Equal(1, (await OutboxAsync(admin)).EnumerateArray().First().GetProperty("attemptCount").GetInt32());
    }

    [Fact]
    public async Task Deadline_reminders_produce_a_single_email()
    {
        var s = await CreateAsync();
        (await SaveAsync(s.Admin, LogSettings())).EnsureSuccessStatusCode();
        var course = await ReadAsync(await s.Admin.PostAsJsonAsync("/api/v1/tenant/courses", new { code = "BIO-1", title = "Biology" }));
        var courseId = course.GetProperty("course").GetProperty("id").GetGuid();
        (await s.Admin.PostAsync($"/api/v1/tenant/courses/{courseId}/submit-review", null)).EnsureSuccessStatusCode();
        (await s.Admin.PostAsync($"/api/v1/tenant/courses/{courseId}/publish", null)).EnsureSuccessStatusCode();
        (await s.Learner.PostAsync($"/api/v1/tenant/courses/{courseId}/enroll", null)).EnsureSuccessStatusCode();
        var assignment = await ReadAsync(await s.Admin.PostAsJsonAsync("/api/v1/tenant/assignments", new { courseId, title = "Cell diagram", maxPoints = 10, dueAtUtc = DateTimeOffset.UtcNow.AddHours(10) }));
        (await s.Admin.PostAsync($"/api/v1/tenant/assignments/{assignment.GetProperty("id").GetGuid()}/publish", null)).EnsureSuccessStatusCode();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LmsDbContext>();
            var tenant = db.Tenants.IgnoreQueryFilters().Single(item => item.Slug == s.Slug);
            ((Lms.Api.Infrastructure.Tenancy.TenantContext)scope.ServiceProvider.GetRequiredService<Lms.Api.Infrastructure.Tenancy.ITenantContext>()).Set(tenant.Id, tenant.Slug);
            var reminders = scope.ServiceProvider.GetRequiredService<DeadlineReminderService>();
            await reminders.RunAsync(db, tenant.Id, DateTimeOffset.UtcNow, TimeSpan.FromHours(24), CancellationToken.None);
            await reminders.RunAsync(db, tenant.Id, DateTimeOffset.UtcNow, TimeSpan.FromHours(24), CancellationToken.None);
        }
        await DispatchAsync();

        Assert.Single(_factory.Services.GetRequiredService<EmailOutbox>().Snapshot(), mail => mail.To == s.LearnerEmail && mail.Subject.StartsWith("Upcoming deadline"));
    }
}
