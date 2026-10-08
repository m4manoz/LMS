using System.Security.Claims;
using Lms.Api.Domain.Courses;
using Lms.Api.Domain.Landing;
using Lms.Api.Domain.Tenants;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Storage;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Landing;

/// <summary>
/// The pictures on an organization's public page: its logo, a main image and banner pictures. Staff upload PNG, JPEG, WebP or GIF files (never SVG,
/// which can carry scripts); the public page loads them without signing in, so they are the only files served to anonymous visitors.
/// </summary>
public static class LandingImageEndpoints
{
    private const int MaxBytes = 3 * 1024 * 1024;
    private const int MaxImages = 30;
    private static readonly string[] AllowedTypes = ["image/png", "image/jpeg", "image/webp", "image/gif"];

    public static void MapLandingImageEndpoints(this WebApplication app)
    {
        var tenant = app.MapGroup("/api/v1/tenant/landing/images").RequireAuthorization("tenant.authenticated");
        tenant.AddEndpointFilter(async (context, next) =>
            context.HttpContext.RequestServices.GetRequiredService<ITenantContext>().IsResolved
                ? await next(context)
                : Results.BadRequest(new { message = "A tenant is required. Use the X-Tenant-Slug header or a tenant subdomain." }));
        tenant.MapGet("", ListAsync).RequireAuthorization("tenant.tenant.manage");
        tenant.MapPost("", UploadAsync).RequireAuthorization("tenant.tenant.manage");
        tenant.MapDelete("/{imageId:guid}", DeleteAsync).RequireAuthorization("tenant.tenant.manage");

        app.MapGet("/api/v1/public/{slug}/landing-images/{imageId:guid}", ServeAsync).AllowAnonymous();
    }

    private static async Task<IResult> ListAsync(LmsDbContext db, CancellationToken cancellationToken)
        => Results.Ok((await db.LandingImages.AsNoTracking().OrderByDescending(item => item.CreatedAtUtc).ToListAsync(cancellationToken)).Select(ToView).ToList());

    private static ImageView ToView(LandingImage item) => new(item.Id, item.FileName, item.SizeBytes);

    private static async Task<IResult> UploadAsync(HttpRequest request, ITenantContext tenantContext, LmsDbContext db, IContentAssetStorage storage, CancellationToken cancellationToken)
    {
        var httpContext = request.HttpContext;
        if (tenantContext.TenantId is not Guid tenantId || !Guid.TryParse(httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Results.Unauthorized();
        var (file, _) = await AttachmentRules.ReadAsync(request, cancellationToken);
        if (file is null || file.Length == 0) return Results.BadRequest(new { message = "Choose a picture to upload." });
        if (file.Length > MaxBytes) return Results.BadRequest(new { message = "Pictures must be 3 MB or smaller." });
        var contentType = (file.ContentType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
        var header = new byte[16];
        await using (var stream = file.OpenReadStream()) _ = await stream.ReadAsync(header, cancellationToken);
        // The declared type is not trusted: the first bytes of the file must match it.
        if (!AllowedTypes.Contains(contentType) || !BlockFileRules.MatchesSignature(contentType, header))
            return Results.BadRequest(new { message = "Upload a PNG, JPEG, WebP or GIF picture." });
        if (await db.LandingImages.CountAsync(cancellationToken) >= MaxImages) return Results.Conflict(new { message = $"There can be at most {MaxImages} pictures. Delete one you no longer use." });

        var stored = await storage.SaveAsync(tenantId, Guid.Empty, file, cancellationToken);
        var image = new LandingImage { Id = Guid.NewGuid(), TenantId = tenantId, FileName = stored.OriginalFileName, ContentType = contentType, SizeBytes = stored.SizeBytes, StorageKey = stored.StorageKey, UploadedByUserId = userId, CreatedAtUtc = DateTimeOffset.UtcNow };
        db.LandingImages.Add(image);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/landing/images/{image.Id}", ToView(image));
    }

    private static async Task<IResult> DeleteAsync(Guid imageId, LmsDbContext db, IContentAssetStorage storage, CancellationToken cancellationToken)
    {
        var image = await db.LandingImages.SingleOrDefaultAsync(item => item.Id == imageId, cancellationToken);
        if (image is null) return Results.NotFound();
        // A page that still points at it simply shows no picture there; the public page drops ids that no longer exist.
        db.LandingImages.Remove(image);
        await db.SaveChangesAsync(cancellationToken);
        await storage.DeleteAsync(image.StorageKey, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ServeAsync(string slug, Guid imageId, HttpContext httpContext, LmsDbContext db, ITenantContext tenantContext, IContentAssetStorage storage, CancellationToken cancellationToken)
    {
        var normalized = TenantSlug.Normalize(slug);
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(item => item.Slug == normalized && item.Status == TenantStatus.Active, cancellationToken);
        if (tenant is null) return Results.NotFound();
        tenantContext.Set(tenant.Id, tenant.Slug);
        var image = await db.LandingImages.AsNoTracking().SingleOrDefaultAsync(item => item.Id == imageId, cancellationToken);
        if (image is null) return Results.NotFound();
        var stream = await storage.OpenReadAsync(image.StorageKey, cancellationToken);
        if (stream is null) return Results.NotFound();
        httpContext.Response.Headers.CacheControl = "public, max-age=3600";
        httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
        httpContext.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";   // even if someone opens the picture's own address, nothing in it can run
        return Results.File(stream, image.ContentType);
    }
}

public sealed record ImageView(Guid Id, string FileName, long SizeBytes);
