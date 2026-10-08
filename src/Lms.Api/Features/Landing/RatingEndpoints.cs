using System.Security.Claims;
using Lms.Api.Domain.Courses;
using Lms.Api.Domain.Learning;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Security;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Landing;

/// <summary>
/// Course ratings. A learner who has started a course rates it from 1 to 5 stars with an optional review and can change or withdraw it.
/// Averages and reviews appear on the public page; staff can hide an abusive rating. Nobody can rate a course they are not taking.
/// </summary>
public static class RatingEndpoints
{
    private const int MaxReviewLength = 1000;

    public static void MapRatingEndpoints(this WebApplication app)
    {
        var tenant = app.MapGroup("/api/v1/tenant").RequireAuthorization("tenant.authenticated");
        tenant.AddEndpointFilter(async (context, next) =>
            context.HttpContext.RequestServices.GetRequiredService<ITenantContext>().IsResolved
                ? await next(context)
                : Results.BadRequest(new { message = "A tenant is required. Use the X-Tenant-Slug header or a tenant subdomain." }));
        tenant.MapGet("/courses/{courseId:guid}/rating", MineAsync).RequireAuthorization("tenant.enrollment.read");
        tenant.MapPut("/courses/{courseId:guid}/rating", SaveAsync).RequireAuthorization("tenant.enrollment.read");
        tenant.MapDelete("/courses/{courseId:guid}/rating", WithdrawAsync).RequireAuthorization("tenant.enrollment.read");
        tenant.MapGet("/courses/{courseId:guid}/ratings", ListAsync).RequireAuthorization("tenant.course.manage");
        tenant.MapPost("/ratings/{ratingId:guid}/hide", (Guid ratingId, HttpContext http, LmsDbContext db, SecurityAuditService audit, ITenantContext tenantContext, CancellationToken ct) => SetHiddenAsync(ratingId, true, http, db, audit, tenantContext, ct)).RequireAuthorization("tenant.course.manage");
        tenant.MapPost("/ratings/{ratingId:guid}/show", (Guid ratingId, HttpContext http, LmsDbContext db, SecurityAuditService audit, ITenantContext tenantContext, CancellationToken ct) => SetHiddenAsync(ratingId, false, http, db, audit, tenantContext, ct)).RequireAuthorization("tenant.course.manage");
    }

    private static Guid? GetUserId(HttpContext context) => Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private static async Task<IResult> MineAsync(Guid courseId, HttpContext httpContext, LmsDbContext db, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var rating = await db.CourseRatings.AsNoTracking().SingleOrDefaultAsync(item => item.CourseId == courseId && item.LearnerUserId == userId, cancellationToken);
        var eligible = await EligibilityAsync(db, courseId, userId, cancellationToken);
        return Results.Ok(new MyRating(rating is null ? null : new RatingView(rating.Stars, rating.Review, rating.IsHidden, rating.UpdatedAtUtc), eligible));
    }

    /// <summary>Null when this learner may rate the course; otherwise why not.</summary>
    private static async Task<string?> EligibilityAsync(LmsDbContext db, Guid courseId, Guid userId, CancellationToken cancellationToken)
    {
        var enrollment = await db.Enrollments.AsNoTracking().Where(item => item.CourseId == courseId && item.LearnerUserId == userId
            && (item.Status == EnrollmentStatus.Active || item.Status == EnrollmentStatus.Completed)).OrderByDescending(item => item.ProgressPercent).FirstOrDefaultAsync(cancellationToken);
        if (enrollment is null) return "Only people taking this course can rate it.";
        if (enrollment.Status != EnrollmentStatus.Completed && enrollment.ProgressPercent < 1) return "Start the course before you rate it.";
        return null;
    }

    private static async Task<IResult> SaveAsync(Guid courseId, HttpContext httpContext, ITenantContext tenantContext, LmsDbContext db, SaveRatingRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        if (request.Stars is < 1 or > 5) return Results.BadRequest(new { message = "Choose from 1 to 5 stars." });
        var review = string.IsNullOrWhiteSpace(request.Review) ? null : request.Review.Trim();
        if (review is { Length: > MaxReviewLength }) return Results.BadRequest(new { message = $"A review can have at most {MaxReviewLength} characters." });
        if (!await db.Courses.AnyAsync(item => item.Id == courseId && item.Status != CourseStatus.Draft, cancellationToken)) return Results.NotFound(new { message = "That course was not found." });
        if (await EligibilityAsync(db, courseId, userId, cancellationToken) is { } reason) return Results.Conflict(new { message = reason });

        var now = DateTimeOffset.UtcNow;
        var rating = await db.CourseRatings.SingleOrDefaultAsync(item => item.CourseId == courseId && item.LearnerUserId == userId, cancellationToken);
        if (rating is null) db.CourseRatings.Add(rating = new CourseRating { Id = Guid.NewGuid(), TenantId = tenantId, CourseId = courseId, LearnerUserId = userId, CreatedAtUtc = now });
        rating.Stars = request.Stars; rating.Review = review; rating.UpdatedAtUtc = now;   // a rating staff hid stays hidden when its author edits it
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new MyRating(new RatingView(rating.Stars, rating.Review, rating.IsHidden, rating.UpdatedAtUtc), null));
    }

    private static async Task<IResult> WithdrawAsync(Guid courseId, HttpContext httpContext, LmsDbContext db, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var rating = await db.CourseRatings.SingleOrDefaultAsync(item => item.CourseId == courseId && item.LearnerUserId == userId, cancellationToken);
        if (rating is null) return Results.NoContent();
        db.CourseRatings.Remove(rating);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ListAsync(Guid courseId, LmsDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.CourseRatings.AsNoTracking().Where(item => item.CourseId == courseId).OrderByDescending(item => item.UpdatedAtUtc).Take(500).ToListAsync(cancellationToken);
        var ids = rows.Select(item => item.LearnerUserId).ToList();
        var names = await db.Users.AsNoTracking().Where(item => ids.Contains(item.Id)).ToDictionaryAsync(item => item.Id, item => item.DisplayName, cancellationToken);
        var summary = Summarise(rows.Where(item => !item.IsHidden));
        return Results.Ok(new StaffRatings(summary, rows.Select(item => new StaffRating(item.Id, names.GetValueOrDefault(item.LearnerUserId, "Unknown"), item.Stars, item.Review, item.IsHidden, item.CreatedAtUtc, item.UpdatedAtUtc)).ToList()));
    }

    private static async Task<IResult> SetHiddenAsync(Guid ratingId, bool hidden, HttpContext httpContext, LmsDbContext db, SecurityAuditService audit, ITenantContext tenantContext, CancellationToken cancellationToken)
    {
        var rating = await db.CourseRatings.SingleOrDefaultAsync(item => item.Id == ratingId, cancellationToken);
        if (rating is null || tenantContext.TenantId is not Guid tenantId) return Results.NotFound();
        if (rating.IsHidden != hidden)
        {
            rating.IsHidden = hidden;
            audit.Add(db, httpContext, tenantId, hidden ? "rating.hidden" : "rating.shown", "course", rating.CourseId, new { ratingId });
            await db.SaveChangesAsync(cancellationToken);
        }
        return Results.NoContent();
    }

    // ---------- used by the public page ----------
    public static RatingSummary Summarise(IEnumerable<CourseRating> ratings)
    {
        var list = ratings.ToList();
        var distribution = Enumerable.Range(1, 5).Select(star => list.Count(item => item.Stars == star)).ToArray();
        return new RatingSummary(list.Count == 0 ? null : Math.Round(list.Average(item => item.Stars), 1), list.Count, distribution);
    }

    /// <summary>The visible ratings of each course.</summary>
    public static async Task<Dictionary<Guid, RatingSummary>> SummariesAsync(LmsDbContext db, IReadOnlyCollection<Guid> courseIds, CancellationToken cancellationToken)
    {
        var rows = await db.CourseRatings.AsNoTracking().Where(item => courseIds.Contains(item.CourseId) && !item.IsHidden).Select(item => new { item.CourseId, item.Stars }).ToListAsync(cancellationToken);
        return rows.GroupBy(item => item.CourseId).ToDictionary(group => group.Key, group => new RatingSummary(Math.Round(group.Average(item => item.Stars), 1), group.Count(),
            Enumerable.Range(1, 5).Select(star => group.Count(item => item.Stars == star)).ToArray()));
    }

    /// <summary>The newest written reviews, each shown with a first name and initial only ("Lena K."), never an email.</summary>
    public static async Task<List<PublicReview>> ReviewsAsync(LmsDbContext db, Guid courseId, int take, CancellationToken cancellationToken)
    {
        var rows = await db.CourseRatings.AsNoTracking().Where(item => item.CourseId == courseId && !item.IsHidden && item.Review != null).OrderByDescending(item => item.UpdatedAtUtc).Take(take).ToListAsync(cancellationToken);
        var ids = rows.Select(item => item.LearnerUserId).ToList();
        var names = await db.Users.AsNoTracking().Where(item => ids.Contains(item.Id)).ToDictionaryAsync(item => item.Id, item => item.DisplayName, cancellationToken);
        return rows.Select(item => new PublicReview(item.Stars, item.Review!, PublicName(names.GetValueOrDefault(item.LearnerUserId)), item.UpdatedAtUtc)).ToList();
    }

    public static string PublicName(string? displayName)
    {
        var parts = (displayName ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return "A learner";
        return parts.Length == 1 ? parts[0] : $"{parts[0]} {char.ToUpperInvariant(parts[^1][0])}.";
    }
}

public sealed record SaveRatingRequest(int Stars, string? Review);
public sealed record RatingView(int Stars, string? Review, bool IsHidden, DateTimeOffset UpdatedAtUtc);
/// <summary>The learner's own rating (if any) and, when they cannot rate right now, why.</summary>
public sealed record MyRating(RatingView? Rating, string? CannotRateBecause);
public sealed record RatingSummary(double? Average, int Count, int[] Distribution);
public sealed record PublicReview(int Stars, string Text, string Author, DateTimeOffset AtUtc);
public sealed record StaffRating(Guid Id, string LearnerName, int Stars, string? Review, bool IsHidden, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record StaffRatings(RatingSummary Summary, IReadOnlyList<StaffRating> Ratings);
