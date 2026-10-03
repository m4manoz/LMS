using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lms.Api.Infrastructure.Storage;
using Microsoft.AspNetCore.Http;

namespace Lms.Api.Tests;

/// <summary>The storage layer and the endpoints that use it, with an in-memory object store standing in for S3.</summary>
public sealed class ObjectStorageTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9, 8, 7, 6];

    private static FormFile File(byte[] bytes, string name = "diagram.png", string contentType = "image/png")
        => new(new MemoryStream(bytes), 0, bytes.Length, "file", name) { Headers = new HeaderDictionary(), ContentType = contentType };

    private static (ObjectStorageContentAssetStorage Storage, InMemoryObjectStore Store) NewStorage(int presignSeconds = 3600)
    {
        var store = new InMemoryObjectStore();
        return (new ObjectStorageContentAssetStorage(store, new S3StorageOptions { PresignSeconds = presignSeconds }), store);
    }

    // ---------- the storage layer ----------
    [Fact]
    public async Task Saved_files_get_a_tenant_scoped_key_and_a_correct_checksum()
    {
        var (storage, store) = NewStorage();
        var tenant = Guid.NewGuid(); var course = Guid.NewGuid();
        var saved = await storage.SaveAsync(tenant, course, File(Png), CancellationToken.None);

        Assert.StartsWith($"{tenant:D}/{course:D}/", saved.StorageKey);
        Assert.EndsWith(".png", saved.StorageKey);
        Assert.Equal(Png.Length, saved.SizeBytes);
        Assert.Equal("image/png", saved.ContentType);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Png)).ToLowerInvariant(), saved.Sha256);
        Assert.Equal(Png, store.Bytes(saved.StorageKey));
        Assert.Equal("image/png", store.ContentTypeOf(saved.StorageKey));
    }

    [Fact]
    public async Task Files_can_be_read_back_and_missing_ones_return_null()
    {
        var (storage, _) = NewStorage();
        var saved = await storage.SaveAsync(Guid.NewGuid(), Guid.NewGuid(), File(Png), CancellationToken.None);
        await using (var stream = (await storage.OpenReadAsync(saved.StorageKey, CancellationToken.None))!)
        {
            using var copy = new MemoryStream(); await stream.CopyToAsync(copy);
            Assert.Equal(Png, copy.ToArray());
        }
        Assert.True(await storage.ExistsAsync(saved.StorageKey, CancellationToken.None));
        Assert.Null(await storage.OpenReadAsync("tenant/course/missing.png", CancellationToken.None));
        Assert.False(await storage.ExistsAsync("tenant/course/missing.png", CancellationToken.None));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("tenant/../other/file.png")]
    [InlineData("/absolute/path.png")]
    [InlineData("back\\slash.png")]
    [InlineData("")]
    [InlineData("new\nline.png")]
    public async Task Unsafe_keys_never_reach_the_store(string key)
    {
        var (storage, store) = NewStorage();
        await store.PutAsync("tenant/course/real.png", new MemoryStream(Png), Png.Length, "image/png", CancellationToken.None);
        Assert.Null(await storage.OpenReadAsync(key, CancellationToken.None));
        Assert.False(await storage.ExistsAsync(key, CancellationToken.None));
        Assert.Null(await storage.CreateTemporaryUrlAsync(key, "image/png", "x.png", true, CancellationToken.None));
    }

    [Fact]
    public async Task Odd_file_extensions_are_dropped_from_the_key()
    {
        var (storage, _) = NewStorage();
        var saved = await storage.SaveAsync(Guid.NewGuid(), Guid.NewGuid(), File(Png, "evil.p/../ng"), CancellationToken.None);
        Assert.DoesNotContain("..", saved.StorageKey);
        var longExtension = await storage.SaveAsync(Guid.NewGuid(), Guid.NewGuid(), File(Png, "name." + new string('x', 40)), CancellationToken.None);
        Assert.DoesNotContain(".xxxx", longExtension.StorageKey);
    }

    [Fact]
    public async Task Temporary_links_use_the_configured_lifetime_and_validated_type()
    {
        var (storage, store) = NewStorage(presignSeconds: 900);
        var url = await storage.CreateTemporaryUrlAsync("t/c/a.png", "image/png", "diagram.png", inline: true, CancellationToken.None);
        Assert.NotNull(url);
        Assert.Equal(TimeSpan.FromSeconds(900), store.LastLifetime);
        Assert.Equal("image/png", store.LastContentType);
        Assert.StartsWith("inline;", store.LastDisposition);

        await storage.CreateTemporaryUrlAsync("t/c/b.zip", "application/zip", "bundle.zip", inline: false, CancellationToken.None);
        Assert.StartsWith("attachment;", store.LastDisposition);
    }

    [Fact]
    public async Task File_names_cannot_break_out_of_the_content_disposition_header()
    {
        var (storage, store) = NewStorage();
        await storage.CreateTemporaryUrlAsync("t/c/a.png", "image/png", "evil\"; filename=\"x\r\nSet-Cookie: a=b.png", true, CancellationToken.None);
        Assert.DoesNotContain('\r', store.LastDisposition!);
        Assert.DoesNotContain('\n', store.LastDisposition!);
        Assert.DoesNotContain("Set-Cookie: a=b.png\"", store.LastDisposition!.Replace("%0D%0A", ""));
    }

    // ---------- fallback to local files ----------
    [Fact]
    public async Task Fallback_reads_legacy_files_but_writes_only_to_the_primary()
    {
        var (primary, store) = NewStorage();
        var legacyStore = new InMemoryObjectStore();
        var legacy = new ObjectStorageContentAssetStorage(legacyStore, new S3StorageOptions());
        var combined = new FallbackContentAssetStorage(primary, legacy);

        var old = await legacy.SaveAsync(Guid.NewGuid(), Guid.NewGuid(), File(Png), CancellationToken.None);
        Assert.NotNull(await combined.OpenReadAsync(old.StorageKey, CancellationToken.None));
        Assert.True(await combined.ExistsAsync(old.StorageKey, CancellationToken.None));
        // Legacy files stream through the API; they have no direct link.
        Assert.Null(await combined.CreateTemporaryUrlAsync(old.StorageKey, "image/png", "d.png", true, CancellationToken.None));

        var fresh = await combined.SaveAsync(Guid.NewGuid(), Guid.NewGuid(), File(Png), CancellationToken.None);
        Assert.Contains(fresh.StorageKey, store.Keys);
        Assert.DoesNotContain(fresh.StorageKey, legacyStore.Keys);
        Assert.NotNull(await combined.CreateTemporaryUrlAsync(fresh.StorageKey, "image/png", "d.png", true, CancellationToken.None));
        Assert.Null(await combined.OpenReadAsync("t/c/nowhere.png", CancellationToken.None));
    }

    // ---------- configuration ----------
    [Fact]
    public void Options_are_validated_and_normalised()
    {
        Assert.Contains(new S3StorageOptions().Validate(), problem => problem.Contains("Bucket"));
        Assert.Contains(new S3StorageOptions { Bucket = "b", ServiceUrl = "not a url" }.Validate(), problem => problem.Contains("ServiceUrl"));
        Assert.Contains(new S3StorageOptions { Bucket = "b", AccessKeyId = "key" }.Validate(), problem => problem.Contains("both"));
        Assert.Empty(new S3StorageOptions { Bucket = "b" }.Validate()); // default AWS credential chain
        Assert.Empty(new S3StorageOptions { Bucket = "b", AccessKeyIdReference = "S3_KEY", SecretAccessKeyReference = "S3_SECRET", ServiceUrl = "http://localhost:9000" }.Validate());

        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:S3:Bucket"] = "b", ["Storage:S3:KeyPrefix"] = "lms", ["Storage:S3:PresignSeconds"] = "5",
        }).Build();
        var options = S3StorageOptions.From(config);
        Assert.Equal("lms/", options.KeyPrefix);
        Assert.Equal(60, options.PresignSeconds); // clamped to a sensible minimum
    }

    [Fact]
    public void Inline_types_are_the_validated_media_types_only()
    {
        Assert.True(BlockFileRules.IsInlineType("image/png"));
        Assert.True(BlockFileRules.IsInlineType("application/pdf; charset=binary"));
        Assert.True(BlockFileRules.IsInlineType("VIDEO/MP4"));
        Assert.False(BlockFileRules.IsInlineType("text/html"));
        Assert.False(BlockFileRules.IsInlineType("image/svg+xml"));
        Assert.False(BlockFileRules.IsInlineType(null));
    }

    [Fact]
    public async Task The_host_refuses_to_start_when_s3_is_selected_without_a_bucket()
    {
        await using var factory = new LmsApiFactory { BrokenS3Configuration = true };
        var exception = Record.Exception(() => factory.CreateClient());
        Assert.NotNull(exception);
        var text = exception!.ToString();
        Assert.Contains("Storage:S3:Bucket", text);
    }
}

/// <summary>A host that uses the S3 provider backed by an in-memory store; one per test class.</summary>
public sealed class S3Fixture : IAsyncDisposable
{
    public LmsApiFactory Factory { get; } = new() { UseFakeObjectStorage = true };
    public ValueTask DisposeAsync() => Factory.DisposeAsync();
}

/// <summary>The same behaviour through the real endpoints, with the S3 provider selected.</summary>
public sealed class ObjectStorageEndpointTests : IClassFixture<S3Fixture>
{
    private readonly LmsApiFactory _factory;
    public ObjectStorageEndpointTests(S3Fixture fixture) => _factory = fixture.Factory;

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private sealed record Setup(string Slug, HttpClient Admin, HttpClient Learner, Guid CourseId, Guid LessonId);

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<Setup> CreateAsync()
    {
        var (slug, _, adminToken) = await _factory.ProvisionTenantWithAdminAsync();
        var admin = _factory.CreateTenantClient(slug, adminToken);
        var course = await ReadAsync(await admin.PostAsJsonAsync("/api/v1/tenant/courses", new { code = "ART-1", title = "Art" }));
        var courseId = course.GetProperty("course").GetProperty("id").GetGuid();
        var module = await ReadAsync(await admin.PostAsJsonAsync($"/api/v1/tenant/courses/{courseId}/modules", new { title = "M1" }));
        var lesson = await ReadAsync(await admin.PostAsJsonAsync($"/api/v1/tenant/courses/{courseId}/modules/{module.GetProperty("id").GetGuid()}/lessons", new { title = "L1" }));
        var email = $"lena@{slug}.test";
        (await admin.PostAsJsonAsync("/api/v1/tenant/users", new { email, displayName = "Lena", password = LmsApiFactory.AdminPassword, roleCode = "LEARNER" })).EnsureSuccessStatusCode();
        var learner = _factory.CreateTenantClient(slug, await _factory.LoginAsync(slug, email, LmsApiFactory.AdminPassword));
        return new Setup(slug, admin, learner, courseId, lesson.GetProperty("id").GetGuid());
    }

    private static async Task PublishAndEnrollAsync(Setup s, bool enroll = true)
    {
        (await s.Admin.PostAsync($"/api/v1/tenant/courses/{s.CourseId}/submit-review", null)).EnsureSuccessStatusCode();
        (await s.Admin.PostAsync($"/api/v1/tenant/courses/{s.CourseId}/publish", null)).EnsureSuccessStatusCode();
        if (enroll) (await s.Learner.PostAsync($"/api/v1/tenant/courses/{s.CourseId}/enroll", null)).EnsureSuccessStatusCode();
    }

    private static async Task<JsonElement> UploadAsync(Setup s, string type, string fileName, string contentType, byte[] bytes)
    {
        var form = new MultipartFormDataContent { { new StringContent(type), "type" } };
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        form.Add(file, "file", fileName);
        var response = await s.Admin.PostAsync($"/api/v1/tenant/courses/{s.CourseId}/lessons/{s.LessonId}/blocks", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("file");
    }

    [Fact]
    public async Task Uploads_go_to_object_storage_under_the_tenant_and_course_prefix_and_download_back()
    {
        var s = await CreateAsync();
        var file = await UploadAsync(s, "Image", "diagram.png", "image/png", Png);
        await PublishAndEnrollAsync(s);

        var key = _factory.Store.Keys.Single(k => k.Contains(s.CourseId.ToString("D")));
        Assert.Equal(Png, _factory.Store.Bytes(key));

        var download = await s.Learner.GetAsync(file.GetProperty("downloadPath").GetString());
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(Png, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("nosniff", download.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Enrolled_learners_get_a_short_lived_inline_link_for_media()
    {
        var s = await CreateAsync();
        var file = await UploadAsync(s, "Image", "diagram.png", "image/png", Png);
        await PublishAndEnrollAsync(s);

        var response = await s.Learner.GetAsync(file.GetProperty("downloadPath").GetString() + "/link");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        var link = await ReadAsync(response);
        Assert.StartsWith("https://objects.test/", link.GetProperty("url").GetString());
        Assert.True(link.GetProperty("expiresAtUtc").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(30));
        Assert.StartsWith("inline;", _factory.Store.LastDisposition);
        Assert.Equal("image/png", _factory.Store.LastContentType);
    }

    [Fact]
    public async Task Downloadable_files_get_an_attachment_link_never_inline()
    {
        var s = await CreateAsync();
        var file = await UploadAsync(s, "Download", "page.html", "text/html", Encoding.ASCII.GetBytes("<script>alert(1)</script>"));
        await PublishAndEnrollAsync(s);

        (await s.Learner.GetAsync(file.GetProperty("downloadPath").GetString() + "/link")).EnsureSuccessStatusCode();
        Assert.StartsWith("attachment;", _factory.Store.LastDisposition);
    }

    [Fact]
    public async Task Links_follow_the_same_access_rules_as_downloads()
    {
        var s = await CreateAsync();
        var file = await UploadAsync(s, "Image", "diagram.png", "image/png", Png);
        var link = file.GetProperty("downloadPath").GetString() + "/link";
        await PublishAndEnrollAsync(s, enroll: false);

        Assert.Equal(HttpStatusCode.NotFound, (await s.Learner.GetAsync(link)).StatusCode); // in the tenant, not enrolled
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.GetAsync(link)).StatusCode);
        (await s.Learner.PostAsync($"/api/v1/tenant/courses/{s.CourseId}/enroll", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await s.Learner.GetAsync(link)).StatusCode);
    }

    [Fact]
    public async Task Draft_course_files_have_no_link_for_learners()
    {
        var s = await CreateAsync();
        var file = await UploadAsync(s, "Image", "diagram.png", "image/png", Png);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Learner.GetAsync(file.GetProperty("downloadPath").GetString() + "/link")).StatusCode);
    }

    [Fact]
    public async Task Another_tenant_cannot_get_a_link()
    {
        var a = await CreateAsync();
        var b = await CreateAsync();
        var file = await UploadAsync(a, "Image", "diagram.png", "image/png", Png);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.GetAsync(file.GetProperty("downloadPath").GetString() + "/link")).StatusCode);
    }

    [Fact]
    public async Task The_bucket_is_created_at_startup_when_asked_to_and_readiness_reports_storage()
    {
        _factory.CreateClient(); // start the host
        Assert.True(_factory.Store.BucketEnsured);

        var ready = await _factory.CreateClient().GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("connected", (await ReadAsync(ready)).GetProperty("storage").GetString());
    }
}

/// <summary>Readiness depends on the object store being reachable. Uses its own host so it can break the store.</summary>
public sealed class ObjectStorageReadinessTests
{
    [Fact]
    public async Task Readiness_fails_while_the_object_store_is_unreachable_and_recovers()
    {
        await using var factory = new LmsApiFactory { UseFakeObjectStorage = true };
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);

        factory.Store.Failing = true;
        var down = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
        Assert.Equal("unavailable", JsonDocument.Parse(await down.Content.ReadAsStringAsync()).RootElement.GetProperty("storage").GetString());

        factory.Store.Failing = false;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }
}

/// <summary>With local storage there are no direct links, so media streams through the API.</summary>
public sealed class LocalStorageLinkTests : IClassFixture<LmsApiFactory>
{
    private readonly LmsApiFactory _factory;
    public LocalStorageLinkTests(LmsApiFactory factory) => _factory = factory;

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    [Fact]
    public async Task Local_storage_returns_no_direct_link_and_readiness_reports_local()
    {
        var (slug, _, token) = await _factory.ProvisionTenantWithAdminAsync();
        var admin = _factory.CreateTenantClient(slug, token);
        var course = await ReadAsync(await admin.PostAsJsonAsync("/api/v1/tenant/courses", new { code = "L-1", title = "Local" }));
        var courseId = course.GetProperty("course").GetProperty("id").GetGuid();
        var module = await ReadAsync(await admin.PostAsJsonAsync($"/api/v1/tenant/courses/{courseId}/modules", new { title = "M" }));
        var lesson = await ReadAsync(await admin.PostAsJsonAsync($"/api/v1/tenant/courses/{courseId}/modules/{module.GetProperty("id").GetGuid()}/lessons", new { title = "L" }));

        var form = new MultipartFormDataContent { { new StringContent("Image"), "type" } };
        var bytes = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1]);
        bytes.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
        form.Add(bytes, "file", "a.png");
        var created = await admin.PostAsync($"/api/v1/tenant/courses/{courseId}/lessons/{lesson.GetProperty("id").GetGuid()}/blocks", form);
        var path = (await ReadAsync(created)).GetProperty("file").GetProperty("downloadPath").GetString()!;

        var link = await ReadAsync(await admin.GetAsync(path + "/link"));
        Assert.Equal(JsonValueKind.Null, link.GetProperty("url").ValueKind);

        var ready = await ReadAsync(await _factory.CreateClient().GetAsync("/health/ready"));
        Assert.Equal("local", ready.GetProperty("storage").GetString());
    }
}
