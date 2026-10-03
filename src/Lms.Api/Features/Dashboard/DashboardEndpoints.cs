using System.Security.Claims;
using Lms.Api.Domain.Assessments;
using Lms.Api.Domain.Courses;
using Lms.Api.Domain.Identity;
using Lms.Api.Domain.Learning;
using Lms.Api.Domain.LiveClasses;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Dashboard;

public static class DashboardEndpoints
{
    public static void MapDashboardEndpoints(this WebApplication app)
    {
        var tenant = app.MapGroup("/api/v1/tenant/dashboard").RequireAuthorization("tenant.authenticated");
        tenant.AddEndpointFilter(async (context, next) =>
        {
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (!tenantContext.IsResolved) return Results.BadRequest(new { message = "A tenant is required. Use the X-Tenant-Slug header or a tenant subdomain." });
            return await next(context);
        });
        tenant.MapGet("/", GetDashboardAsync);
    }

    private static async Task<IResult> GetDashboardAsync(HttpContext httpContext, LmsDbContext db, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? httpContext.User.FindFirstValue("sub"), out var userId))
            return Results.Unauthorized();

        var now = DateTimeOffset.UtcNow;
        var learning = new LearningSummary(
            await db.Enrollments.CountAsync(item => item.LearnerUserId == userId && item.Status == EnrollmentStatus.Active, cancellationToken),
            await db.Enrollments.CountAsync(item => item.LearnerUserId == userId && item.Status == EnrollmentStatus.Completed, cancellationToken));

        var upcomingSessions = HasPermission(httpContext, LmsPermissions.LiveClassRead)
            ? await db.LiveClassSessions.AsNoTracking()
                .Where(item => item.Status != LiveSessionStatus.Cancelled && item.Status != LiveSessionStatus.Completed && item.EndAtUtc >= now)
                .OrderBy(item => item.StartAtUtc).Take(5)
                .Select(item => new UpcomingSession(item.Id, item.Title, item.StartAtUtc, item.EndAtUtc, item.Status.ToString()))
                .ToListAsync(cancellationToken)
            : [];

        TeachingSummary? teaching = HasPermission(httpContext, LmsPermissions.AssessmentManage)
            ? new TeachingSummary(await db.AssessmentAttempts.CountAsync(item => item.Status == AttemptStatus.Submitted, cancellationToken))
            : null;

        AdminSummary? admin = HasPermission(httpContext, LmsPermissions.ReportRead)
            ? new AdminSummary(
                await db.Enrollments.CountAsync(item => item.Status == EnrollmentStatus.Active, cancellationToken),
                await db.Courses.CountAsync(item => item.Status == CourseStatus.Published, cancellationToken))
            : null;

        return Results.Ok(new DashboardResponse(learning, upcomingSessions, teaching, admin));
    }

    private static bool HasPermission(HttpContext context, string permission)
        => context.User.Claims.Any(claim => claim.Type == "permission" && claim.Value.Equals(permission, StringComparison.OrdinalIgnoreCase));
}

public sealed record LearningSummary(int ActiveEnrollments, int CompletedEnrollments);
public sealed record UpcomingSession(Guid Id, string Title, DateTimeOffset StartAtUtc, DateTimeOffset EndAtUtc, string Status);
public sealed record TeachingSummary(int AttemptsToGrade);
public sealed record AdminSummary(int ActiveEnrollments, int PublishedCourses);
public sealed record DashboardResponse(LearningSummary Learning, IReadOnlyList<UpcomingSession> UpcomingSessions, TeachingSummary? Teaching, AdminSummary? Admin);
