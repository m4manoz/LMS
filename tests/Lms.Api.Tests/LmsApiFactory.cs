using System.Net.Http.Headers;
using System.Net.Http.Json;
using Lms.Api.Infrastructure.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;

namespace Lms.Api.Tests;

public sealed class LmsApiFactory : WebApplicationFactory<Program>
{
    public const string PlatformKey = "test-platform-key-0001";
    public const string AdminPassword = "Passw0rd!123-test";

    /// <summary>Lets a test point SMTP at a local (closed) port; production keeps this off to prevent SSRF.</summary>
    public bool AllowPrivateSmtpHosts { get; init; }

    /// <summary>Runs the host with the S3 storage provider backed by <see cref="Store"/> instead of local disk.</summary>
    public bool UseFakeObjectStorage { get; init; }
    /// <summary>Configures the S3 provider without a bucket, which must stop the host from starting.</summary>
    public bool BrokenS3Configuration { get; init; }
    /// <summary>Runs the host against the real S3-compatible server described by the LMS_TEST_S3_* environment variables.</summary>
    public bool UseRealObjectStorage { get; init; }
    public InMemoryObjectStore Store { get; } = new();
    /// <summary>Replaces FFmpeg, so video conversion can be tested without it.</summary>
    public Lms.Api.Infrastructure.Videos.IVideoTranscoder? Transcoder { get; init; }
    /// <summary>Answers every outgoing HTTP call the API makes (the AI service), so nothing real is contacted.</summary>
    public HttpMessageHandler? HttpHandler { get; init; }
    /// <summary>Replaces LiveKit's recording service.</summary>
    public Lms.Api.Infrastructure.LiveClasses.ILiveKitEgressClient? Egress { get; init; }
    /// <summary>Replaces LiveKit's room service (who is in a room, muting, closing).</summary>
    public Lms.Api.Infrastructure.LiveClasses.ILiveKitRoomClient? Rooms { get; init; }
    /// <summary>More configuration for this host, applied after the usual test settings.</summary>
    public Dictionary<string, string?>? ExtraSettings { get; init; }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        if (UseRealObjectStorage)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Provider"] = "S3",
                ["Storage:S3:ServiceUrl"] = Environment.GetEnvironmentVariable("LMS_TEST_S3_URL"),
                ["Storage:S3:AccessKeyId"] = Environment.GetEnvironmentVariable("LMS_TEST_S3_ACCESS_KEY"),
                ["Storage:S3:SecretAccessKey"] = Environment.GetEnvironmentVariable("LMS_TEST_S3_SECRET_KEY"),
                ["Storage:S3:Bucket"] = "lms-e2e-" + Guid.NewGuid().ToString("N")[..12],
                ["Storage:S3:ForcePathStyle"] = "true",
                ["Storage:S3:CreateBucket"] = "true",
            }));
        }
        if (UseFakeObjectStorage || BrokenS3Configuration)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Provider"] = "S3",
                ["Storage:S3:Bucket"] = BrokenS3Configuration ? "" : "test-bucket",
                ["Storage:S3:CreateBucket"] = "true",
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IObjectStore>();
                services.AddSingleton<IObjectStore>(Store);
            });
        }
        if (HttpHandler is not null)
            builder.ConfigureTestServices(services => services.ConfigureHttpClientDefaults(client => client.ConfigurePrimaryHttpMessageHandler(() => HttpHandler)));
        if (Egress is not null)
            builder.ConfigureTestServices(services => { services.RemoveAll<Lms.Api.Infrastructure.LiveClasses.ILiveKitEgressClient>(); services.AddSingleton(Egress); });
        if (Rooms is not null)
            builder.ConfigureTestServices(services => { services.RemoveAll<Lms.Api.Infrastructure.LiveClasses.ILiveKitRoomClient>(); services.AddSingleton(Rooms); });
        if (Transcoder is not null)
            builder.ConfigureTestServices(services => { services.RemoveAll<Lms.Api.Infrastructure.Videos.IVideoTranscoder>(); services.AddSingleton(Transcoder); });
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Videos:Processing:WorkerEnabled"] = "false",   // tests run the processing service by hand
            ["LiveKit:Automation:WorkerEnabled"] = "false",  // and the class automation too
            ["Database:Provider"] = "InMemory",
            ["Database:ApplyMigrations"] = "false",
            // Each host gets its own in-memory database so test classes running in parallel cannot interfere with each other.
            ["Database:InMemoryName"] = "lms-test-" + Guid.NewGuid().ToString("N"),
            ["Retention:Enabled"] = "false",
            // Reminders are exercised directly through DeadlineReminderService so tests stay deterministic.
            ["Notifications:RemindersEnabled"] = "false",
            ["Integrations:AllowPrivateSmtpHosts"] = AllowPrivateSmtpHosts ? "true" : "false",
            // Tests call NotificationDispatcher directly so delivery is deterministic.
            ["Notifications:DispatcherEnabled"] = "false",
            ["Storage:LocalRoot"] = Path.Combine(Path.GetTempPath(), "lms-tests-assets"),
            // Each test signs in several times from one host; production defaults stay at 10 logins/minute.
            ["RateLimiting:LoginPermitsPerMinute"] = "100000",
            ["RateLimiting:ApiPermitsPerMinute"] = "100000",
            ["Platform:ProvisioningKey"] = PlatformKey,
            ["DataProtection:KeyStoragePath"] = Path.Combine(Path.GetTempPath(), "lms-tests-keys")
        }));
        if (ExtraSettings is not null) builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(ExtraSettings));
    }

    public HttpClient CreateTenantClient(string slug, string? bearer = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Slug", slug);
        if (bearer is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return client;
    }

    /// <summary>Provisions a uniquely named tenant with an admin and returns (slug, adminEmail, accessToken).</summary>
    public async Task<(string Slug, string Email, string Token)> ProvisionTenantWithAdminAsync()
    {
        var slug = "t" + Guid.NewGuid().ToString("N")[..10];
        var email = $"admin@{slug}.test";
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Platform-Key", PlatformKey);
        (await client.PostAsJsonAsync("/api/v1/platform/tenants", new { name = slug, slug })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/api/v1/platform/tenants/{slug}/bootstrap-admin",
            new { email, displayName = "Admin", password = AdminPassword })).EnsureSuccessStatusCode();
        return (slug, email, await LoginAsync(slug, email, AdminPassword));
    }

    public async Task<string> LoginAsync(string slug, string email, string password)
    {
        var response = await CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { tenantSlug = slug, email, password });
        response.EnsureSuccessStatusCode();
        var session = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return session.GetProperty("accessToken").GetString()!;
    }
}
