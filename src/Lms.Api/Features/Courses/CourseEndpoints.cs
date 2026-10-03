using System.Security.Claims;
using System.Text.RegularExpressions;
using Lms.Api.Domain.Courses;
using Lms.Api.Domain.Identity;
using Lms.Api.Domain.Learning;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.CourseAccess;
using Lms.Api.Infrastructure.Storage;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Courses;

public static class CourseEndpoints
{
    private const long MaximumAssetBytes = 100 * 1024 * 1024;

    public static void MapCourseEndpoints(this WebApplication app)
    {
        var tenant = app.MapGroup("/api/v1/tenant/courses").RequireAuthorization("tenant.authenticated");
        tenant.AddEndpointFilter(async (context, next) =>
        {
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (!tenantContext.IsResolved)
                return Results.BadRequest(new { message = "A tenant is required. Use the X-Tenant-Slug header or a tenant subdomain." });
            return await next(context);
        });

        tenant.MapGet("", ListCoursesAsync).RequireAuthorization("tenant.course.read");
        tenant.MapGet("/{courseId:guid}", GetCourseAsync).RequireAuthorization("tenant.course.read");
        tenant.MapGet("/{courseId:guid}/offline-manifest", GetOfflineManifestAsync).RequireAuthorization("tenant.enrollment.read");
        tenant.MapPost("", CreateCourseAsync).RequireAuthorization("tenant.course.manage");
        tenant.MapPut("/{courseId:guid}", UpdateCourseAsync).RequireAuthorization("tenant.course.manage");
        tenant.MapPost("/{courseId:guid}/submit-review", SubmitReviewAsync).RequireAuthorization("tenant.course.review");
        tenant.MapPost("/{courseId:guid}/publish", PublishAsync).RequireAuthorization("tenant.course.publish");
        tenant.MapPost("/{courseId:guid}/versions", StartVersionAsync).RequireAuthorization("tenant.course.manage");
        tenant.MapDelete("/{courseId:guid}/versions/draft", DiscardVersionAsync).RequireAuthorization("tenant.course.manage");
        tenant.MapPost("/{courseId:guid}/archive", ArchiveAsync).RequireAuthorization("tenant.course.manage");
        tenant.MapPut("/{courseId:guid}/modules/order", ReorderModulesAsync).RequireAuthorization("tenant.course.manage");
        tenant.MapDelete("/{courseId:guid}/modules/{moduleId:guid}", DeleteModuleAsync).RequireAuthorization("tenant.course.manage");
        tenant.MapPut("/{courseId:guid}/modules/{moduleId:guid}/lessons/order", ReorderLessonsAsync).RequireAuthorization("tenant.course.manage");
        tenant.MapDelete("/{courseId:guid}/lessons/{lessonId:guid}", DeleteLessonAsync).RequireAuthorization("tenant.course.manage");
        tenant.MapPost("/{courseId:guid}/modules", CreateModuleAsync).RequireAuthorization("tenant.course.manage");
        tenant.MapPut("/{courseId:guid}/modules/{moduleId:guid}", UpdateModuleAsync).RequireAuthorization("tenant.course.manage");
        tenant.MapPost("/{courseId:guid}/modules/{moduleId:guid}/lessons", CreateLessonAsync).RequireAuthorization("tenant.course.manage");
        tenant.MapPut("/{courseId:guid}/lessons/{lessonId:guid}", UpdateLessonAsync).RequireAuthorization("tenant.course.manage");
        tenant.MapPost("/{courseId:guid}/assets", UploadAssetAsync).RequireAuthorization("tenant.course.manage");
        tenant.MapGet("/{courseId:guid}/assets/{assetId:guid}", DownloadAssetAsync).RequireAuthorization("tenant.course.read");
        tenant.MapGet("/{courseId:guid}/assets/{assetId:guid}/link", AssetLinkAsync).RequireAuthorization("tenant.course.read");
    }

    private static async Task<IResult> ListCoursesAsync(HttpContext httpContext, LmsDbContext db, CancellationToken cancellationToken)
    {
        var canManage = httpContext.User.HasClaim("permission", LmsPermissions.CourseManage);
        var query = db.Courses.AsNoTracking().AsQueryable();
        if (!canManage) query = query.Where(item => item.Status == CourseStatus.Published);

        var courses = await query.OrderBy(item => item.Title)
            .Select(item => new CourseSummaryResponse(
                item.Id, item.Code, item.Slug, item.Title, item.Description,
                item.Status.ToString(), item.StartDateAd, item.EndDateAd,
                item.CurrentVersionId, item.PublishedAtUtc, item.Capacity, item.CategoryId,
                db.CourseCategories.Where(category => category.Id == item.CategoryId).Select(category => category.Name).FirstOrDefault()))
            .ToListAsync(cancellationToken);
        return Results.Ok(courses);
    }

    private static async Task<IResult> GetCourseAsync(
        HttpContext httpContext,
        LmsDbContext db,
        Guid courseId,
        string? version,
        CancellationToken cancellationToken)
    {
        var course = await db.Courses.AsNoTracking().SingleOrDefaultAsync(item => item.Id == courseId, cancellationToken);
        if (course is null || (!CanManage(httpContext) && course.Status != CourseStatus.Published)) return Results.NotFound();
        // Staff can open the in-progress copy of a published course; learners only ever see the live version.
        var showDraft = CanManage(httpContext) && string.Equals(version, "draft", StringComparison.OrdinalIgnoreCase);
        if (showDraft && course.DraftVersionId is null) return Results.NotFound(new { message = "This course has no version in progress." });
        return Results.Ok(await BuildDetailsAsync(db, course, cancellationToken, showDraft, CanManage(httpContext)));
    }

    private static async Task<IResult> GetOfflineManifestAsync(
        HttpContext httpContext,
        LmsDbContext db,
        Guid courseId,
        CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var course = await db.Courses.AsNoTracking().SingleOrDefaultAsync(item => item.Id == courseId && item.Status == CourseStatus.Published, cancellationToken);
        if (course is null || course.CurrentVersionId is not Guid versionId) return Results.NotFound();

        var canManage = HasPermission(httpContext, LmsPermissions.EnrollmentManage) || HasPermission(httpContext, LmsPermissions.CourseManage);
        var enrollment = canManage
            ? null
            : await db.Enrollments.AsNoTracking().SingleOrDefaultAsync(item => item.CourseId == courseId && item.LearnerUserId == userId && (item.Status == EnrollmentStatus.Active || item.Status == EnrollmentStatus.Completed), cancellationToken);
        if (!canManage && enrollment is null) return Results.NotFound(new { message = "An active enrollment is required to prepare offline learning content." });

        var modules = await db.CourseModules.AsNoTracking().Where(item => item.CourseVersionId == versionId).OrderBy(item => item.DisplayOrder).ToListAsync(cancellationToken);
        var moduleIds = modules.Select(item => item.Id).ToArray();
        var lessons = await db.CourseLessons.AsNoTracking().Where(item => moduleIds.Contains(item.CourseModuleId)).OrderBy(item => item.DisplayOrder).ToListAsync(cancellationToken);
        var assets = await db.ContentAssets.AsNoTracking().Where(item => item.CourseId == courseId && (item.CourseVersionId == versionId || item.CourseVersionId == null)).OrderBy(item => item.CreatedAtUtc).ToListAsync(cancellationToken);
        var generatedAtUtc = DateTimeOffset.UtcNow;
        var expiresAtUtc = generatedAtUtc.AddHours(24);

        return Results.Ok(new OfflineManifestResponse(
            course.Id,
            course.Code,
            course.Title,
            generatedAtUtc,
            expiresAtUtc,
            enrollment?.Id,
            modules.Select(module => new OfflineModuleResponse(
                module.Id,
                module.Title,
                module.Description,
                module.DisplayOrder,
                lessons.Where(lesson => lesson.CourseModuleId == module.Id)
                    .Select(lesson => new OfflineLessonResponse(lesson.Id, lesson.Title, lesson.Summary, lesson.ContentHtml, lesson.DisplayOrder))
                    .ToArray())).ToArray(),
            assets.Select(asset => new OfflineAssetResponse(
                asset.Id,
                asset.OriginalFileName,
                asset.ContentType,
                asset.SizeBytes,
                asset.Sha256,
                $"/api/v1/tenant/courses/{course.Id:D}/assets/{asset.Id:D}"))
                .ToArray()));
    }

    private static async Task<IResult> CreateCourseAsync(
        HttpContext httpContext,
        LmsDbContext db,
        ITenantContext tenantContext,
        CreateCourseRequest request,
        CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || GetUserId(httpContext) is not Guid userId)
            return Results.BadRequest(new { message = "A tenant and authenticated user are required." });
        var validation = ValidateCourse(request.Code, request.Title, request.StartDateAd, request.EndDateAd, request.Capacity);
        if (validation is not null) return validation;

        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Courses.AnyAsync(item => item.Code == code, cancellationToken))
            return Results.Conflict(new { message = "A course with this code already exists." });

        var now = DateTimeOffset.UtcNow;
        Guid? categoryId = null;
        if (!string.IsNullOrWhiteSpace(request.CategoryName))
        {
            var categorySlug = Slugify(request.CategoryName);
            var category = await db.CourseCategories.SingleOrDefaultAsync(item => item.Slug == categorySlug, cancellationToken);
            if (category is null)
            {
                category = new CourseCategory { Id = Guid.NewGuid(), TenantId = tenantId, Name = request.CategoryName.Trim(), Slug = categorySlug };
                db.CourseCategories.Add(category);
            }
            categoryId = category.Id;
        }
        if (request.CategoryId is Guid chosen)
        {
            if (!await db.CourseCategories.AnyAsync(item => item.Id == chosen, cancellationToken)) return Results.BadRequest(new { message = "The selected category does not exist." });
            categoryId = chosen;
        }
        var course = new Course
        {
            Id = Guid.NewGuid(), TenantId = tenantId, Code = code,
            Slug = await CreateUniqueSlugAsync(db, tenantId, request.Title, cancellationToken),
            Title = request.Title.Trim(), Description = request.Description?.Trim(),
            Status = CourseStatus.Draft, OwnerUserId = userId,
            CategoryId = categoryId,
            StartDateAd = request.StartDateAd, EndDateAd = request.EndDateAd,
            Capacity = request.Capacity,
            CreatedAtUtc = now, UpdatedAtUtc = now
        };
        var version = new CourseVersion
        {
            Id = Guid.NewGuid(), TenantId = tenantId, CourseId = course.Id,
            VersionNumber = 1, Status = CourseVersionStatus.Draft,
            ChangeSummary = "Initial course version", CreatedByUserId = userId, CreatedAtUtc = now
        };
        course.CurrentVersionId = version.Id;
        db.Courses.Add(course);
        db.CourseVersions.Add(version);
        db.CourseWorkflowEvents.Add(new CourseWorkflowEvent { Id = Guid.NewGuid(), TenantId = tenantId, CourseId = course.Id, ActorUserId = userId, EventType = "created", ToStatus = CourseStatus.Draft, Notes = "Initial course version created.", CreatedAtUtc = now });
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/courses/{course.Id:D}", await BuildDetailsAsync(db, course, cancellationToken));
    }

    private static async Task<IResult> UpdateCourseAsync(
        LmsDbContext db,
        Guid courseId,
        UpdateCourseRequest request,
        CancellationToken cancellationToken)
    {
        var course = await db.Courses.SingleOrDefaultAsync(item => item.Id == courseId, cancellationToken);
        if (course is null) return Results.NotFound();
        if (course.Status is CourseStatus.Published or CourseStatus.Archived)
            return Results.Conflict(new { message = "Published or archived courses cannot be edited in this version." });
        var validation = ValidateCourse(null, request.Title, request.StartDateAd, request.EndDateAd, request.Capacity);
        if (validation is not null) return validation;

        if (request.CategoryId is Guid chosen && !await db.CourseCategories.AnyAsync(item => item.Id == chosen, cancellationToken))
            return Results.BadRequest(new { message = "The selected category does not exist." });
        course.CategoryId = request.CategoryId; // no category when left out
        course.Title = request.Title.Trim();
        course.Description = request.Description?.Trim();
        course.StartDateAd = request.StartDateAd;
        course.EndDateAd = request.EndDateAd;
        course.Capacity = request.Capacity;
        course.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(await BuildDetailsAsync(db, course, cancellationToken));
    }

    private static async Task<IResult> SubmitReviewAsync(HttpContext httpContext, LmsDbContext db, Guid courseId, CancellationToken cancellationToken)
    {
        var course = await db.Courses.SingleOrDefaultAsync(item => item.Id == courseId, cancellationToken);
        if (course is null) return Results.NotFound();
        if (course.Status == CourseStatus.Published && course.DraftVersionId is Guid draftId)
        {
            var draft = await db.CourseVersions.SingleOrDefaultAsync(item => item.Id == draftId, cancellationToken);
            if (draft is null || draft.Status != CourseVersionStatus.Draft) return Results.Conflict(new { message = "Only a draft version can be submitted for review." });
            draft.Status = CourseVersionStatus.InReview;
            course.UpdatedAtUtc = DateTimeOffset.UtcNow;
            db.CourseWorkflowEvents.Add(CreateWorkflowEvent(course, httpContext, "version_submitted_for_review", CourseStatus.Published, CourseStatus.Published, $"Version {draft.VersionNumber} submitted for review."));
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(await BuildDetailsAsync(db, course, cancellationToken, true));
        }
        if (course.Status != CourseStatus.Draft) return Results.Conflict(new { message = "Only draft courses can be submitted for review." });
        var version = await CurrentVersionAsync(db, course, cancellationToken);
        if (version is null) return Results.Conflict(new { message = "The course has no current version." });
        var previousStatus = course.Status;
        course.Status = CourseStatus.InReview;
        course.UpdatedAtUtc = DateTimeOffset.UtcNow;
        version.Status = CourseVersionStatus.InReview;
        db.CourseWorkflowEvents.Add(CreateWorkflowEvent(course, httpContext, "submitted_for_review", previousStatus, CourseStatus.InReview, "Course submitted for review."));
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(await BuildDetailsAsync(db, course, cancellationToken));
    }

    private static async Task<IResult> PublishAsync(HttpContext httpContext, LmsDbContext db, CourseVersioningService versioning, Guid courseId, CancellationToken cancellationToken)
    {
        var course = await db.Courses.SingleOrDefaultAsync(item => item.Id == courseId, cancellationToken);
        if (course is null) return Results.NotFound();
        if (course.Status == CourseStatus.Published && course.DraftVersionId is Guid draftId)
        {
            var draft = await db.CourseVersions.SingleOrDefaultAsync(item => item.Id == draftId, cancellationToken);
            if (draft is null || draft.Status != CourseVersionStatus.InReview) return Results.Conflict(new { message = "Only a version in review can be published." });
            var summary = await versioning.PublishDraftAsync(db, course, draft, DateTimeOffset.UtcNow, cancellationToken);
            db.CourseWorkflowEvents.Add(CreateWorkflowEvent(course, httpContext, "version_published", CourseStatus.Published, CourseStatus.Published,
                $"Version {draft.VersionNumber} published. {summary.LessonsCarriedOver} lessons carried over, {summary.LessonsAdded} added, {summary.LessonsRemoved} removed; {summary.EnrollmentsUpdated} active learners' progress updated, {summary.LearnersCompleted} finished, {summary.LearnersNotified} notified."));
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(await BuildDetailsAsync(db, course, cancellationToken));
        }
        if (course.Status != CourseStatus.InReview) return Results.Conflict(new { message = "Only courses in review can be published." });
        var version = await CurrentVersionAsync(db, course, cancellationToken);
        if (version is null) return Results.Conflict(new { message = "The course has no current version." });
        var now = DateTimeOffset.UtcNow;
        var previousStatus = course.Status;
        course.Status = CourseStatus.Published;
        course.PublishedAtUtc = now;
        course.UpdatedAtUtc = now;
        version.Status = CourseVersionStatus.Published;
        version.PublishedAtUtc = now;
        db.CourseWorkflowEvents.Add(CreateWorkflowEvent(course, httpContext, "published", previousStatus, CourseStatus.Published, "Course version published."));
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(await BuildDetailsAsync(db, course, cancellationToken));
    }

    private static async Task<IResult> StartVersionAsync(HttpContext httpContext, LmsDbContext db, CourseVersioningService versioning, Guid courseId, StartVersionRequest? request, CancellationToken cancellationToken)
    {
        var course = await db.Courses.SingleOrDefaultAsync(item => item.Id == courseId, cancellationToken);
        if (course is null) return Results.NotFound();
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        if (request?.ChangeSummary is { Length: > 500 }) return Results.ValidationProblem(new Dictionary<string, string[]> { ["changeSummary"] = ["Describe the change in 500 characters or fewer."] });
        var (draft, error) = await versioning.StartDraftAsync(db, course, userId, request?.ChangeSummary, cancellationToken);
        if (draft is null) return Results.Conflict(new { message = error });
        db.CourseWorkflowEvents.Add(CreateWorkflowEvent(course, httpContext, "version_started", CourseStatus.Published, CourseStatus.Published, $"Version {draft.VersionNumber} started."));
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/courses/{courseId:D}?version=draft", await BuildDetailsAsync(db, course, cancellationToken, true));
    }

    private static async Task<IResult> DiscardVersionAsync(HttpContext httpContext, LmsDbContext db, CourseVersioningService versioning, Guid courseId, CancellationToken cancellationToken)
    {
        var course = await db.Courses.SingleOrDefaultAsync(item => item.Id == courseId, cancellationToken);
        if (course is null) return Results.NotFound();
        if (course.Status != CourseStatus.Published || course.DraftVersionId is null) return Results.NotFound(new { message = "This course has no version in progress." });
        await versioning.DiscardDraftAsync(db, course, cancellationToken);
        db.CourseWorkflowEvents.Add(CreateWorkflowEvent(course, httpContext, "version_discarded", CourseStatus.Published, CourseStatus.Published, "The version in progress was discarded."));
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(await BuildDetailsAsync(db, course, cancellationToken));
    }

    private static async Task<IResult> ArchiveAsync(HttpContext httpContext, LmsDbContext db, Guid courseId, CancellationToken cancellationToken)
    {
        var course = await db.Courses.SingleOrDefaultAsync(item => item.Id == courseId, cancellationToken);
        if (course is null) return Results.NotFound();
        var previousStatus = course.Status;
        course.Status = CourseStatus.Archived;
        course.UpdatedAtUtc = DateTimeOffset.UtcNow;
        var version = await CurrentVersionAsync(db, course, cancellationToken);
        if (version is not null) version.Status = CourseVersionStatus.Archived;
        db.CourseWorkflowEvents.Add(CreateWorkflowEvent(course, httpContext, "archived", previousStatus, CourseStatus.Archived, "Course archived."));
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(await BuildDetailsAsync(db, course, cancellationToken));
    }

    private static async Task<IResult> CreateModuleAsync(
        LmsDbContext db,
        CourseVersioningService versioning,
        Guid courseId,
        CreateModuleRequest request,
        CancellationToken cancellationToken)
    {
        var course = await db.Courses.SingleOrDefaultAsync(item => item.Id == courseId, cancellationToken);
        if (course is null) return Results.NotFound();
        var editableId = await versioning.EditableVersionIdAsync(db, course, cancellationToken);
        if (editableId is null) return Results.Conflict(new { message = NotEditable });
        if (string.IsNullOrWhiteSpace(request.Title)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = ["Module title is required."] });
        var displayOrder = await db.CourseModules.CountAsync(item => item.CourseVersionId == editableId, cancellationToken) + 1;
        var module = new CourseModule { Id = Guid.NewGuid(), TenantId = course.TenantId, CourseVersionId = editableId.Value, Title = request.Title.Trim(), Description = request.Description?.Trim(), DisplayOrder = displayOrder };
        db.CourseModules.Add(module);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/courses/{courseId:D}/modules/{module.Id:D}", module);
    }

    private static async Task<IResult> UpdateModuleAsync(
        LmsDbContext db,
        CourseVersioningService versioning,
        Guid courseId,
        Guid moduleId,
        UpdateModuleRequest request,
        CancellationToken cancellationToken)
    {
        var course = await db.Courses.SingleOrDefaultAsync(item => item.Id == courseId, cancellationToken);
        var module = await db.CourseModules.SingleOrDefaultAsync(item => item.Id == moduleId, cancellationToken);
        if (course is null || module is null) return Results.NotFound();
        if (await versioning.EditableVersionIdAsync(db, course, cancellationToken) != module.CourseVersionId) return Results.Conflict(new { message = NotEditable });
        if (string.IsNullOrWhiteSpace(request.Title)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = ["Module title is required."] });
        module.Title = request.Title.Trim();
        module.Description = request.Description?.Trim();
        if (request.DisplayOrder is > 0) module.DisplayOrder = request.DisplayOrder.Value;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(module);
    }

    private static async Task<IResult> CreateLessonAsync(
        LmsDbContext db,
        CourseVersioningService versioning,
        Guid courseId,
        Guid moduleId,
        CreateLessonRequest request,
        CancellationToken cancellationToken)
    {
        var course = await db.Courses.SingleOrDefaultAsync(item => item.Id == courseId, cancellationToken);
        var module = await db.CourseModules.SingleOrDefaultAsync(item => item.Id == moduleId, cancellationToken);
        if (course is null || module is null) return Results.NotFound();
        if (await versioning.EditableVersionIdAsync(db, course, cancellationToken) != module.CourseVersionId) return Results.Conflict(new { message = NotEditable });
        if (string.IsNullOrWhiteSpace(request.Title)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = ["Lesson title is required."] });
        var displayOrder = await db.CourseLessons.CountAsync(item => item.CourseModuleId == moduleId, cancellationToken) + 1;
        var lesson = new CourseLesson { Id = Guid.NewGuid(), TenantId = course.TenantId, CourseModuleId = moduleId, Title = request.Title.Trim(), Summary = request.Summary?.Trim(), ContentHtml = request.ContentHtml, DisplayOrder = displayOrder };
        db.CourseLessons.Add(lesson);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/courses/{courseId:D}/lessons/{lesson.Id:D}", lesson);
    }

    private static async Task<IResult> UpdateLessonAsync(
        LmsDbContext db,
        CourseVersioningService versioning,
        Guid courseId,
        Guid lessonId,
        UpdateLessonRequest request,
        CancellationToken cancellationToken)
    {
        var course = await db.Courses.SingleOrDefaultAsync(item => item.Id == courseId, cancellationToken);
        var lesson = await db.CourseLessons.SingleOrDefaultAsync(item => item.Id == lessonId, cancellationToken);
        if (course is null || lesson is null) return Results.NotFound();
        var lessonVersionId = await db.CourseModules.Where(item => item.Id == lesson.CourseModuleId).Select(item => (Guid?)item.CourseVersionId).SingleOrDefaultAsync(cancellationToken);
        if (lessonVersionId is null || await versioning.EditableVersionIdAsync(db, course, cancellationToken) != lessonVersionId) return Results.Conflict(new { message = NotEditable });
        if (string.IsNullOrWhiteSpace(request.Title)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = ["Lesson title is required."] });
        lesson.Title = request.Title.Trim();
        lesson.Summary = request.Summary?.Trim();
        lesson.ContentHtml = request.ContentHtml;
        if (request.DisplayOrder is > 0) lesson.DisplayOrder = request.DisplayOrder.Value;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(lesson);
    }

    // ---------- removing and ordering content (only in the version that can be edited) ----------
    private static async Task<IResult> DeleteLessonAsync(LmsDbContext db, CourseVersioningService versioning, Guid courseId, Guid lessonId, CancellationToken cancellationToken)
    {
        var course = await db.Courses.SingleOrDefaultAsync(item => item.Id == courseId, cancellationToken);
        var lesson = await db.CourseLessons.SingleOrDefaultAsync(item => item.Id == lessonId, cancellationToken);
        if (course is null || lesson is null) return Results.NotFound();
        var module = await db.CourseModules.SingleAsync(item => item.Id == lesson.CourseModuleId, cancellationToken);
        if (await versioning.EditableVersionIdAsync(db, course, cancellationToken) != module.CourseVersionId) return Results.Conflict(new { message = NotEditable });
        db.LessonBlocks.RemoveRange(await db.LessonBlocks.Where(item => item.CourseLessonId == lessonId).ToListAsync(cancellationToken));
        db.CourseLessons.Remove(lesson);
        var rest = await db.CourseLessons.Where(item => item.CourseModuleId == module.Id && item.Id != lessonId).OrderBy(item => item.DisplayOrder).ToListAsync(cancellationToken);
        for (var i = 0; i < rest.Count; i++) rest[i].DisplayOrder = i + 1;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteModuleAsync(LmsDbContext db, CourseVersioningService versioning, Guid courseId, Guid moduleId, CancellationToken cancellationToken)
    {
        var course = await db.Courses.SingleOrDefaultAsync(item => item.Id == courseId, cancellationToken);
        var module = await db.CourseModules.SingleOrDefaultAsync(item => item.Id == moduleId, cancellationToken);
        if (course is null || module is null) return Results.NotFound();
        if (await versioning.EditableVersionIdAsync(db, course, cancellationToken) != module.CourseVersionId) return Results.Conflict(new { message = NotEditable });
        var lessonIds = await db.CourseLessons.Where(item => item.CourseModuleId == moduleId).Select(item => item.Id).ToListAsync(cancellationToken);
        db.LessonBlocks.RemoveRange(await db.LessonBlocks.Where(item => lessonIds.Contains(item.CourseLessonId)).ToListAsync(cancellationToken));
        db.CourseLessons.RemoveRange(await db.CourseLessons.Where(item => item.CourseModuleId == moduleId).ToListAsync(cancellationToken));
        // Opening rules for this module go with it, and other modules stop waiting for it.
        db.ModuleAccessRules.RemoveRange(await db.ModuleAccessRules.Where(item => item.ModuleId == moduleId).ToListAsync(cancellationToken));
        foreach (var rule in await db.ModuleAccessRules.Where(item => item.RequiresModuleId == moduleId).ToListAsync(cancellationToken)) rule.RequiresModuleId = null;
        db.CourseModules.Remove(module);
        var rest = await db.CourseModules.Where(item => item.CourseVersionId == module.CourseVersionId && item.Id != moduleId).OrderBy(item => item.DisplayOrder).ToListAsync(cancellationToken);
        for (var i = 0; i < rest.Count; i++) rest[i].DisplayOrder = i + 1;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ReorderModulesAsync(LmsDbContext db, CourseVersioningService versioning, Guid courseId, OrderRequest request, CancellationToken cancellationToken)
    {
        var course = await db.Courses.SingleOrDefaultAsync(item => item.Id == courseId, cancellationToken);
        if (course is null) return Results.NotFound();
        if (await versioning.EditableVersionIdAsync(db, course, cancellationToken) is not Guid versionId) return Results.Conflict(new { message = NotEditable });
        var modules = await db.CourseModules.Where(item => item.CourseVersionId == versionId).ToListAsync(cancellationToken);
        if (!IsExactOrder(request.Ids, modules.Select(item => item.Id))) return Results.BadRequest(new { message = "Send every module of the course exactly once." });
        for (var i = 0; i < request.Ids!.Count; i++) modules.Single(item => item.Id == request.Ids[i]).DisplayOrder = i + 1;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ReorderLessonsAsync(LmsDbContext db, CourseVersioningService versioning, Guid courseId, Guid moduleId, OrderRequest request, CancellationToken cancellationToken)
    {
        var course = await db.Courses.SingleOrDefaultAsync(item => item.Id == courseId, cancellationToken);
        var module = await db.CourseModules.SingleOrDefaultAsync(item => item.Id == moduleId, cancellationToken);
        if (course is null || module is null) return Results.NotFound();
        if (await versioning.EditableVersionIdAsync(db, course, cancellationToken) != module.CourseVersionId) return Results.Conflict(new { message = NotEditable });
        var lessons = await db.CourseLessons.Where(item => item.CourseModuleId == moduleId).ToListAsync(cancellationToken);
        if (!IsExactOrder(request.Ids, lessons.Select(item => item.Id))) return Results.BadRequest(new { message = "Send every lesson of the module exactly once." });
        for (var i = 0; i < request.Ids!.Count; i++) lessons.Single(item => item.Id == request.Ids[i]).DisplayOrder = i + 1;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static bool IsExactOrder(List<Guid>? ids, IEnumerable<Guid> existing)
    {
        var expected = existing.ToHashSet();
        return ids is not null && ids.Count == expected.Count && ids.Distinct().Count() == ids.Count && ids.All(expected.Contains);
    }

    private static async Task<IResult> UploadAssetAsync(
        HttpRequest httpRequest,
        LmsDbContext db,
        CourseVersioningService versioning,
        IContentAssetStorage storage,
        Guid courseId,
        CancellationToken cancellationToken)
    {
        var course = await db.Courses.SingleOrDefaultAsync(item => item.Id == courseId, cancellationToken);
        if (course is null) return Results.NotFound();
        var editableId = await versioning.EditableVersionIdAsync(db, course, cancellationToken);
        if (course.Status is CourseStatus.Published or CourseStatus.Archived && editableId is null) return Results.Conflict(new { message = "Assets can only be added to an unpublished course or a new version in progress." });
        var form = await httpRequest.ReadFormAsync(cancellationToken);
        var file = form.Files.FirstOrDefault();
        if (file is null || file.Length == 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Choose a non-empty file."] });
        if (file.Length > MaximumAssetBytes) return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Files must be 100 MB or smaller."] });
        var userId = GetUserId(httpRequest.HttpContext);
        if (userId is not Guid createdByUserId) return Results.BadRequest(new { message = "An authenticated user is required." });
        var stored = await storage.SaveAsync(course.TenantId, course.Id, file, cancellationToken);
        var asset = new ContentAsset
        {
            Id = Guid.NewGuid(), TenantId = course.TenantId, CourseId = course.Id,
            CourseVersionId = editableId ?? course.CurrentVersionId, OriginalFileName = stored.OriginalFileName,
            StorageKey = stored.StorageKey, ContentType = stored.ContentType,
            SizeBytes = stored.SizeBytes, Sha256 = stored.Sha256,
            CreatedByUserId = createdByUserId, CreatedAtUtc = DateTimeOffset.UtcNow
        };
        db.ContentAssets.Add(asset);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/courses/{course.Id:D}/assets/{asset.Id:D}", ToAssetResponse(asset));
    }

    /// <summary>The asset if this user may read it: staff always, learners only for published courses they are enrolled in.</summary>
    private static async Task<ContentAsset?> FindReadableAssetAsync(HttpContext httpContext, LmsDbContext db, Guid courseId, Guid assetId, CancellationToken cancellationToken)
    {
        var course = await db.Courses.AsNoTracking().SingleOrDefaultAsync(item => item.Id == courseId, cancellationToken);
        var asset = await db.ContentAssets.AsNoTracking().SingleOrDefaultAsync(item => item.Id == assetId && item.CourseId == courseId, cancellationToken);
        if (course is null || asset is null) return null;
        if (CanManage(httpContext)) return asset;
        if (course.Status != CourseStatus.Published) return null;
        // Files that only belong to a version still being written are not visible to learners.
        if (asset.CourseVersionId is Guid assetVersion && assetVersion != course.CurrentVersionId) return null;
        // Being in the tenant is not enough: learners must be enrolled in the course.
        var userId = GetUserId(httpContext);
        var enrolled = userId is Guid learnerId && await db.Enrollments.AnyAsync(item => item.CourseId == courseId && item.LearnerUserId == learnerId
            && (item.Status == EnrollmentStatus.Active || item.Status == EnrollmentStatus.Completed), cancellationToken);
        return enrolled ? asset : null;
    }

    private static async Task<IResult> DownloadAssetAsync(
        HttpContext httpContext,
        LmsDbContext db,
        IContentAssetStorage storage,
        Guid courseId,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        var asset = await FindReadableAssetAsync(httpContext, db, courseId, assetId, cancellationToken);
        if (asset is null) return Results.NotFound();
        var stream = await storage.OpenReadAsync(asset.StorageKey, cancellationToken);
        if (stream is null) return Results.NotFound();
        httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff"; // never let a browser guess a more dangerous type
        return Results.Stream(stream, asset.ContentType, asset.OriginalFileName, enableRangeProcessing: stream.CanSeek);
    }

    /// <summary>
    /// A short-lived direct link so the browser can stream media from object storage. The same access rules as the
    /// download apply. Returns a null url when the storage provider has no direct links; callers then use the download.
    /// </summary>
    private static async Task<IResult> AssetLinkAsync(
        HttpContext httpContext,
        LmsDbContext db,
        IContentAssetStorage storage,
        Guid courseId,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        var asset = await FindReadableAssetAsync(httpContext, db, courseId, assetId, cancellationToken);
        if (asset is null) return Results.NotFound();
        var url = await storage.CreateTemporaryUrlAsync(asset.StorageKey, asset.ContentType, asset.OriginalFileName, BlockFileRules.IsInlineType(asset.ContentType), cancellationToken);
        httpContext.Response.Headers.CacheControl = "no-store"; // the link is a credential; never cache it
        var seconds = httpContext.RequestServices.GetService<S3StorageOptions>()?.PresignSeconds ?? 0;
        return Results.Ok(new AssetLinkResponse(url?.ToString(), url is null ? null : DateTimeOffset.UtcNow.AddSeconds(seconds)));
    }

    private const string NotEditable = "Content can only be edited while the course is a draft, or in a new version that has not been submitted for review.";

    private static async Task<CourseDetailResponse> BuildDetailsAsync(LmsDbContext db, Course course, CancellationToken cancellationToken, bool draft = false, bool staff = true)
    {
        var categoryName = course.CategoryId is Guid categoryId ? await db.CourseCategories.AsNoTracking().Where(item => item.Id == categoryId).Select(item => item.Name).SingleOrDefaultAsync(cancellationToken) : null;
        var draftVersion = staff && course.DraftVersionId is Guid draftId ? await db.CourseVersions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == draftId, cancellationToken) : null;
        var version = draft && draftVersion is not null
            ? draftVersion
            : await CurrentVersionAsync(db, course, cancellationToken);
        var modules = version is null
            ? []
            : await db.CourseModules.AsNoTracking().Where(item => item.CourseVersionId == version.Id).OrderBy(item => item.DisplayOrder).ToListAsync(cancellationToken);
        var lessons = modules.Count == 0
            ? []
            : await db.CourseLessons.AsNoTracking().Where(item => modules.Select(module => module.Id).Contains(item.CourseModuleId)).OrderBy(item => item.DisplayOrder).ToListAsync(cancellationToken);
        var workflow = await db.CourseWorkflowEvents.AsNoTracking().Where(item => item.CourseId == course.Id).OrderByDescending(item => item.CreatedAtUtc).Select(item => new CourseWorkflowEventResponse(item.Id, item.EventType, item.FromStatus.HasValue ? item.FromStatus.Value.ToString() : null, item.ToStatus.ToString(), item.Notes, item.CreatedAtUtc)).ToArrayAsync(cancellationToken);
        return new CourseDetailResponse(
            new CourseSummaryResponse(course.Id, course.Code, course.Slug, course.Title, course.Description, course.Status.ToString(), course.StartDateAd, course.EndDateAd, course.CurrentVersionId, course.PublishedAtUtc, course.Capacity, course.CategoryId, categoryName),
            version is null ? null : new CourseVersionResponse(version.Id, version.VersionNumber, version.Status.ToString(), version.ChangeSummary, version.CreatedAtUtc, version.PublishedAtUtc),
            modules.Select(module => new CourseModuleResponse(module.Id, module.Title, module.Description, module.DisplayOrder, lessons.Where(lesson => lesson.CourseModuleId == module.Id).Select(lesson => new CourseLessonResponse(lesson.Id, lesson.Title, lesson.Summary, lesson.ContentHtml, lesson.DisplayOrder)).ToArray())).ToArray(),
            workflow,
            draftVersion is null ? null : new CourseVersionResponse(draftVersion.Id, draftVersion.VersionNumber, draftVersion.Status.ToString(), draftVersion.ChangeSummary, draftVersion.CreatedAtUtc, draftVersion.PublishedAtUtc),
            draft && draftVersion is not null);
    }

    private static Task<CourseVersion?> CurrentVersionAsync(LmsDbContext db, Course course, CancellationToken cancellationToken) =>
        db.CourseVersions.SingleOrDefaultAsync(item => item.Id == course.CurrentVersionId && item.CourseId == course.Id, cancellationToken);

    private static IResult? ValidateCourse(string? code, string title, DateOnly? startDateAd, DateOnly? endDateAd, int? capacity)
    {
        var errors = new Dictionary<string, string[]>();
        if (code is not null && (string.IsNullOrWhiteSpace(code) || code.Trim().Length > 80)) errors["code"] = ["Course code is required and must be 80 characters or fewer."];
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 250) errors["title"] = ["Course title is required and must be 250 characters or fewer."];
        if (startDateAd is not null && endDateAd is not null && startDateAd > endDateAd) errors["endDateAd"] = ["The end date must be on or after the start date."];
        if (capacity is <= 0 or > 1_000_000) errors["capacity"] = ["Capacity must be between 1 and 1,000,000 when supplied."];
        return errors.Count == 0 ? null : Results.ValidationProblem(errors);
    }

    private static async Task<string> CreateUniqueSlugAsync(LmsDbContext db, Guid tenantId, string title, CancellationToken cancellationToken)
    {
        var baseSlug = Slugify(title);
        var slug = baseSlug;
        var suffix = 2;
        while (await db.Courses.IgnoreQueryFilters().AnyAsync(item => item.TenantId == tenantId && item.Slug == slug, cancellationToken)) slug = $"{baseSlug}-{suffix++}";
        return slug;
    }

    private static string Slugify(string value)
    {
        var slug = Regex.Replace(value.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "course" : slug[..Math.Min(slug.Length, 140)];
    }

    private static bool CanManage(HttpContext httpContext) => httpContext.User.HasClaim("permission", LmsPermissions.CourseManage);

    private static bool HasPermission(HttpContext httpContext, string permission) => httpContext.User.HasClaim("permission", permission);

    private static Guid? GetUserId(HttpContext httpContext) => Guid.TryParse(httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private static AssetResponse ToAssetResponse(ContentAsset asset) => new(asset.Id, asset.OriginalFileName, asset.ContentType, asset.SizeBytes, asset.Sha256, asset.CreatedAtUtc);

    private static CourseWorkflowEvent CreateWorkflowEvent(Course course, HttpContext httpContext, string eventType, CourseStatus fromStatus, CourseStatus toStatus, string notes) =>
        new() { Id = Guid.NewGuid(), TenantId = course.TenantId, CourseId = course.Id, ActorUserId = GetUserId(httpContext) ?? Guid.Empty, EventType = eventType, FromStatus = fromStatus, ToStatus = toStatus, Notes = notes, CreatedAtUtc = DateTimeOffset.UtcNow };
}

public sealed record CreateCourseRequest(string Code, string Title, string? Description = null, string? CategoryName = null, DateOnly? StartDateAd = null, DateOnly? EndDateAd = null, int? Capacity = null, Guid? CategoryId = null);
public sealed record UpdateCourseRequest(string Title, string? Description = null, DateOnly? StartDateAd = null, DateOnly? EndDateAd = null, int? Capacity = null, Guid? CategoryId = null);
public sealed record CreateModuleRequest(string Title, string? Description = null);
public sealed record UpdateModuleRequest(string Title, string? Description = null, int? DisplayOrder = null);
public sealed record CreateLessonRequest(string Title, string? Summary = null, string? ContentHtml = null);
public sealed record UpdateLessonRequest(string Title, string? Summary = null, string? ContentHtml = null, int? DisplayOrder = null);
public sealed record CourseSummaryResponse(Guid Id, string Code, string Slug, string Title, string? Description, string Status, DateOnly? StartDateAd, DateOnly? EndDateAd, Guid? CurrentVersionId, DateTimeOffset? PublishedAtUtc, int? Capacity = null, Guid? CategoryId = null, string? CategoryName = null);
public sealed record CourseVersionResponse(Guid Id, int VersionNumber, string Status, string? ChangeSummary, DateTimeOffset CreatedAtUtc, DateTimeOffset? PublishedAtUtc);
public sealed record CourseLessonResponse(Guid Id, string Title, string? Summary, string? ContentHtml, int DisplayOrder);
public sealed record CourseModuleResponse(Guid Id, string Title, string? Description, int DisplayOrder, CourseLessonResponse[] Lessons);
public sealed record CourseDetailResponse(CourseSummaryResponse Course, CourseVersionResponse? CurrentVersion, CourseModuleResponse[] Modules, CourseWorkflowEventResponse[] Workflow, CourseVersionResponse? DraftVersion = null, bool ViewingDraft = false);
public sealed record StartVersionRequest(string? ChangeSummary);
public sealed record OrderRequest(List<Guid>? Ids);
public sealed record CourseWorkflowEventResponse(Guid Id, string EventType, string? FromStatus, string ToStatus, string? Notes, DateTimeOffset CreatedAtUtc);
public sealed record AssetResponse(Guid Id, string OriginalFileName, string ContentType, long SizeBytes, string Sha256, DateTimeOffset CreatedAtUtc);
public sealed record OfflineManifestResponse(Guid CourseId, string CourseCode, string CourseTitle, DateTimeOffset GeneratedAtUtc, DateTimeOffset ExpiresAtUtc, Guid? EnrollmentId, OfflineModuleResponse[] Modules, OfflineAssetResponse[] Assets);
public sealed record OfflineModuleResponse(Guid Id, string Title, string? Description, int DisplayOrder, OfflineLessonResponse[] Lessons);
public sealed record OfflineLessonResponse(Guid Id, string Title, string? Summary, string? ContentHtml, int DisplayOrder);
public sealed record OfflineAssetResponse(Guid Id, string OriginalFileName, string ContentType, long SizeBytes, string Sha256, string DownloadUrl);

public sealed record AssetLinkResponse(string? Url, DateTimeOffset? ExpiresAtUtc);
