using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Lms.Api.Infrastructure.Security;
using Lms.Api.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;

namespace Lms.Api.Tests;

/// <summary>
/// Skipped unless LMS_TEST_S3_URL is set, so the normal test run needs no server. To run against MinIO:
///   docker compose -f infra/s3/docker-compose.yml up -d
///   set LMS_TEST_S3_URL=http://localhost:9000  LMS_TEST_S3_ACCESS_KEY=lmsadmin  LMS_TEST_S3_SECRET_KEY=lmsadmin-secret
/// </summary>
public sealed class RequiresS3FactAttribute : FactAttribute
{
    public RequiresS3FactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LMS_TEST_S3_URL")))
            Skip = "Set LMS_TEST_S3_URL (and LMS_TEST_S3_ACCESS_KEY / LMS_TEST_S3_SECRET_KEY) to run against a real S3-compatible server.";
    }
}

public sealed class S3ObjectStoreIntegrationTests : IAsyncLifetime
{
    private S3ObjectStore? store;
    private readonly string bucket = "lms-test-" + Guid.NewGuid().ToString("N")[..12];

    public Task InitializeAsync()
    {
        var url = Environment.GetEnvironmentVariable("LMS_TEST_S3_URL");
        if (string.IsNullOrWhiteSpace(url)) return Task.CompletedTask;
        store = new S3ObjectStore(new S3StorageOptions
        {
            ServiceUrl = url, Bucket = bucket, ForcePathStyle = true, KeyPrefix = "lms/",
            AccessKeyId = Environment.GetEnvironmentVariable("LMS_TEST_S3_ACCESS_KEY"), SecretAccessKey = Environment.GetEnvironmentVariable("LMS_TEST_S3_SECRET_KEY"),
        }, new ConfigurationSecretStore(new ConfigurationBuilder().Build()));
        return Task.CompletedTask;
    }

    public Task DisposeAsync() { store?.Dispose(); return Task.CompletedTask; }

    private static byte[] Random(int size) { var bytes = new byte[size]; RandomNumberGenerator.Fill(bytes); return bytes; }

    [RequiresS3Fact]
    public async Task Objects_can_be_stored_checked_and_read_back_and_missing_ones_are_reported()
    {
        await store!.EnsureBucketAsync(default);
        await store.EnsureBucketAsync(default); // idempotent
        await store.PingAsync(default);

        var data = Random(2048);
        await store.PutAsync("tenant/course/a.bin", new MemoryStream(data), data.Length, "application/octet-stream", default);
        Assert.True(await store.ExistsAsync("tenant/course/a.bin", default));
        await using (var stream = (await store.GetAsync("tenant/course/a.bin", default))!)
        {
            using var copy = new MemoryStream(); await stream.CopyToAsync(copy);
            Assert.Equal(data, copy.ToArray());
        }
        Assert.False(await store.ExistsAsync("tenant/course/nope.bin", default));
        Assert.Null(await store.GetAsync("tenant/course/nope.bin", default));
    }

    [RequiresS3Fact]
    public async Task Large_files_survive_the_round_trip_intact()
    {
        await store!.EnsureBucketAsync(default);
        var data = Random(12 * 1024 * 1024);
        await store.PutAsync("tenant/course/large.bin", new MemoryStream(data), data.Length, "application/octet-stream", default);
        await using var stream = (await store.GetAsync("tenant/course/large.bin", default))!;
        using var copy = new MemoryStream(); await stream.CopyToAsync(copy);
        Assert.Equal(SHA256.HashData(data), SHA256.HashData(copy.ToArray()));
    }

    [RequiresS3Fact]
    public async Task Signed_links_serve_the_object_with_our_type_and_disposition_and_without_credentials()
    {
        await store!.EnsureBucketAsync(default);
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
        await store.PutAsync("tenant/course/pic.png", new MemoryStream(png), png.Length, "application/octet-stream", default);

        var url = await store.CreateTemporaryUrlAsync("tenant/course/pic.png", TimeSpan.FromMinutes(5), "image/png", "inline; filename=\"pic.png\"", default);
        using var http = new HttpClient(); // no credentials at all: the link is the credential
        var response = await http.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType); // overridden from what was stored
        Assert.StartsWith("inline", response.Content.Headers.ContentDisposition?.ToString());
        Assert.Equal(png, await response.Content.ReadAsByteArrayAsync());

        var attachment = await store.CreateTemporaryUrlAsync("tenant/course/pic.png", TimeSpan.FromMinutes(5), "application/zip", "attachment; filename=\"x.zip\"", default);
        var forced = await http.GetAsync(attachment);
        Assert.StartsWith("attachment", forced.Content.Headers.ContentDisposition?.ToString());
    }

    [RequiresS3Fact]
    public async Task A_tampered_link_is_refused()
    {
        await store!.EnsureBucketAsync(default);
        await store.PutAsync("tenant/course/secret.bin", new MemoryStream([1, 2, 3]), 3, "application/octet-stream", default);
        var url = await store.CreateTemporaryUrlAsync("tenant/course/secret.bin", TimeSpan.FromMinutes(5), "application/octet-stream", "attachment", default);
        using var http = new HttpClient();
        var other = new Uri(url!.ToString().Replace("secret.bin", "other.bin"));
        Assert.NotEqual(HttpStatusCode.OK, (await http.GetAsync(other)).StatusCode);
    }

    [RequiresS3Fact]
    public async Task Content_assets_round_trip_through_the_real_store()
    {
        await store!.EnsureBucketAsync(default);
        var assets = new ObjectStorageContentAssetStorage(store, new S3StorageOptions { PresignSeconds = 600 });
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 5, 5, 5 };
        var file = new Microsoft.AspNetCore.Http.FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "chart.png") { Headers = new Microsoft.AspNetCore.Http.HeaderDictionary(), ContentType = "image/png" };

        var saved = await assets.SaveAsync(Guid.NewGuid(), Guid.NewGuid(), file, default);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), saved.Sha256);
        await using var read = (await assets.OpenReadAsync(saved.StorageKey, default))!;
        using var copy = new MemoryStream(); await read.CopyToAsync(copy);
        Assert.Equal(bytes, copy.ToArray());

        var link = await assets.CreateTemporaryUrlAsync(saved.StorageKey, "image/png", "chart.png", inline: true, default);
        using var http = new HttpClient();
        Assert.Equal(bytes, await http.GetByteArrayAsync(link));
    }
}

/// <summary>The whole path through the API against a real S3-compatible server: upload, access rules, signed link, readiness.</summary>
public sealed class S3EndToEndTests : IAsyncLifetime
{
    private LmsApiFactory? factory;

    public Task InitializeAsync()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LMS_TEST_S3_URL"))) factory = new LmsApiFactory { UseRealObjectStorage = true };
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() { if (factory is not null) await factory.DisposeAsync(); }

    [RequiresS3Fact]
    public async Task Course_media_uploads_to_the_real_store_and_streams_through_a_signed_link_to_enrolled_learners_only()
    {
        var (slug, _, adminToken) = await factory!.ProvisionTenantWithAdminAsync();
        var admin = factory.CreateTenantClient(slug, adminToken);
        var json = async (HttpResponseMessage r) => System.Text.Json.JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
        var course = await json(await admin.PostAsJsonAsync("/api/v1/tenant/courses", new { code = "E2E-1", title = "End to end" }));
        var courseId = course.GetProperty("course").GetProperty("id").GetGuid();
        var module = await json(await admin.PostAsJsonAsync($"/api/v1/tenant/courses/{courseId}/modules", new { title = "M" }));
        var lesson = await json(await admin.PostAsJsonAsync($"/api/v1/tenant/courses/{courseId}/modules/{module.GetProperty("id").GetGuid()}/lessons", new { title = "L" }));

        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 4, 3, 2, 1 };
        var form = new MultipartFormDataContent { { new StringContent("Image"), "type" } };
        var part = new ByteArrayContent(png);
        part.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("image/png");
        form.Add(part, "file", "e2e.png");
        var block = await json(await admin.PostAsync($"/api/v1/tenant/courses/{courseId}/lessons/{lesson.GetProperty("id").GetGuid()}/blocks", form));
        var path = block.GetProperty("file").GetProperty("downloadPath").GetString()!;

        (await admin.PostAsync($"/api/v1/tenant/courses/{courseId}/submit-review", null)).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/api/v1/tenant/courses/{courseId}/publish", null)).EnsureSuccessStatusCode();
        var email = $"lena@{slug}.test";
        (await admin.PostAsJsonAsync("/api/v1/tenant/users", new { email, displayName = "Lena", password = LmsApiFactory.AdminPassword, roleCode = "LEARNER" })).EnsureSuccessStatusCode();
        var learner = factory.CreateTenantClient(slug, await factory.LoginAsync(slug, email, LmsApiFactory.AdminPassword));

        Assert.Equal(HttpStatusCode.NotFound, (await learner.GetAsync(path + "/link")).StatusCode); // not enrolled
        (await learner.PostAsync($"/api/v1/tenant/courses/{courseId}/enroll", null)).EnsureSuccessStatusCode();

        // Through the API (works for every provider) ...
        Assert.Equal(png, await learner.GetByteArrayAsync(path));
        // ... and straight from storage with the signed link, which carries no credentials of its own.
        var link = await json(await learner.GetAsync(path + "/link"));
        using var plain = new HttpClient();
        var direct = await plain.GetAsync(link.GetProperty("url").GetString());
        Assert.Equal(HttpStatusCode.OK, direct.StatusCode);
        Assert.Equal("image/png", direct.Content.Headers.ContentType?.MediaType);
        Assert.Equal(png, await direct.Content.ReadAsByteArrayAsync());

        var ready = await json(await factory.CreateClient().GetAsync("/health/ready"));
        Assert.Equal("connected", ready.GetProperty("storage").GetString());
    }
}
