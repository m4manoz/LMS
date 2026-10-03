using System.Text;
using Lms.Api.Domain.Assessments;
using Lms.Api.Domain.Identity;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Reports;

public static class ReportEndpoints
{
    public static void MapReportEndpoints(this WebApplication app)
    {
        var tenant = app.MapGroup("/api/v1/tenant/reports").RequireAuthorization("tenant.report.read");
        tenant.AddEndpointFilter(async (context, next) =>
        {
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (!tenantContext.IsResolved) return Results.BadRequest(new { message = "A tenant is required. Use the X-Tenant-Slug header or a tenant subdomain." });
            return await next(context);
        });
        tenant.MapGet("/overview", GetOverviewAsync);
        tenant.MapGet("/course-progress", GetCourseProgressAsync);

        var exports = app.MapGroup("/api/v1/tenant/reports").RequireAuthorization("tenant.report.export");
        exports.MapGet("/enrollments.csv", ExportEnrollmentsAsync);
        exports.MapGet("/grades.csv", ExportGradesAsync);
    }

    private static async Task<IResult> GetOverviewAsync(LmsDbContext db, CancellationToken cancellationToken)
    {
        var enrollmentCount = await db.Enrollments.CountAsync(cancellationToken);
        var completedCount = await db.Enrollments.CountAsync(item => item.Status == Domain.Learning.EnrollmentStatus.Completed, cancellationToken);
        var activeCourseCount = await db.Courses.CountAsync(item => item.Status == Domain.Courses.CourseStatus.Published, cancellationToken);
        var certificateCount = await db.Certificates.CountAsync(cancellationToken);
        var gradedPercentages = await db.AssessmentAttempts.Where(item => item.Status == AttemptStatus.Graded && item.Percentage != null).Select(item => item.Percentage!.Value).ToListAsync(cancellationToken);
        var averageGrade = gradedPercentages.Count == 0 ? 0 : Math.Round(gradedPercentages.Average(), 2);
        var averageProgress = await db.Enrollments.Select(item => (decimal)item.ProgressPercent).ToListAsync(cancellationToken);
        return Results.Ok(new ReportsOverviewResponse(activeCourseCount, enrollmentCount, completedCount, enrollmentCount == 0 ? 0 : Math.Round(completedCount * 100m / enrollmentCount, 2), averageProgress.Count == 0 ? 0 : Math.Round(averageProgress.Average(), 2), gradedPercentages.Count, averageGrade, certificateCount));
    }

    private static async Task<IResult> GetCourseProgressAsync(LmsDbContext db, Guid? courseId, CancellationToken cancellationToken)
    {
        var query = db.Enrollments.AsNoTracking();
        if (courseId is Guid selectedCourseId) query = query.Where(item => item.CourseId == selectedCourseId);
        var enrollments = await query.OrderByDescending(item => item.UpdatedAtUtc).ToListAsync(cancellationToken);
        var courses = await db.Courses.AsNoTracking().Where(item => enrollments.Select(enrollment => enrollment.CourseId).Contains(item.Id)).ToDictionaryAsync(item => item.Id, cancellationToken);
        var users = await db.Users.AsNoTracking().Where(item => enrollments.Select(enrollment => enrollment.LearnerUserId).Contains(item.Id)).ToDictionaryAsync(item => item.Id, cancellationToken);
        return Results.Ok(enrollments.Where(item => courses.ContainsKey(item.CourseId) && users.ContainsKey(item.LearnerUserId)).Select(item => new CourseProgressReportResponse(item.CourseId, courses[item.CourseId].Code, courses[item.CourseId].Title, item.LearnerUserId, users[item.LearnerUserId].DisplayName, item.Status.ToString(), item.ProgressPercent, item.EnrolledAtUtc, item.CompletedAtUtc)).ToArray());
    }

    private static async Task<IResult> ExportEnrollmentsAsync(LmsDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.Enrollments.AsNoTracking().OrderBy(item => item.EnrolledAtUtc).ToListAsync(cancellationToken);
        var courses = await db.Courses.AsNoTracking().Where(item => rows.Select(row => row.CourseId).Contains(item.Id)).ToDictionaryAsync(item => item.Id, cancellationToken);
        var users = await db.Users.AsNoTracking().Where(item => rows.Select(row => row.LearnerUserId).Contains(item.Id)).ToDictionaryAsync(item => item.Id, cancellationToken);
        var csv = new StringBuilder("CourseCode,CourseTitle,Learner,Status,ProgressPercent,EnrolledAtUtc,CompletedAtUtc\n");
        foreach (var row in rows.Where(item => courses.ContainsKey(item.CourseId) && users.ContainsKey(item.LearnerUserId))) csv.AppendLine(string.Join(',', Csv(courses[row.CourseId].Code), Csv(courses[row.CourseId].Title), Csv(users[row.LearnerUserId].DisplayName), Csv(row.Status.ToString()), row.ProgressPercent, Csv(row.EnrolledAtUtc.ToString("O")), Csv(row.CompletedAtUtc?.ToString("O"))));
        return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", "lms-enrollments.csv");
    }

    private static async Task<IResult> ExportGradesAsync(LmsDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.AssessmentAttempts.AsNoTracking().OrderBy(item => item.StartedAtUtc).ToListAsync(cancellationToken);
        var assessments = await db.Assessments.AsNoTracking().Where(item => rows.Select(row => row.AssessmentId).Contains(item.Id)).ToDictionaryAsync(item => item.Id, cancellationToken);
        var users = await db.Users.AsNoTracking().Where(item => rows.Select(row => row.LearnerUserId).Contains(item.Id)).ToDictionaryAsync(item => item.Id, cancellationToken);
        var csv = new StringBuilder("Assessment,Learner,AttemptNumber,Status,ScorePoints,PossiblePoints,Percentage,StartedAtUtc,SubmittedAtUtc\n");
        foreach (var row in rows.Where(item => assessments.ContainsKey(item.AssessmentId) && users.ContainsKey(item.LearnerUserId))) csv.AppendLine(string.Join(',', Csv(assessments[row.AssessmentId].Title), Csv(users[row.LearnerUserId].DisplayName), row.AttemptNumber, Csv(row.Status.ToString()), row.ScorePoints, row.PossiblePoints, row.Percentage?.ToString() ?? string.Empty, Csv(row.StartedAtUtc.ToString("O")), Csv(row.SubmittedAtUtc?.ToString("O"))));
        return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", "lms-grades.csv");
    }

    private static string Csv(string? value)
    {
        var text = value ?? string.Empty;
        return $"\"{text.Replace("\"", "\"\"")}\"";
    }
}

public sealed record ReportsOverviewResponse(int PublishedCourseCount, int EnrollmentCount, int CompletedEnrollmentCount, decimal CompletionRatePercent, decimal AverageProgressPercent, int GradedAttemptCount, decimal AverageGradePercent, int CertificateCount);
public sealed record CourseProgressReportResponse(Guid CourseId, string CourseCode, string CourseTitle, Guid LearnerUserId, string LearnerName, string Status, int ProgressPercent, DateTimeOffset EnrolledAtUtc, DateTimeOffset? CompletedAtUtc);
