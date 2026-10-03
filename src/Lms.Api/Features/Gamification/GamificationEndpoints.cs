using System.Security.Claims;
using Lms.Api.Domain.Gamification;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Gamification;

public static class GamificationEndpoints
{
    public static void MapGamificationEndpoints(this WebApplication app)
    {
        var tenant = app.MapGroup("/api/v1/tenant/gamification").RequireAuthorization("tenant.gamification.read");
        tenant.AddEndpointFilter(async (context, next) =>
        {
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            return tenantContext.IsResolved ? await next(context) : Results.BadRequest(new { message = "A tenant is required." });
        });
        tenant.MapGet("/me", GetProfileAsync);
        tenant.MapGet("/leaderboard", GetLeaderboardAsync);
        tenant.MapGet("/badges", GetBadgesAsync);
        tenant.MapGet("/settings", GetSettingsAsync).RequireAuthorization("tenant.gamification.manage");
        tenant.MapPut("/settings", UpdateSettingsAsync).RequireAuthorization("tenant.gamification.manage");
        tenant.MapPut("/badges/{code}", UpdateBadgeAsync).RequireAuthorization("tenant.gamification.manage");
        tenant.MapGet("/reviews", ListReviewsAsync).RequireAuthorization("tenant.gamification.manage");
        tenant.MapPost("/reviews/{reviewId:guid}/resolve", ResolveReviewAsync).RequireAuthorization("tenant.gamification.manage");
    }

    private static async Task<IResult> GetProfileAsync(HttpContext httpContext, LmsDbContext db, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var profile = await db.GamificationProfiles.AsNoTracking().SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        var badges = await db.UserBadges.AsNoTracking().Include(item => item.BadgeDefinition).Where(item => item.UserId == userId).OrderByDescending(item => item.AwardedAtUtc).Select(item => new BadgeResponse(item.BadgeDefinition.Code, item.BadgeDefinition.Name, item.BadgeDefinition.Description, item.AwardedAtUtc)).ToArrayAsync(cancellationToken);
        var events = await db.GamificationEvents.AsNoTracking().Where(item => item.UserId == userId).OrderByDescending(item => item.OccurredAtUtc).Take(10).Select(item => new GamificationEventResponse(item.Type.ToString(), item.Points, item.Description, item.OccurredAtUtc)).ToArrayAsync(cancellationToken);
        return Results.Ok(new GamificationProfileResponse(profile?.TotalPoints ?? 0, profile?.CurrentStreakDays ?? 0, profile?.LongestStreakDays ?? 0, profile?.LastActivityDateAd, badges, events));
    }

    private static async Task<IResult> GetLeaderboardAsync(LmsDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.GamificationProfiles.AsNoTracking().OrderByDescending(item => item.TotalPoints).ThenBy(item => item.UserId).Take(25).ToListAsync(cancellationToken);
        var userIds = rows.Select(item => item.UserId).ToArray();
        var users = await db.Users.AsNoTracking().Where(item => userIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, cancellationToken);
        return Results.Ok(rows.Select((item, index) => new LeaderboardEntryResponse(index + 1, item.UserId, users.TryGetValue(item.UserId, out var user) ? user.DisplayName : "Learner", item.TotalPoints, item.CurrentStreakDays)).ToArray());
    }

    private static async Task<IResult> GetBadgesAsync(LmsDbContext db, CancellationToken cancellationToken)
    {
        var badges = await db.BadgeDefinitions.AsNoTracking().Where(item => item.IsActive).OrderBy(item => item.Name).Select(item => new BadgeDefinitionResponse(item.Code, item.Name, item.Description, item.PointsThreshold, item.StreakDays)).ToArrayAsync(cancellationToken);
        return Results.Ok(badges);
    }

    private static async Task<IResult> GetSettingsAsync(LmsDbContext db, CancellationToken cancellationToken)
    {
        var settings = await db.GamificationSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return Results.Ok(settings is null
            ? new GamificationSettingsResponse(true, 100, 500, 10, null)
            : ToSettingsResponse(settings));
    }

    private static async Task<IResult> UpdateSettingsAsync(LmsDbContext db, ITenantContext tenantContext, UpdateGamificationSettingsRequest request, CancellationToken cancellationToken)
    {
        if (request.CourseCompletionPoints is < 0 or > 10000) return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.CourseCompletionPoints)] = ["Course completion points must be between 0 and 10,000."] });
        if (request.DailyPointCap is < 1 or > 100000) return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.DailyPointCap)] = ["Daily point cap must be between 1 and 100,000."] });
        if (request.MaxAwardsPerHour is < 1 or > 1000) return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.MaxAwardsPerHour)] = ["The hourly award limit must be between 1 and 1,000."] });
        var settings = await db.GamificationSettings.SingleOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            if (tenantContext.TenantId is not Guid tenantId) return Results.BadRequest(new { message = "A tenant is required." });
            settings = new GamificationSettings { Id = Guid.NewGuid(), TenantId = tenantId };
            db.GamificationSettings.Add(settings);
        }
        settings.IsEnabled = request.IsEnabled; settings.CourseCompletionPoints = request.CourseCompletionPoints; settings.DailyPointCap = request.DailyPointCap; settings.MaxAwardsPerHour = request.MaxAwardsPerHour; settings.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToSettingsResponse(settings));
    }

    private static async Task<IResult> UpdateBadgeAsync(LmsDbContext db, string code, UpdateBadgeRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 150) return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Name)] = ["Badge name is required and must be at most 150 characters."] });
        if (request.PointsThreshold is < 0 or > 100000 || request.StreakDays is < 1 or > 3650) return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.PointsThreshold)] = ["Badge thresholds are outside the supported range."] });
        var badge = await db.BadgeDefinitions.SingleOrDefaultAsync(item => item.Code == code.Trim().ToUpperInvariant(), cancellationToken);
        if (badge is null) return Results.NotFound();
        badge.Name = request.Name.Trim(); badge.Description = request.Description?.Trim() ?? badge.Description; badge.PointsThreshold = request.PointsThreshold; badge.StreakDays = request.StreakDays; badge.IsActive = request.IsActive;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new BadgeDefinitionResponse(badge.Code, badge.Name, badge.Description, badge.PointsThreshold, badge.StreakDays));
    }

    private static async Task<IResult> ListReviewsAsync(LmsDbContext db, CancellationToken cancellationToken)
    {
        var reviews = await db.GamificationAbuseReviews.AsNoTracking().Where(item => item.Status == "Open").OrderByDescending(item => item.CreatedAtUtc).Take(100).ToArrayAsync(cancellationToken);
        return Results.Ok(reviews.Select(item => new GamificationAbuseReviewResponse(item.Id, item.UserId, item.Signal, item.EventCount, item.Status, item.CreatedAtUtc, item.ResolvedAtUtc)).ToArray());
    }

    private static async Task<IResult> ResolveReviewAsync(LmsDbContext db, Guid reviewId, CancellationToken cancellationToken)
    {
        var review = await db.GamificationAbuseReviews.SingleOrDefaultAsync(item => item.Id == reviewId, cancellationToken);
        if (review is null) return Results.NotFound();
        review.Status = "Resolved";
        review.ResolvedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new GamificationAbuseReviewResponse(review.Id, review.UserId, review.Signal, review.EventCount, review.Status, review.CreatedAtUtc, review.ResolvedAtUtc));
    }

    private static GamificationSettingsResponse ToSettingsResponse(GamificationSettings settings) => new(settings.IsEnabled, settings.CourseCompletionPoints, settings.DailyPointCap, settings.MaxAwardsPerHour, settings.UpdatedAtUtc);

    private static Guid? GetUserId(HttpContext httpContext) => Guid.TryParse(httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}

public sealed record GamificationProfileResponse(int TotalPoints, int CurrentStreakDays, int LongestStreakDays, DateOnly? LastActivityDateAd, BadgeResponse[] Badges, GamificationEventResponse[] RecentEvents);
public sealed record BadgeResponse(string Code, string Name, string Description, DateTimeOffset AwardedAtUtc);
public sealed record BadgeDefinitionResponse(string Code, string Name, string Description, int? PointsThreshold, int? StreakDays);
public sealed record GamificationEventResponse(string Type, int Points, string Description, DateTimeOffset OccurredAtUtc);
public sealed record LeaderboardEntryResponse(int Rank, Guid UserId, string DisplayName, int TotalPoints, int CurrentStreakDays);
public sealed record GamificationSettingsResponse(bool IsEnabled, int CourseCompletionPoints, int DailyPointCap, int MaxAwardsPerHour, DateTimeOffset? UpdatedAtUtc);
public sealed record UpdateGamificationSettingsRequest(bool IsEnabled, int CourseCompletionPoints, int DailyPointCap, int MaxAwardsPerHour = 10);
public sealed record UpdateBadgeRequest(string Name, string? Description, int? PointsThreshold, int? StreakDays, bool IsActive);
public sealed record GamificationAbuseReviewResponse(Guid Id, Guid UserId, string Signal, int EventCount, string Status, DateTimeOffset CreatedAtUtc, DateTimeOffset? ResolvedAtUtc);
