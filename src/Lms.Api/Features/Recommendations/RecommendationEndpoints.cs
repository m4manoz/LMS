using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Lms.Api.Domain.Assessments;
using Lms.Api.Domain.Courses;
using Lms.Api.Domain.Identity;
using Lms.Api.Domain.Learning;
using Lms.Api.Domain.Recommendations;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Recommendations;

public static class RecommendationEndpoints
{
    public static void MapRecommendationEndpoints(this WebApplication app)
    {
        var tenant = app.MapGroup("/api/v1/tenant/learning").RequireAuthorization("tenant.recommendation.read");
        tenant.AddEndpointFilter(async (context, next) =>
        {
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            return tenantContext.IsResolved ? await next(context) : Results.BadRequest(new { message = "A tenant is required." });
        });
        tenant.MapGet("/recommendations", GetRecommendationsAsync);
        tenant.MapPost("/recommendations/{courseId:guid}/dismiss", DismissAsync);
    }

    private static async Task<IResult> GetRecommendationsAsync(HttpContext httpContext, LmsDbContext db, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid learnerUserId) return Results.Unauthorized();
        var requestedUserId = Guid.TryParse(httpContext.Request.Query["learnerUserId"], out var requested) ? requested : learnerUserId;
        if (requestedUserId != learnerUserId && !httpContext.User.HasClaim("permission", LmsPermissions.LearnerRead)) return Results.Forbid();

        var enrollments = await db.Enrollments.AsNoTracking()
            .Where(item => item.LearnerUserId == requestedUserId && (item.Status == EnrollmentStatus.Active || item.Status == EnrollmentStatus.Completed))
            .Select(item => new { item.CourseId, item.ProgressPercent })
            .ToListAsync(cancellationToken);
        var enrolledIds = enrollments.Select(item => item.CourseId).ToArray();
        var dismissedIds = await db.RecommendationDismissals.AsNoTracking().Where(item => item.LearnerUserId == requestedUserId).Select(item => item.CourseId).ToArrayAsync(cancellationToken);
        var assessmentEvidence = await db.AssessmentAttempts.AsNoTracking()
            .Where(item => item.LearnerUserId == requestedUserId && item.Status == AttemptStatus.Graded && item.Percentage != null)
            .GroupBy(item => item.CourseId)
            .Select(group => new { CourseId = group.Key, Average = group.Average(item => item.Percentage!.Value) })
            .ToDictionaryAsync(item => item.CourseId, item => item.Average, cancellationToken);
        var preferredCategoryIds = await db.Courses.AsNoTracking().Where(item => enrolledIds.Contains(item.Id) && item.CategoryId != null).Select(item => item.CategoryId!.Value).Distinct().ToArrayAsync(cancellationToken);
        var candidates = await db.Courses.AsNoTracking().Where(item => item.Status == CourseStatus.Published && !enrolledIds.Contains(item.Id) && !dismissedIds.Contains(item.Id)).OrderBy(item => item.Title).Take(100).ToListAsync(cancellationToken);
        var categoryIds = candidates.Where(item => item.CategoryId != null).Select(item => item.CategoryId!.Value).Distinct().ToArray();
        var categories = await db.CourseCategories.AsNoTracking().Where(item => categoryIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, cancellationToken);
        var results = candidates.Select(course =>
        {
            var categoryMatch = course.CategoryId is Guid categoryId && preferredCategoryIds.Contains(categoryId);
            var evidence = assessmentEvidence.GetValueOrDefault(course.Id);
            var variant = GetVariant(requestedUserId, course.Id);
            var score = categoryMatch ? 0.7m : course.CategoryId is null ? 0.45m : 0.55m;
            var reasons = new List<string>();
            if (categoryMatch) { score += 0.2m; reasons.Add("matches subjects in your current learning"); }
            if (evidence >= 80) { score += 0.08m; reasons.Add($"your assessment average is {evidence:0.#}%"); }
            else if (evidence > 0) reasons.Add($"your assessment average is {evidence:0.#}%");
            if (variant == "boosted") score += 0.01m;
            var reason = reasons.Count == 0 ? "a published course available in your tenant" : string.Join("; ", reasons);
            var explanation = evidence > 0 ? $"Assessment evidence: {evidence:0.#}% average." : categoryMatch ? "Subject match from active learning." : "Catalog availability signal.";
            return new RecommendationResponse(course.Id, course.Code, course.Title, course.Description, course.CategoryId is Guid id && categories.TryGetValue(id, out var category) ? category.Name : null, Math.Min(0.99m, score), reason, explanation, variant, DateTimeOffset.UtcNow);
        }).OrderByDescending(item => item.Score).ThenBy(item => item.Title).Take(10).ToArray();
        return Results.Ok(results);
    }

    private static async Task<IResult> DismissAsync(HttpContext httpContext, LmsDbContext db, Guid courseId, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid learnerUserId) return Results.Unauthorized();
        if (!await db.Courses.AnyAsync(item => item.Id == courseId && item.Status == CourseStatus.Published, cancellationToken)) return Results.NotFound();
        if (!Guid.TryParse(httpContext.User.FindFirstValue("tenant_id"), out var tenantId)) return Results.BadRequest(new { message = "A tenant is required." });
        var dismissal = await db.RecommendationDismissals.SingleOrDefaultAsync(item => item.LearnerUserId == learnerUserId && item.CourseId == courseId, cancellationToken);
        if (dismissal is null)
        {
            dismissal = new RecommendationDismissal { Id = Guid.NewGuid(), TenantId = tenantId, LearnerUserId = learnerUserId, CourseId = courseId, Variant = GetVariant(learnerUserId, courseId), DismissedAtUtc = DateTimeOffset.UtcNow };
            db.RecommendationDismissals.Add(dismissal);
        }
        else
        {
            dismissal.DismissedAtUtc = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static string GetVariant(Guid userId, Guid courseId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{userId:D}:{courseId:D}:recommendation-v1"));
        return (hash[0] & 1) == 0 ? "control" : "boosted";
    }

    private static Guid? GetUserId(HttpContext httpContext) => Guid.TryParse(httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}

public sealed record RecommendationResponse(Guid CourseId, string CourseCode, string Title, string? Description, string? CategoryName, decimal Score, string Reason, string Explanation, string Variant, DateTimeOffset GeneratedAtUtc);
