using System.Security.Claims;
using Lms.Api.Domain.Courses;
using Lms.Api.Domain.Identity;
using Lms.Api.Domain.Learning;
using Lms.Api.Infrastructure.CourseAccess;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Storage;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Content;

/// <summary>
/// Ordered content blocks inside a lesson. Staff edit them while the course is a draft; enrolled learners read them
/// once it is published. File-backed blocks upload in the same request that creates them.
/// </summary>
public static class ContentBlockEndpoints
{
    private const int MaxBlocksPerLesson = 100;
    private const long MaxFileBytes = 100 * 1024 * 1024;
    private const int MaxTextLength = 20000;
    private static readonly string[] DefaultEmbedHosts = ["www.youtube.com", "www.youtube-nocookie.com", "player.vimeo.com"];

    public static void MapContentBlockEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/tenant/courses/{courseId:guid}/lessons/{lessonId:guid}/blocks").RequireAuthorization("tenant.authenticated");
        group.AddEndpointFilter(async (context, next) =>
        {
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (!tenantContext.IsResolved)
                return Results.BadRequest(new { message = "A tenant is required. Use the X-Tenant-Slug header or a tenant subdomain." });
            return await next(context);
        });

        group.MapGet("", ListAsync).RequireAuthorization("tenant.course.read");
        group.MapPost("", CreateAsync).RequireAuthorization("tenant.course.manage");
        group.MapPut("/{blockId:guid}", UpdateAsync).RequireAuthorization("tenant.course.manage");
        group.MapDelete("/{blockId:guid}", DeleteAsync).RequireAuthorization("tenant.course.manage");
        group.MapPut("/order", ReorderAsync).RequireAuthorization("tenant.course.manage");
    }

    // ---------- read ----------
    private static async Task<IResult> ListAsync(Guid courseId, Guid lessonId, HttpContext httpContext, LmsDbContext db, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var (course, lesson) = await FindAsync(db, courseId, lessonId, HasPermission(httpContext, LmsPermissions.CourseManage), cancellationToken);
        if (course is null || lesson is null) return Results.NotFound();

        // Staff see drafts; everyone else needs a published course and an active or completed enrollment.
        if (!HasPermission(httpContext, LmsPermissions.CourseManage))
        {
            if (course.Status != CourseStatus.Published) return Results.NotFound();
            var enrollment = await db.Enrollments.AsNoTracking().SingleOrDefaultAsync(item => item.CourseId == courseId && item.LearnerUserId == userId
                && (item.Status == EnrollmentStatus.Active || item.Status == EnrollmentStatus.Completed), cancellationToken);
            if (enrollment is null || course.CurrentVersionId is not Guid versionId) return Results.NotFound();
            // Drip and "finish this first" rules: a closed module's content is not delivered, even to someone who knows the URL.
            if (await new ModuleAccessService().LockOfAsync(db, enrollment, versionId, lesson.CourseModuleId, cancellationToken) is { } closed)
                return Results.Conflict(new { message = closed.Reason, locked = true, unlocksAtUtc = closed.UnlocksAtUtc });
        }
        return Results.Ok(await BlocksAsync(db, courseId, lessonId, cancellationToken));
    }

    // ---------- write ----------
    private static async Task<IResult> CreateAsync(Guid courseId, Guid lessonId, HttpRequest request, ITenantContext tenantContext, LmsDbContext db, IContentAssetStorage storage, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var httpContext = request.HttpContext;
        if (tenantContext.TenantId is not Guid tenantId || GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var (course, lesson) = await FindAsync(db, courseId, lessonId, true, cancellationToken);
        if (course is null || lesson is null) return Results.NotFound();
        if (!await CanEditAsync(db, course, lesson, cancellationToken)) return Conflict("Content can only be edited while the course is a draft, or in a new version that has not been submitted for review.");
        if (await db.LessonBlocks.CountAsync(item => item.CourseLessonId == lessonId, cancellationToken) >= MaxBlocksPerLesson)
            return Problem($"A lesson can have at most {MaxBlocksPerLesson} blocks.");

        // Multipart for file blocks, JSON for everything else.
        BlockInput input;
        IFormFile? file = null;
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(cancellationToken);
            input = new BlockInput(form["type"], form["title"], form["text"], form["language"], form["url"], form["caption"]);
            file = form.Files.FirstOrDefault();
        }
        else
        {
            var body = await request.ReadFromJsonAsync<BlockInput>(cancellationToken);
            if (body is null) return Problem("The request body is empty.");
            input = body;
        }

        if (!Enum.TryParse<BlockType>(input.Type, true, out var type) || !Enum.IsDefined(type)) return Problem("Unknown block type.");
        var error = ValidateFields(type, input, configuration);
        if (error is not null) return Problem(error);

        ContentAsset? asset = null;
        if (BlockFileRules.UsesFile(type))
        {
            if (file is null || file.Length == 0) return Problem("Choose a file for this block.");
            if (file.Length > MaxFileBytes) return Problem("Files must be 100 MB or smaller.");
            var header = new byte[16];
            await using (var stream = file.OpenReadStream()) _ = await stream.ReadAsync(header, cancellationToken);
            var fileError = BlockFileRules.Validate(type, file.ContentType, header);
            if (fileError is not null) return Problem(fileError);

            var stored = await storage.SaveAsync(tenantId, courseId, file, cancellationToken);
            asset = new ContentAsset
            {
                Id = Guid.NewGuid(), TenantId = tenantId, CourseId = courseId, CourseVersionId = await CourseVersionRules.EditableVersionIdAsync(db, course, cancellationToken), OriginalFileName = stored.OriginalFileName,
                StorageKey = stored.StorageKey, ContentType = stored.ContentType, SizeBytes = stored.SizeBytes, Sha256 = stored.Sha256, CreatedByUserId = userId, CreatedAtUtc = DateTimeOffset.UtcNow
            };
            db.ContentAssets.Add(asset);
        }
        else if (file is not null) return Problem("This block type does not take a file.");

        var now = DateTimeOffset.UtcNow;
        var order = (await db.LessonBlocks.Where(item => item.CourseLessonId == lessonId).Select(item => (int?)item.DisplayOrder).MaxAsync(cancellationToken) ?? 0) + 1;
        var block = new LessonBlock
        {
            Id = Guid.NewGuid(), TenantId = tenantId, CourseLessonId = lessonId, Type = type, DisplayOrder = order,
            Title = Clean(input.Title), Text = Clean(input.Text), Language = Clean(input.Language), Url = Clean(input.Url), Caption = Clean(input.Caption),
            ContentAssetId = asset?.Id, CreatedAtUtc = now, UpdatedAtUtc = now
        };
        db.LessonBlocks.Add(block);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/courses/{courseId}/lessons/{lessonId}/blocks", (await BlocksAsync(db, courseId, lessonId, cancellationToken)).First(item => item.Id == block.Id));
    }

    private static async Task<IResult> UpdateAsync(Guid courseId, Guid lessonId, Guid blockId, LmsDbContext db, IConfiguration configuration, BlockInput input, CancellationToken cancellationToken)
    {
        var (course, lesson) = await FindAsync(db, courseId, lessonId, true, cancellationToken);
        if (course is null || lesson is null) return Results.NotFound();
        if (!await CanEditAsync(db, course, lesson, cancellationToken)) return Conflict("Content can only be edited while the course is a draft, or in a new version that has not been submitted for review.");
        var block = await db.LessonBlocks.SingleOrDefaultAsync(item => item.Id == blockId && item.CourseLessonId == lessonId, cancellationToken);
        if (block is null) return Results.NotFound();

        // The type and the uploaded file cannot change; replace the block instead.
        var error = ValidateFields(block.Type, input with { Type = block.Type.ToString() }, configuration);
        if (error is not null) return Problem(error);
        block.Title = Clean(input.Title);
        block.Text = Clean(input.Text);
        block.Language = Clean(input.Language);
        block.Url = Clean(input.Url);
        block.Caption = Clean(input.Caption);
        block.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok((await BlocksAsync(db, courseId, lessonId, cancellationToken)).First(item => item.Id == blockId));
    }

    private static async Task<IResult> DeleteAsync(Guid courseId, Guid lessonId, Guid blockId, LmsDbContext db, CancellationToken cancellationToken)
    {
        var (course, lesson) = await FindAsync(db, courseId, lessonId, true, cancellationToken);
        if (course is null || lesson is null) return Results.NotFound();
        if (!await CanEditAsync(db, course, lesson, cancellationToken)) return Conflict("Content can only be edited while the course is a draft, or in a new version that has not been submitted for review.");
        var block = await db.LessonBlocks.SingleOrDefaultAsync(item => item.Id == blockId && item.CourseLessonId == lessonId, cancellationToken);
        if (block is null) return Results.NotFound();
        db.LessonBlocks.Remove(block);
        // Close the gap so the order stays 1..n.
        var rest = await db.LessonBlocks.Where(item => item.CourseLessonId == lessonId && item.Id != blockId).OrderBy(item => item.DisplayOrder).ToListAsync(cancellationToken);
        for (var i = 0; i < rest.Count; i++) rest[i].DisplayOrder = i + 1;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ReorderAsync(Guid courseId, Guid lessonId, LmsDbContext db, ReorderRequest request, CancellationToken cancellationToken)
    {
        var (course, lesson) = await FindAsync(db, courseId, lessonId, true, cancellationToken);
        if (course is null || lesson is null) return Results.NotFound();
        if (!await CanEditAsync(db, course, lesson, cancellationToken)) return Conflict("Content can only be edited while the course is a draft, or in a new version that has not been submitted for review.");
        var blocks = await db.LessonBlocks.Where(item => item.CourseLessonId == lessonId).ToListAsync(cancellationToken);
        var ids = request.BlockIds ?? [];
        if (ids.Count != blocks.Count || ids.Distinct().Count() != ids.Count || !blocks.All(block => ids.Contains(block.Id)))
            return Problem("Send every block of the lesson exactly once.");
        for (var i = 0; i < ids.Count; i++) blocks.Single(block => block.Id == ids[i]).DisplayOrder = i + 1;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(await BlocksAsync(db, courseId, lessonId, cancellationToken));
    }

    // ---------- validation ----------
    private static string? ValidateFields(BlockType type, BlockInput input, IConfiguration configuration)
    {
        if (input.Title is { Length: > 200 }) return "The title must be 200 characters or fewer.";
        if (input.Caption is { Length: > 500 }) return "The caption must be 500 characters or fewer.";
        if (input.Language is { Length: > 30 }) return "The language name is too long.";
        if (input.Text is { Length: > MaxTextLength }) return $"Text must be {MaxTextLength} characters or fewer.";
        if (input.Url is { Length: > 2000 }) return "The link is too long.";

        switch (type)
        {
            case BlockType.Text:
                return string.IsNullOrWhiteSpace(input.Text) ? "Write some text for this block." : null;
            case BlockType.Code:
                return string.IsNullOrWhiteSpace(input.Text) ? "Paste the code for this block." : null;
            case BlockType.Link:
                return Uri.TryCreate(input.Url, UriKind.Absolute, out var link) && link.Scheme is "http" or "https" ? null : "Enter a valid http or https link.";
            case BlockType.Embed:
                var hosts = configuration.GetSection("Content:EmbedHosts").Get<string[]>() is { Length: > 0 } configured ? configured : DefaultEmbedHosts;
                return BlockFileRules.IsEmbedAllowed(input.Url, hosts) ? null : $"Embeds must be https links on: {string.Join(", ", hosts)}.";
            default:
                return null;
        }
    }

    // ---------- helpers ----------
    /// <summary>
    /// The lesson must belong to this course's live version; the id alone is not enough. Staff may also reach the lessons of a
    /// new version in progress, which learners never can.
    /// </summary>
    private static async Task<(Course? Course, CourseLesson? Lesson)> FindAsync(LmsDbContext db, Guid courseId, Guid lessonId, bool includeDraft, CancellationToken cancellationToken)
    {
        var course = await db.Courses.AsNoTracking().SingleOrDefaultAsync(item => item.Id == courseId, cancellationToken);
        if (course is null) return (null, null);
        var versionIds = new List<Guid>();
        if (course.CurrentVersionId is Guid current) versionIds.Add(current);
        if (includeDraft && course.DraftVersionId is Guid draft) versionIds.Add(draft);
        if (versionIds.Count == 0) return (null, null);
        var lesson = await (from l in db.CourseLessons.AsNoTracking()
                            join m in db.CourseModules.AsNoTracking() on l.CourseModuleId equals m.Id
                            where l.Id == lessonId && versionIds.Contains(m.CourseVersionId)
                            select l).SingleOrDefaultAsync(cancellationToken);
        return lesson is null ? (null, null) : (course, lesson);
    }

    private static async Task<bool> CanEditAsync(LmsDbContext db, Course course, CourseLesson lesson, CancellationToken cancellationToken)
    {
        var editable = await CourseVersionRules.EditableVersionIdAsync(db, course, cancellationToken);
        return editable is not null && await db.CourseModules.AnyAsync(item => item.Id == lesson.CourseModuleId && item.CourseVersionId == editable, cancellationToken);
    }

    private static async Task<List<BlockResponse>> BlocksAsync(LmsDbContext db, Guid courseId, Guid lessonId, CancellationToken cancellationToken)
    {
        var blocks = await db.LessonBlocks.AsNoTracking().Where(item => item.CourseLessonId == lessonId).OrderBy(item => item.DisplayOrder).ToListAsync(cancellationToken);
        var assetIds = blocks.Where(item => item.ContentAssetId != null).Select(item => item.ContentAssetId!.Value).ToList();
        var assets = await db.ContentAssets.AsNoTracking().Where(item => assetIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, cancellationToken);
        return blocks.Select(block =>
        {
            var asset = block.ContentAssetId is Guid id && assets.TryGetValue(id, out var found) ? found : null;
            return new BlockResponse(block.Id, block.Type.ToString(), block.DisplayOrder, block.Title, block.Text, block.Language, block.Url, block.Caption,
                asset is null ? null : new BlockFile(asset.OriginalFileName, asset.ContentType, asset.SizeBytes, $"/api/v1/tenant/courses/{courseId}/assets/{asset.Id}"));
        }).ToList();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static IResult Problem(string message) => Results.BadRequest(new { message });
    private static IResult Conflict(string message) => Results.Conflict(new { message });

    private static Guid? GetUserId(HttpContext context)
        => Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub"), out var id) ? id : null;

    private static bool HasPermission(HttpContext context, string permission)
        => context.User.Claims.Any(claim => claim.Type == "permission" && claim.Value.Equals(permission, StringComparison.OrdinalIgnoreCase));
}

public sealed record BlockInput(string? Type, string? Title, string? Text, string? Language, string? Url, string? Caption);
public sealed record ReorderRequest(List<Guid>? BlockIds);
public sealed record BlockFile(string FileName, string ContentType, long SizeBytes, string DownloadPath);
public sealed record BlockResponse(Guid Id, string Type, int DisplayOrder, string? Title, string? Text, string? Language, string? Url, string? Caption, BlockFile? File);
