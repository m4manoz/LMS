using System.Security.Claims;
using Lms.Api.Domain.Assignments;
using Lms.Api.Domain.Identity;
using Lms.Api.Domain.Learning;
using Lms.Api.Infrastructure.Notifications;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Storage;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Assignments;

/// <summary>Assignments with text/file submissions, late policy and grading. Reuses the assessment and grade permissions.</summary>
public static class AssignmentEndpoints
{
    private const long MaximumSubmissionBytes = 25 * 1024 * 1024;
    private const int MaximumTextLength = 20000;

    public static void MapAssignmentEndpoints(this WebApplication app)
    {
        var tenant = app.MapGroup("/api/v1/tenant/assignments").RequireAuthorization("tenant.authenticated");
        tenant.AddEndpointFilter(async (context, next) =>
        {
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (!tenantContext.IsResolved)
                return Results.BadRequest(new { message = "A tenant is required. Use the X-Tenant-Slug header or a tenant subdomain." });
            return await next(context);
        });

        tenant.MapGet("", ListAsync).RequireAuthorization("tenant.assessment.read");
        tenant.MapPost("", CreateAsync).RequireAuthorization("tenant.assessment.manage");
        tenant.MapGet("/{assignmentId:guid}", GetAsync).RequireAuthorization("tenant.assessment.read");
        tenant.MapPost("/{assignmentId:guid}/publish", PublishAsync).RequireAuthorization("tenant.assessment.manage");
        tenant.MapPost("/{assignmentId:guid}/close", CloseAsync).RequireAuthorization("tenant.assessment.manage");
        tenant.MapPost("/{assignmentId:guid}/submission", SubmitAsync).RequireAuthorization("tenant.assessment.attempt");
        tenant.MapGet("/{assignmentId:guid}/submissions", ListSubmissionsAsync).RequireAuthorization("tenant.grade.manage");
        tenant.MapPost("/submissions/{submissionId:guid}/grade", GradeAsync).RequireAuthorization("tenant.grade.manage");
        tenant.MapGet("/submissions/{submissionId:guid}/file", DownloadAsync).RequireAuthorization("tenant.assessment.read");
    }

    private static async Task<IResult> ListAsync(HttpContext httpContext, LmsDbContext db, Guid? courseId, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var manager = HasPermission(httpContext, LmsPermissions.AssessmentManage);
        var query = db.Assignments.AsNoTracking().AsQueryable();
        if (courseId is Guid selectedCourse) query = query.Where(item => item.CourseId == selectedCourse);
        if (!manager)
        {
            var enrolled = db.Enrollments.Where(item => item.LearnerUserId == userId && (item.Status == EnrollmentStatus.Active || item.Status == EnrollmentStatus.Completed)).Select(item => item.CourseId);
            query = query.Where(item => item.Status != AssignmentStatus.Draft && enrolled.Contains(item.CourseId));
        }
        var assignments = await query.OrderBy(item => item.DueAtUtc == null).ThenBy(item => item.DueAtUtc).ThenBy(item => item.Title).Take(200).ToListAsync(cancellationToken);
        var ids = assignments.Select(item => item.Id).ToList();
        var courseTitles = await db.Courses.AsNoTracking().ToDictionaryAsync(item => item.Id, item => item.Title, cancellationToken);

        var counts = manager
            ? await db.AssignmentSubmissions.AsNoTracking().Where(item => ids.Contains(item.AssignmentId))
                .GroupBy(item => item.AssignmentId).Select(group => new { group.Key, Total = group.Count(), Graded = group.Count(item => item.Status == SubmissionStatus.Graded) })
                .ToDictionaryAsync(item => item.Key, item => (item.Total, item.Graded), cancellationToken)
            : [];
        var mine = await db.AssignmentSubmissions.AsNoTracking().Where(item => ids.Contains(item.AssignmentId) && item.LearnerUserId == userId)
            .ToDictionaryAsync(item => item.AssignmentId, cancellationToken);

        return Results.Ok(assignments.Select(item => ToSummary(item, courseTitles.GetValueOrDefault(item.CourseId, "Course"),
            counts.TryGetValue(item.Id, out var count) ? count.Total : 0, counts.TryGetValue(item.Id, out var graded) ? graded.Graded : 0,
            mine.TryGetValue(item.Id, out var submission) ? ToSubmissionResponse(submission, null) : null)));
    }

    private static async Task<IResult> CreateAsync(HttpContext httpContext, ITenantContext tenantContext, LmsDbContext db, CreateAssignmentRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var title = request.Title?.Trim() ?? string.Empty;
        if (title.Length is < 3 or > 250) return Results.BadRequest(new { message = "Title must be between 3 and 250 characters." });
        if (request.MaxPoints is < 1 or > 1000) return Results.BadRequest(new { message = "Maximum points must be between 1 and 1000." });
        if (request.LatePenaltyPercent is < 0 or > 100) return Results.BadRequest(new { message = "Late penalty must be between 0 and 100 percent." });
        if (request.Instructions is { Length: > MaximumTextLength }) return Results.BadRequest(new { message = "Instructions are too long." });
        if (!await db.Courses.AnyAsync(item => item.Id == request.CourseId, cancellationToken)) return Results.BadRequest(new { message = "The selected course does not exist." });
        var now = DateTimeOffset.UtcNow;
        var assignment = new Assignment
        {
            Id = Guid.NewGuid(), TenantId = tenantId, CourseId = request.CourseId, Title = title,
            Instructions = string.IsNullOrWhiteSpace(request.Instructions) ? null : request.Instructions.Trim(),
            MaxPoints = request.MaxPoints, DueAtUtc = request.DueAtUtc, AllowLate = request.AllowLate,
            LatePenaltyPercent = request.AllowLate ? request.LatePenaltyPercent : 0, Status = AssignmentStatus.Draft,
            CreatedByUserId = userId, CreatedAtUtc = now, UpdatedAtUtc = now
        };
        db.Assignments.Add(assignment);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/assignments/{assignment.Id}", new { assignment.Id });
    }

    private static async Task<IResult> GetAsync(Guid assignmentId, HttpContext httpContext, LmsDbContext db, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var assignment = await FindVisibleAsync(db, httpContext, userId, assignmentId, cancellationToken);
        if (assignment is null) return Results.NotFound();
        var title = await db.Courses.AsNoTracking().Where(item => item.Id == assignment.CourseId).Select(item => item.Title).SingleOrDefaultAsync(cancellationToken) ?? "Course";
        var submission = await db.AssignmentSubmissions.AsNoTracking().SingleOrDefaultAsync(item => item.AssignmentId == assignmentId && item.LearnerUserId == userId, cancellationToken);
        return Results.Ok(ToSummary(assignment, title, 0, 0, submission is null ? null : ToSubmissionResponse(submission, null)));
    }

    private static async Task<IResult> PublishAsync(Guid assignmentId, LmsDbContext db, NotificationService notifications, CancellationToken cancellationToken)
    {
        var result = await ChangeStatusAsync(db, assignmentId, AssignmentStatus.Draft, AssignmentStatus.Published, "Only draft assignments can be published.", cancellationToken);
        if (result is not Microsoft.AspNetCore.Http.HttpResults.NoContent) return result;

        // Tell enrolled learners about the new assignment (once, thanks to the dedup key).
        var assignment = await db.Assignments.AsNoTracking().SingleAsync(item => item.Id == assignmentId, cancellationToken);
        var courseTitle = await db.Courses.AsNoTracking().Where(item => item.Id == assignment.CourseId).Select(item => item.Title).SingleOrDefaultAsync(cancellationToken) ?? "Course";
        var learners = await db.Enrollments.AsNoTracking().Where(item => item.CourseId == assignment.CourseId && item.Status == EnrollmentStatus.Active).Select(item => item.LearnerUserId).ToListAsync(cancellationToken);
        await notifications.QueueManyAsync(db, assignment.TenantId, learners, "ASSIGNMENT_PUBLISHED", new Dictionary<string, string>
        {
            ["Title"] = assignment.Title, ["CourseTitle"] = courseTitle,
            ["DueDate"] = assignment.DueAtUtc is DateTimeOffset due ? due.ToString("u") : "with no deadline"
        }, $"assignment-published:{assignment.Id}", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    private static Task<IResult> CloseAsync(Guid assignmentId, LmsDbContext db, CancellationToken cancellationToken)
        => ChangeStatusAsync(db, assignmentId, AssignmentStatus.Published, AssignmentStatus.Closed, "Only published assignments can be closed.", cancellationToken);

    private static async Task<IResult> ChangeStatusAsync(LmsDbContext db, Guid assignmentId, AssignmentStatus from, AssignmentStatus to, string conflict, CancellationToken cancellationToken)
    {
        var assignment = await db.Assignments.SingleOrDefaultAsync(item => item.Id == assignmentId, cancellationToken);
        if (assignment is null) return Results.NotFound();
        if (assignment.Status != from) return Results.Conflict(new { message = conflict });
        assignment.Status = to;
        assignment.UpdatedAtUtc = DateTimeOffset.UtcNow;
        if (to == AssignmentStatus.Published) assignment.PublishedAtUtc = assignment.UpdatedAtUtc;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> SubmitAsync(Guid assignmentId, HttpRequest request, ITenantContext tenantContext, LmsDbContext db, IContentAssetStorage storage, CancellationToken cancellationToken)
    {
        var httpContext = request.HttpContext;
        if (tenantContext.TenantId is not Guid tenantId || GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var assignment = await FindVisibleAsync(db, httpContext, userId, assignmentId, cancellationToken);
        if (assignment is null) return Results.NotFound();
        if (assignment.Status != AssignmentStatus.Published) return Results.Conflict(new { message = "This assignment is not open for submissions." });

        var now = DateTimeOffset.UtcNow;
        var late = assignment.DueAtUtc is DateTimeOffset due && now > due;
        if (late && !assignment.AllowLate) return Results.Conflict(new { message = "The deadline has passed and late submissions are not accepted." });

        var form = await request.ReadFormAsync(cancellationToken);
        var text = form["text"].ToString().Trim();
        var file = form.Files.FirstOrDefault();
        if (text.Length > MaximumTextLength) return Results.BadRequest(new { message = "The written response is too long." });
        if (text.Length == 0 && (file is null || file.Length == 0)) return Results.BadRequest(new { message = "Provide a written response or attach a file." });
        if (file is not null && file.Length > MaximumSubmissionBytes) return Results.BadRequest(new { message = "Files must be 25 MB or smaller." });

        var submission = await db.AssignmentSubmissions.SingleOrDefaultAsync(item => item.AssignmentId == assignmentId && item.LearnerUserId == userId, cancellationToken);
        if (submission is { Status: SubmissionStatus.Graded }) return Results.Conflict(new { message = "This submission has already been graded." });
        if (submission is null)
        {
            submission = new AssignmentSubmission { Id = Guid.NewGuid(), TenantId = tenantId, AssignmentId = assignmentId, LearnerUserId = userId };
            db.AssignmentSubmissions.Add(submission);
        }
        else submission.SubmissionCount++;

        submission.TextResponse = text.Length == 0 ? null : text;
        if (file is { Length: > 0 })
        {
            var stored = await storage.SaveAsync(tenantId, assignment.CourseId, file, cancellationToken);
            submission.FileStorageKey = stored.StorageKey;
            submission.FileName = stored.OriginalFileName;
            submission.FileContentType = stored.ContentType;
            submission.FileSizeBytes = stored.SizeBytes;
        }
        submission.IsLate = late;
        submission.Status = SubmissionStatus.Submitted;
        submission.SubmittedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToSubmissionResponse(submission, null));
    }

    private static async Task<IResult> ListSubmissionsAsync(Guid assignmentId, LmsDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Assignments.AnyAsync(item => item.Id == assignmentId, cancellationToken)) return Results.NotFound();
        var submissions = await db.AssignmentSubmissions.AsNoTracking().Where(item => item.AssignmentId == assignmentId).OrderBy(item => item.SubmittedAtUtc).ToListAsync(cancellationToken);
        var ids = submissions.Select(item => item.LearnerUserId).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(item => ids.Contains(item.Id)).ToDictionaryAsync(item => item.Id, item => item.DisplayName, cancellationToken);
        return Results.Ok(submissions.Select(item => ToSubmissionResponse(item, names.GetValueOrDefault(item.LearnerUserId, "Unknown"))));
    }

    private static async Task<IResult> GradeAsync(Guid submissionId, HttpContext httpContext, LmsDbContext db, NotificationService notifications, GradeSubmissionRequest request, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid graderId) return Results.Unauthorized();
        var submission = await db.AssignmentSubmissions.SingleOrDefaultAsync(item => item.Id == submissionId, cancellationToken);
        if (submission is null) return Results.NotFound();
        var assignment = await db.Assignments.SingleAsync(item => item.Id == submission.AssignmentId, cancellationToken);
        if (request.ScorePoints < 0 || request.ScorePoints > assignment.MaxPoints)
            return Results.BadRequest(new { message = $"Score must be between 0 and {assignment.MaxPoints}." });
        if (request.Feedback is { Length: > MaximumTextLength }) return Results.BadRequest(new { message = "Feedback is too long." });

        var final = submission.IsLate && assignment.LatePenaltyPercent > 0
            ? Math.Round(request.ScorePoints * (100 - assignment.LatePenaltyPercent) / 100m, 2)
            : request.ScorePoints;
        submission.ScorePoints = request.ScorePoints;
        submission.FinalPoints = final;
        submission.Feedback = string.IsNullOrWhiteSpace(request.Feedback) ? null : request.Feedback.Trim();
        submission.Status = SubmissionStatus.Graded;
        submission.GradedByUserId = graderId;
        submission.GradedAtUtc = DateTimeOffset.UtcNow;
        await notifications.QueueManyAsync(db, submission.TenantId, [submission.LearnerUserId], "ASSIGNMENT_GRADED", new Dictionary<string, string>
        {
            ["Title"] = assignment.Title, ["Score"] = final.ToString("0.##"), ["MaxPoints"] = assignment.MaxPoints.ToString()
        }, $"assignment-graded:{submission.Id}:{final:0.##}", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToSubmissionResponse(submission, null));
    }

    private static async Task<IResult> DownloadAsync(Guid submissionId, HttpContext httpContext, LmsDbContext db, IContentAssetStorage storage, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var submission = await db.AssignmentSubmissions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == submissionId, cancellationToken);
        if (submission?.FileStorageKey is null) return Results.NotFound();
        // Learners may only download their own file; graders may download any.
        if (submission.LearnerUserId != userId && !HasPermission(httpContext, LmsPermissions.GradeManage)) return Results.NotFound();
        var stream = await storage.OpenReadAsync(submission.FileStorageKey, cancellationToken);
        return stream is null ? Results.NotFound() : Results.File(stream, submission.FileContentType ?? "application/octet-stream", submission.FileName);
    }

    // ---------- helpers ----------
    private static async Task<Assignment?> FindVisibleAsync(LmsDbContext db, HttpContext httpContext, Guid userId, Guid assignmentId, CancellationToken cancellationToken)
    {
        var assignment = await db.Assignments.AsNoTracking().SingleOrDefaultAsync(item => item.Id == assignmentId, cancellationToken);
        if (assignment is null) return null;
        if (HasPermission(httpContext, LmsPermissions.AssessmentManage)) return assignment;
        if (assignment.Status == AssignmentStatus.Draft) return null;
        var enrolled = await db.Enrollments.AnyAsync(item => item.CourseId == assignment.CourseId && item.LearnerUserId == userId
            && (item.Status == EnrollmentStatus.Active || item.Status == EnrollmentStatus.Completed), cancellationToken);
        return enrolled ? assignment : null;
    }

    private static AssignmentResponse ToSummary(Assignment item, string courseTitle, int submissionCount, int gradedCount, SubmissionResponse? mine)
        => new(item.Id, item.CourseId, courseTitle, item.Title, item.Instructions, item.MaxPoints, item.DueAtUtc, item.AllowLate, item.LatePenaltyPercent,
            item.Status.ToString(), submissionCount, gradedCount, mine);

    private static SubmissionResponse ToSubmissionResponse(AssignmentSubmission item, string? learnerName)
        => new(item.Id, item.AssignmentId, item.LearnerUserId, learnerName, item.TextResponse, item.FileName, item.FileSizeBytes,
            item.SubmissionCount, item.IsLate, item.Status.ToString(), item.ScorePoints, item.FinalPoints, item.Feedback, item.SubmittedAtUtc, item.GradedAtUtc);

    private static Guid? GetUserId(HttpContext context)
        => Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub"), out var id) ? id : null;

    private static bool HasPermission(HttpContext context, string permission)
        => context.User.Claims.Any(claim => claim.Type == "permission" && claim.Value.Equals(permission, StringComparison.OrdinalIgnoreCase));
}

public sealed record CreateAssignmentRequest(Guid CourseId, string? Title, string? Instructions, int MaxPoints, DateTimeOffset? DueAtUtc, bool AllowLate, int LatePenaltyPercent);
public sealed record GradeSubmissionRequest(decimal ScorePoints, string? Feedback);
public sealed record SubmissionResponse(Guid Id, Guid AssignmentId, Guid LearnerUserId, string? LearnerName, string? TextResponse, string? FileName, long? FileSizeBytes,
    int SubmissionCount, bool IsLate, string Status, decimal? ScorePoints, decimal? FinalPoints, string? Feedback, DateTimeOffset SubmittedAtUtc, DateTimeOffset? GradedAtUtc);
public sealed record AssignmentResponse(Guid Id, Guid CourseId, string CourseTitle, string Title, string? Instructions, int MaxPoints, DateTimeOffset? DueAtUtc, bool AllowLate,
    int LatePenaltyPercent, string Status, int SubmissionCount, int GradedCount, SubmissionResponse? MySubmission);
