using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lms.Api.Domain.Courses;
using Lms.Api.Domain.Learning;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Catalog;

/// <summary>Course categories, learning paths and shared learning resources.</summary>
public static class CatalogEndpoints
{
    public static void MapCatalogEndpoints(this WebApplication app)
    {
        var tenant = app.MapGroup("/api/v1/tenant/catalog").RequireAuthorization("tenant.authenticated");
        tenant.AddEndpointFilter(async (context, next) =>
        {
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (!tenantContext.IsResolved)
                return Results.BadRequest(new { message = "A tenant is required. Use the X-Tenant-Slug header or a tenant subdomain." });
            return await next(context);
        });

        tenant.MapGet("/categories", ListCategoriesAsync).RequireAuthorization("tenant.course.read");
        tenant.MapPost("/categories", CreateCategoryAsync).RequireAuthorization("tenant.course.manage");
        tenant.MapDelete("/categories/{categoryId:guid}", DeleteCategoryAsync).RequireAuthorization("tenant.course.manage");

        tenant.MapGet("/paths", ListPathsAsync).RequireAuthorization("tenant.course.read");
        tenant.MapPost("/paths", CreatePathAsync).RequireAuthorization("tenant.course.manage");
        tenant.MapDelete("/paths/{pathId:guid}", DeletePathAsync).RequireAuthorization("tenant.course.manage");

        tenant.MapGet("/resources", ListResourcesAsync).RequireAuthorization("tenant.course.read");
        tenant.MapPost("/resources", CreateResourceAsync).RequireAuthorization("tenant.course.manage");
        tenant.MapDelete("/resources/{resourceId:guid}", DeleteResourceAsync).RequireAuthorization("tenant.course.manage");
    }

    // ---------- categories ----------
    private static async Task<IResult> ListCategoriesAsync(LmsDbContext db, CancellationToken cancellationToken)
    {
        var categories = await db.CourseCategories.AsNoTracking().OrderBy(item => item.Name).ToListAsync(cancellationToken);
        var counts = await db.Courses.AsNoTracking().Where(item => item.CategoryId != null)
            .GroupBy(item => item.CategoryId!.Value).Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, cancellationToken);
        return Results.Ok(categories.Select(item => new CategoryResponse(item.Id, item.Name, item.Slug, counts.GetValueOrDefault(item.Id))));
    }

    private static async Task<IResult> CreateCategoryAsync(ITenantContext tenantContext, LmsDbContext db, CreateCategoryRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId) return Results.BadRequest(new { message = "A tenant is required." });
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is < 2 or > 150) return Results.BadRequest(new { message = "Category name must be between 2 and 150 characters." });
        var slug = Slugify(name);
        if (slug.Length == 0) return Results.BadRequest(new { message = "Category name must contain letters or numbers." });
        if (await db.CourseCategories.AnyAsync(item => item.Slug == slug, cancellationToken))
            return Results.Conflict(new { message = "A category with this name already exists." });
        var category = new CourseCategory { Id = Guid.NewGuid(), TenantId = tenantId, Name = name, Slug = slug };
        db.CourseCategories.Add(category);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/catalog/categories/{category.Id}", new CategoryResponse(category.Id, category.Name, category.Slug, 0));
    }

    private static async Task<IResult> DeleteCategoryAsync(Guid categoryId, LmsDbContext db, CancellationToken cancellationToken)
    {
        var category = await db.CourseCategories.SingleOrDefaultAsync(item => item.Id == categoryId, cancellationToken);
        if (category is null) return Results.NotFound();
        if (await db.Courses.AnyAsync(item => item.CategoryId == categoryId, cancellationToken))
            return Results.Conflict(new { message = "This category is used by courses and cannot be deleted." });
        db.CourseCategories.Remove(category);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    // ---------- learning paths ----------
    private static async Task<IResult> ListPathsAsync(LmsDbContext db, CancellationToken cancellationToken)
    {
        var paths = await db.LearningPaths.AsNoTracking().OrderBy(item => item.Title).ToListAsync(cancellationToken);
        var courseTitles = await db.Courses.AsNoTracking().ToDictionaryAsync(item => item.Id, item => new { item.Title, item.Code }, cancellationToken);
        return Results.Ok(paths.Select(path =>
        {
            var courses = ParseIds(path.ItemsJson)
                .Where(courseTitles.ContainsKey)
                .Select(id => new PathCourse(id, courseTitles[id].Code, courseTitles[id].Title))
                .ToList();
            return new LearningPathResponse(path.Id, path.Title, path.Description, courses, path.CreatedAtUtc);
        }));
    }

    private static async Task<IResult> CreatePathAsync(HttpContext httpContext, ITenantContext tenantContext, LmsDbContext db, CreatePathRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId) return Results.BadRequest(new { message = "A tenant is required." });
        var title = request.Title?.Trim() ?? string.Empty;
        if (title.Length is < 2 or > 250) return Results.BadRequest(new { message = "Title must be between 2 and 250 characters." });
        var courseIds = (request.CourseIds ?? []).Distinct().ToList();
        if (courseIds.Count == 0) return Results.BadRequest(new { message = "Choose at least one course for the path." });
        var known = await db.Courses.Where(item => courseIds.Contains(item.Id)).Select(item => item.Id).ToListAsync(cancellationToken);
        if (known.Count != courseIds.Count) return Results.BadRequest(new { message = "One or more selected courses do not exist." });
        var now = DateTimeOffset.UtcNow;
        var path = new LearningPath
        {
            Id = Guid.NewGuid(), TenantId = tenantId, Title = title,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            ItemsJson = JsonSerializer.Serialize(courseIds), CreatedByUserId = GetUserId(httpContext), CreatedAtUtc = now, UpdatedAtUtc = now
        };
        db.LearningPaths.Add(path);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/catalog/paths/{path.Id}", new { path.Id });
    }

    private static async Task<IResult> DeletePathAsync(Guid pathId, LmsDbContext db, CancellationToken cancellationToken)
    {
        var path = await db.LearningPaths.SingleOrDefaultAsync(item => item.Id == pathId, cancellationToken);
        if (path is null) return Results.NotFound();
        db.LearningPaths.Remove(path);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    // ---------- resources (link/reference resources; file upload uses course assets) ----------
    private static async Task<IResult> ListResourcesAsync(LmsDbContext db, string? type, CancellationToken cancellationToken)
    {
        var query = db.LearningResources.AsNoTracking();
        if (Enum.TryParse<ResourceType>(type, true, out var parsed)) query = query.Where(item => item.Type == parsed);
        var resources = await query.OrderByDescending(item => item.UploadedAtUtc).ToListAsync(cancellationToken);
        return Results.Ok(resources.Select(item => new ResourceResponse(item.Id, item.Type.ToString(), item.Title, item.Description, ReadUrl(item.MetadataJson), item.UploadedAtUtc)));
    }

    private static async Task<IResult> CreateResourceAsync(HttpContext httpContext, ITenantContext tenantContext, LmsDbContext db, CreateResourceRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId) return Results.BadRequest(new { message = "A tenant is required." });
        var title = request.Title?.Trim() ?? string.Empty;
        if (title.Length is < 2 or > 300) return Results.BadRequest(new { message = "Title must be between 2 and 300 characters." });
        if (!Enum.TryParse<ResourceType>(request.Type, true, out var type)) return Results.BadRequest(new { message = "Unknown resource type." });
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return Results.BadRequest(new { message = "A valid http(s) URL is required." });
        var resource = new LearningResource
        {
            Id = Guid.NewGuid(), TenantId = tenantId, Type = type, Title = title,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            MetadataJson = JsonSerializer.Serialize(new { url = uri.ToString() }),
            UploadedByUserId = GetUserId(httpContext), UploadedAtUtc = DateTimeOffset.UtcNow
        };
        db.LearningResources.Add(resource);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/catalog/resources/{resource.Id}", new ResourceResponse(resource.Id, resource.Type.ToString(), resource.Title, resource.Description, uri.ToString(), resource.UploadedAtUtc));
    }

    private static async Task<IResult> DeleteResourceAsync(Guid resourceId, LmsDbContext db, CancellationToken cancellationToken)
    {
        var resource = await db.LearningResources.SingleOrDefaultAsync(item => item.Id == resourceId, cancellationToken);
        if (resource is null) return Results.NotFound();
        db.LearningResources.Remove(resource);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    // ---------- helpers ----------
    private static Guid? GetUserId(HttpContext context)
        => Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub"), out var id) ? id : null;

    private static IEnumerable<Guid> ParseIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<Guid>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string? ReadUrl(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            return document.RootElement.TryGetProperty("url", out var url) ? url.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static string Slugify(string value)
        => Regex.Replace(value.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
}

public sealed record CreateCategoryRequest(string? Name);
public sealed record CategoryResponse(Guid Id, string Name, string Slug, int CourseCount);
public sealed record CreatePathRequest(string? Title, string? Description, List<Guid>? CourseIds);
public sealed record PathCourse(Guid Id, string Code, string Title);
public sealed record LearningPathResponse(Guid Id, string Title, string? Description, IReadOnlyList<PathCourse> Courses, DateTimeOffset CreatedAtUtc);
public sealed record CreateResourceRequest(string? Type, string? Title, string? Description, string? Url);
public sealed record ResourceResponse(Guid Id, string Type, string Title, string? Description, string? Url, DateTimeOffset UploadedAtUtc);
