using System.Security.Claims;
using Lms.Api.Domain.Assessments;
using Lms.Api.Domain.Assignments;
using Lms.Api.Domain.Identity;
using Lms.Api.Domain.Learning;
using Lms.Api.Domain.LiveClasses;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Tasks;

/// <summary>A single feed of dated and undated work for the signed-in user; powers My tasks and the Calendar.</summary>
public static class TaskEndpoints
{
    private const int DefaultPastDays = 30;
    private const int DefaultFutureDays = 90;

    public static void MapTaskEndpoints(this WebApplication app)
    {
        var tenant = app.MapGroup("/api/v1/tenant/tasks").RequireAuthorization("tenant.authenticated");
        tenant.AddEndpointFilter(async (context, next) =>
        {
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (!tenantContext.IsResolved)
                return Results.BadRequest(new { message = "A tenant is required. Use the X-Tenant-Slug header or a tenant subdomain." });
            return await next(context);
        });
        tenant.MapGet("", ListAsync);
    }

    private static async Task<IResult> ListAsync(HttpContext httpContext, LmsDbContext db, DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var now = DateTimeOffset.UtcNow;
        var windowStart = from ?? now.AddDays(-DefaultPastDays);
        var windowEnd = to ?? now.AddDays(DefaultFutureDays);
        if (windowEnd < windowStart) return Results.BadRequest(new { message = "The end of the range must be after its start." });
        if (windowEnd - windowStart > TimeSpan.FromDays(400)) return Results.BadRequest(new { message = "The range cannot be longer than 400 days." });

        var courseTitles = await db.Courses.AsNoTracking().ToDictionaryAsync(item => item.Id, item => item.Title, cancellationToken);
        var items = new List<TaskItem>();

        var enrolledCourseIds = await db.Enrollments.AsNoTracking()
            .Where(item => item.LearnerUserId == userId && (item.Status == EnrollmentStatus.Active || item.Status == EnrollmentStatus.Completed))
            .Select(item => item.CourseId).ToListAsync(cancellationToken);

        // ---- the user's own assignments ----
        var assignments = await db.Assignments.AsNoTracking()
            .Where(item => enrolledCourseIds.Contains(item.CourseId) && item.Status != AssignmentStatus.Draft).ToListAsync(cancellationToken);
        var assignmentIds = assignments.Select(item => item.Id).ToList();
        var mine = await db.AssignmentSubmissions.AsNoTracking().Where(item => assignmentIds.Contains(item.AssignmentId) && item.LearnerUserId == userId)
            .ToDictionaryAsync(item => item.AssignmentId, cancellationToken);
        foreach (var assignment in assignments)
        {
            mine.TryGetValue(assignment.Id, out var submission);
            string status;
            if (submission is { Status: SubmissionStatus.Graded }) status = "Graded";
            else if (submission is not null) status = "Submitted";
            else if (assignment.Status == AssignmentStatus.Closed) continue; // missed and closed: nothing left to do
            else status = assignment.DueAtUtc is DateTimeOffset due && due < now ? "Overdue" : "Todo";

            // Open or overdue work is always listed; completed work only when it falls inside the window.
            var open = status is "Todo" or "Overdue";
            if (!open && !InWindow(assignment.DueAtUtc ?? submission?.SubmittedAtUtc, windowStart, windowEnd)) continue;
            if (open && assignment.DueAtUtc is DateTimeOffset dueAt && status == "Todo" && dueAt > windowEnd) continue;
            items.Add(new TaskItem($"assignment:{assignment.Id}", "assignment", assignment.Title, courseTitles.GetValueOrDefault(assignment.CourseId), null,
                assignment.DueAtUtc, status, "assignments", null));
        }

        // ---- the user's own assessments (no deadlines, so they are undated to-dos) ----
        var assessments = await db.Assessments.AsNoTracking()
            .Where(item => enrolledCourseIds.Contains(item.CourseId) && item.Status == AssessmentStatus.Published).ToListAsync(cancellationToken);
        var assessmentIds = assessments.Select(item => item.Id).ToList();
        var attempts = await db.AssessmentAttempts.AsNoTracking().Where(item => assessmentIds.Contains(item.AssessmentId) && item.LearnerUserId == userId)
            .GroupBy(item => item.AssessmentId)
            .Select(group => new { group.Key, Done = group.Any(a => a.Status != AttemptStatus.InProgress), Started = group.Any() })
            .ToDictionaryAsync(item => item.Key, cancellationToken);
        foreach (var assessment in assessments)
        {
            var status = attempts.TryGetValue(assessment.Id, out var attempt) ? (attempt.Done ? "Done" : "InProgress") : "Todo";
            items.Add(new TaskItem($"assessment:{assessment.Id}", "assessment", assessment.Title, courseTitles.GetValueOrDefault(assessment.CourseId), null, null, status, "quizzes", null));
        }

        // ---- upcoming live classes ----
        if (HasPermission(httpContext, LmsPermissions.LiveClassRead))
        {
            var sessions = await db.LiveClassSessions.AsNoTracking()
                .Where(item => item.Status != LiveSessionStatus.Cancelled && item.Status != LiveSessionStatus.Completed && item.EndAtUtc >= windowStart && item.StartAtUtc <= windowEnd)
                .OrderBy(item => item.StartAtUtc).Take(200).ToListAsync(cancellationToken);
            items.AddRange(sessions.Select(item => new TaskItem($"live:{item.Id}", "live-class", item.Title,
                item.CourseId is Guid courseId ? courseTitles.GetValueOrDefault(courseId) : null, item.StartAtUtc, item.EndAtUtc,
                item.Status == LiveSessionStatus.Live ? "Live" : item.EndAtUtc < now ? "Ended" : "Scheduled", "live", null))); // a class whose time has passed is never a to-do, even if nobody marked it completed
        }

        // ---- grading work for teachers ----
        if (HasPermission(httpContext, LmsPermissions.GradeManage))
        {
            var pending = await db.AssignmentSubmissions.AsNoTracking().Where(item => item.Status == SubmissionStatus.Submitted)
                .GroupBy(item => item.AssignmentId).Select(group => new { group.Key, Count = group.Count() }).ToListAsync(cancellationToken);
            var pendingIds = pending.Select(item => item.Key).ToList();
            var toGrade = await db.Assignments.AsNoTracking().Where(item => pendingIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, cancellationToken);
            foreach (var group in pending.Where(item => toGrade.ContainsKey(item.Key)))
            {
                var assignment = toGrade[group.Key];
                items.Add(new TaskItem($"grading:{assignment.Id}", "grading", assignment.Title, courseTitles.GetValueOrDefault(assignment.CourseId), null,
                    assignment.DueAtUtc, "ToGrade", "assignments", group.Count));
            }
        }

        return Results.Ok(new TaskFeed(windowStart, windowEnd, items
            .OrderBy(item => item.Status == "Overdue" ? 0 : 1)
            .ThenBy(item => item.StartAtUtc ?? item.DueAtUtc ?? DateTimeOffset.MaxValue)
            .ThenBy(item => item.Title).ToList()));
    }

    private static bool InWindow(DateTimeOffset? value, DateTimeOffset start, DateTimeOffset end) => value is DateTimeOffset date && date >= start && date <= end;

    private static Guid? GetUserId(HttpContext context)
        => Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub"), out var id) ? id : null;

    private static bool HasPermission(HttpContext context, string permission)
        => context.User.Claims.Any(claim => claim.Type == "permission" && claim.Value.Equals(permission, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Kind: assignment | assessment | live-class | grading. Target is the menu id that opens the item.</summary>
public sealed record TaskItem(string Id, string Kind, string Title, string? CourseTitle, DateTimeOffset? StartAtUtc, DateTimeOffset? DueAtUtc, string Status, string Target, int? Count);
public sealed record TaskFeed(DateTimeOffset FromUtc, DateTimeOffset ToUtc, IReadOnlyList<TaskItem> Items);
