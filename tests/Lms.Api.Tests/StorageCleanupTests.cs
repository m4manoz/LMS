using System.Net;
using System.Net.Http.Json;
using System.Text;
using Lms.Api.Domain.Courses;
using Lms.Api.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

/// <summary>Files are taken out of storage when nothing uses them any more, and kept while something still does.</summary>
public sealed class StorageCleanupTests : IClassFixture<LmsApiFactory>
{
    private readonly LmsApiFactory _factory;
    private readonly TestWorld _world;
    public StorageCleanupTests(LmsApiFactory factory) { _factory = factory; _world = new TestWorld(factory); }

    private async Task<bool> ExistsAsync(string key)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IContentAssetStorage>().ExistsAsync(key, CancellationToken.None);
    }

    private sealed record Block(Guid LessonUrlCourse, Guid LessonId, Guid BlockId, Guid AssetId, string Key);

    private async Task<(Tenant Tenant, CourseInfo Course, Block Block)> NewBlockAsync()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "CLEAN", publish: false);
        var lessonId = course.LessonIds[0][0];
        var form = new MultipartFormDataContent { { new StringContent("Download"), "type" }, { new StringContent("Template"), "title" } };
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("a downloadable file"));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "template.docx");
        var response = await t.Admin.PostAsync($"/api/v1/tenant/courses/{course.Id}/lessons/{lessonId}/blocks", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var blockId = (await ReadAsync(response)).GetProperty("id").GetGuid();
        Block? found = null;
        await _world.WithDbAsync(t.Slug, async db =>
        {
            var block = await db.LessonBlocks.SingleAsync(item => item.Id == blockId);
            var asset = await db.ContentAssets.SingleAsync(item => item.Id == block.ContentAssetId);
            found = new Block(course.Id, lessonId, blockId, asset.Id, asset.StorageKey);
        });
        return (t, course, found!);
    }

    [Fact]
    public async Task Deleting_a_block_removes_its_file_and_record()
    {
        var (t, course, block) = await NewBlockAsync();
        Assert.True(await ExistsAsync(block.Key));
        Assert.Equal(HttpStatusCode.NoContent, (await t.Admin.DeleteAsync($"/api/v1/tenant/courses/{course.Id}/lessons/{block.LessonId}/blocks/{block.BlockId}")).StatusCode);
        Assert.False(await ExistsAsync(block.Key));
        await _world.WithDbAsync(t.Slug, async db => Assert.False(await db.ContentAssets.AnyAsync(item => item.Id == block.AssetId)));
    }

    [Fact]
    public async Task A_file_another_block_still_shows_stays_until_the_last_one_goes()
    {
        var (t, course, block) = await NewBlockAsync();
        var secondId = Guid.NewGuid();
        await _world.WithDbAsync(t.Slug, async db =>
        {
            // The same file in a second place, as when a draft copy of a published course reuses its files.
            var first = await db.LessonBlocks.SingleAsync(item => item.Id == block.BlockId);
            db.LessonBlocks.Add(new LessonBlock { Id = secondId, TenantId = first.TenantId, CourseLessonId = first.CourseLessonId, Type = first.Type, DisplayOrder = 2, Title = "Copy", ContentAssetId = first.ContentAssetId, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        });
        var url = $"/api/v1/tenant/courses/{course.Id}/lessons/{block.LessonId}/blocks";

        Assert.Equal(HttpStatusCode.NoContent, (await t.Admin.DeleteAsync($"{url}/{block.BlockId}")).StatusCode);
        Assert.True(await ExistsAsync(block.Key));
        await _world.WithDbAsync(t.Slug, async db => Assert.True(await db.ContentAssets.AnyAsync(item => item.Id == block.AssetId)));

        Assert.Equal(HttpStatusCode.NoContent, (await t.Admin.DeleteAsync($"{url}/{secondId}")).StatusCode);
        Assert.False(await ExistsAsync(block.Key));
    }

    [Fact]
    public async Task Deleting_a_text_block_touches_no_files()
    {
        var (t, course, block) = await NewBlockAsync();
        var created = await t.Admin.PostAsJsonAsync($"/api/v1/tenant/courses/{course.Id}/lessons/{block.LessonId}/blocks", new { type = "Text", text = "Words" });
        var textId = (await ReadAsync(created)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await t.Admin.DeleteAsync($"/api/v1/tenant/courses/{course.Id}/lessons/{block.LessonId}/blocks/{textId}")).StatusCode);
        Assert.True(await ExistsAsync(block.Key));
    }

    private static MultipartFormDataContent Form(string text, string? fileName = null, string fileText = "")
    {
        var content = new MultipartFormDataContent { { new StringContent(text), "text" } };
        if (fileName is not null) { var file = new ByteArrayContent(Encoding.UTF8.GetBytes(fileText)); file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain"); content.Add(file, "file", fileName); }
        return content;
    }

    private async Task<string> KeyOfAsync(string slug, Guid learnerId, Guid assignmentId)
    {
        var key = string.Empty;
        await _world.WithDbAsync(slug, async db => key = (await db.AssignmentSubmissions.SingleAsync(item => item.AssignmentId == assignmentId && item.LearnerUserId == learnerId)).FileStorageKey!);
        return key;
    }

    [Fact]
    public async Task Replacing_an_assignment_file_removes_the_old_one()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "REPL");
        var lena = await _world.AddLearnerAsync(t, "Lena");
        Assert.True((await EnrollAsync(lena, course)).IsSuccessStatusCode);
        var assignment = (await ReadAsync(await t.Admin.PostAsJsonAsync("/api/v1/tenant/assignments", new { courseId = course.Id, title = "Files", maxPoints = 10 }))).GetProperty("id").GetGuid();
        await t.Admin.PostAsync($"/api/v1/tenant/assignments/{assignment}/publish", null);

        await lena.Client.PostAsync($"/api/v1/tenant/assignments/{assignment}/submission", Form("v1", "first.txt", "one"));
        var first = await KeyOfAsync(t.Slug, lena.Id, assignment);
        Assert.True(await ExistsAsync(first));

        await lena.Client.PostAsync($"/api/v1/tenant/assignments/{assignment}/submission", Form("v2")); // no new file: the old one stays
        Assert.Equal(first, await KeyOfAsync(t.Slug, lena.Id, assignment));
        Assert.True(await ExistsAsync(first));

        await lena.Client.PostAsync($"/api/v1/tenant/assignments/{assignment}/submission", Form("v3", "second.txt", "two"));
        var second = await KeyOfAsync(t.Slug, lena.Id, assignment);
        Assert.NotEqual(first, second);
        Assert.False(await ExistsAsync(first));
        Assert.True(await ExistsAsync(second));
    }

    [Fact]
    public async Task A_groups_shared_file_survives_until_it_is_replaced_for_everyone()
    {
        var t = await _world.NewTenantAsync();
        var course = await _world.NewCourseAsync(t, "GRPF");
        var lena = await _world.AddLearnerAsync(t, "Lena");
        var otto = await _world.AddLearnerAsync(t, "Otto");
        foreach (var learner in new[] { lena, otto }) Assert.True((await EnrollAsync(learner, course)).IsSuccessStatusCode);
        var assignment = (await ReadAsync(await t.Admin.PostAsJsonAsync("/api/v1/tenant/assignments", new { courseId = course.Id, title = "Team files", maxPoints = 10, isGroup = true }))).GetProperty("id").GetGuid();
        await t.Admin.PostAsync($"/api/v1/tenant/assignments/{assignment}/publish", null);
        (await t.Admin.PostAsJsonAsync($"/api/v1/tenant/assignments/{assignment}/groups", new { name = "Team", memberUserIds = new[] { lena.Id, otto.Id } })).EnsureSuccessStatusCode();

        await lena.Client.PostAsync($"/api/v1/tenant/assignments/{assignment}/submission", Form("v1", "shared.txt", "shared"));
        var shared = await KeyOfAsync(t.Slug, lena.Id, assignment);
        Assert.Equal(shared, await KeyOfAsync(t.Slug, otto.Id, assignment));   // both rows point at the one file
        await otto.Client.PostAsync($"/api/v1/tenant/assignments/{assignment}/submission", Form("v2"));
        Assert.True(await ExistsAsync(shared));

        await otto.Client.PostAsync($"/api/v1/tenant/assignments/{assignment}/submission", Form("v3", "new.txt", "new"));
        Assert.False(await ExistsAsync(shared));
        Assert.True(await ExistsAsync(await KeyOfAsync(t.Slug, lena.Id, assignment)));   // everyone moved on to the new file
    }
}
