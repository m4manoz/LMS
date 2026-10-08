using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Lms.Api.Domain.Assignments;
using Lms.Api.Domain.Identity;
using Lms.Api.Domain.Learning;
using Lms.Api.Features.Assessments;
using Lms.Api.Infrastructure.Assignments;
using Lms.Api.Infrastructure.Notifications;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Storage;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Assignments;

/// <summary>
/// Assignments with text/file submissions, late policy and grading, optionally scored with a rubric and optionally done in groups.
/// Reuses the assessment and grade permissions. Group work keeps one row per member (all carrying the same work and grade) so the gradebook,
/// the task list and the reminders work exactly as they do for individual work.
/// </summary>
public static class AssignmentEndpoints
{
    private const long MaximumSubmissionBytes = 25 * 1024 * 1024;
    private const int MaximumTextLength = 20000;
    private const int MaximumGroupSize = 12;
    private const int MaximumSimilarSubmissions = 300;
    private const long MaximumReadableFileBytes = 1024 * 1024;
    private static readonly HashSet<string> ReadableExtensions = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md", ".csv", ".tex" };

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
        tenant.MapGet("/{assignmentId:guid}/similarity", SimilarityAsync).RequireAuthorization("tenant.grade.manage");
        tenant.MapPost("/submissions/{submissionId:guid}/grade", GradeAsync).RequireAuthorization("tenant.grade.manage");
        tenant.MapGet("/submissions/{submissionId:guid}/file", DownloadAsync).RequireAuthorization("tenant.assessment.read");

        tenant.MapGet("/{assignmentId:guid}/groups", ListGroupsAsync).RequireAuthorization("tenant.assessment.manage");
        tenant.MapPost("/{assignmentId:guid}/groups", CreateGroupAsync).RequireAuthorization("tenant.assessment.manage");
        tenant.MapPut("/{assignmentId:guid}/groups/{groupId:guid}", UpdateGroupAsync).RequireAuthorization("tenant.assessment.manage");
        tenant.MapDelete("/{assignmentId:guid}/groups/{groupId:guid}", DeleteGroupAsync).RequireAuthorization("tenant.assessment.manage");
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
        var rubrics = await RubricsAsync(db, assignments, cancellationToken);
        var groups = await MyGroupsAsync(db, ids, userId, cancellationToken);

        return Results.Ok(assignments.Select(item => ToSummary(item, courseTitles.GetValueOrDefault(item.CourseId, "Course"),
            counts.TryGetValue(item.Id, out var count) ? count.Total : 0, counts.TryGetValue(item.Id, out var graded) ? graded.Graded : 0,
            mine.TryGetValue(item.Id, out var submission) ? ToSubmissionResponse(submission, null) : null,
            item.RubricId is Guid rubricId ? rubrics.GetValueOrDefault(rubricId) : null, groups.GetValueOrDefault(item.Id))));
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

        var maxPoints = request.MaxPoints;
        if (request.RubricId is Guid wanted)
        {
            var rubric = await db.Rubrics.AsNoTracking().SingleOrDefaultAsync(item => item.Id == wanted && item.CourseId == request.CourseId, cancellationToken);
            if (rubric is null) return Results.BadRequest(new { message = "That rubric was not found in this course." });
            maxPoints = rubric.TotalPoints;   // an assignment scored by a rubric is worth exactly what the rubric adds up to
        }
        var now = DateTimeOffset.UtcNow;
        var assignment = new Assignment
        {
            Id = Guid.NewGuid(), TenantId = tenantId, CourseId = request.CourseId, Title = title,
            Instructions = string.IsNullOrWhiteSpace(request.Instructions) ? null : request.Instructions.Trim(),
            MaxPoints = maxPoints, DueAtUtc = request.DueAtUtc, AllowLate = request.AllowLate,
            LatePenaltyPercent = request.AllowLate ? request.LatePenaltyPercent : 0, Status = AssignmentStatus.Draft,
            RubricId = request.RubricId, IsGroup = request.IsGroup ?? false,
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
        var rubrics = await RubricsAsync(db, [assignment], cancellationToken);
        var groups = await MyGroupsAsync(db, [assignmentId], userId, cancellationToken);
        return Results.Ok(ToSummary(assignment, title, 0, 0, submission is null ? null : ToSubmissionResponse(submission, null),
            assignment.RubricId is Guid rubricId ? rubrics.GetValueOrDefault(rubricId) : null, groups.GetValueOrDefault(assignmentId)));
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

        // Whose rows this submission lands on: only the submitter's for individual work, every member's for group work.
        var memberIds = new List<Guid> { userId };
        Guid? groupId = null;
        if (assignment.IsGroup)
        {
            var membership = await db.AssignmentGroupMembers.AsNoTracking().SingleOrDefaultAsync(item => item.AssignmentId == assignmentId && item.LearnerUserId == userId, cancellationToken);
            if (membership is null) return Results.Conflict(new { message = "You have not been placed in a group for this assignment yet. Ask your teacher." });
            groupId = membership.GroupId;
            memberIds = await db.AssignmentGroupMembers.AsNoTracking().Where(item => item.GroupId == membership.GroupId).Select(item => item.LearnerUserId).ToListAsync(cancellationToken);
        }
        var rows = await db.AssignmentSubmissions.Where(item => item.AssignmentId == assignmentId && memberIds.Contains(item.LearnerUserId)).ToListAsync(cancellationToken);
        if (rows.Any(item => item.Status == SubmissionStatus.Graded))
            return Results.Conflict(new { message = assignment.IsGroup ? "This group's work has already been graded." : "This submission has already been graded." });

        StoredAsset? stored = file is { Length: > 0 } ? await storage.SaveAsync(tenantId, assignment.CourseId, file, cancellationToken) : null;
        var kept = rows.FirstOrDefault(item => item.FileStorageKey is not null);
        var replaced = stored is null ? [] : rows.Select(item => item.FileStorageKey).Where(key => key is not null).Distinct().ToList();
        foreach (var memberId in memberIds)
        {
            var row = rows.FirstOrDefault(item => item.LearnerUserId == memberId);
            if (row is null)
            {
                row = new AssignmentSubmission { Id = Guid.NewGuid(), TenantId = tenantId, AssignmentId = assignmentId, LearnerUserId = memberId, GroupId = groupId };
                db.AssignmentSubmissions.Add(row);
                rows.Add(row);
            }
            else row.SubmissionCount++;
            row.GroupId = groupId;
            row.TextResponse = text.Length == 0 ? null : text;
            if (stored is not null) { row.FileStorageKey = stored.StorageKey; row.FileName = stored.OriginalFileName; row.FileContentType = stored.ContentType; row.FileSizeBytes = stored.SizeBytes; }
            else if (row.FileStorageKey is null && kept is not null) { row.FileStorageKey = kept.FileStorageKey; row.FileName = kept.FileName; row.FileContentType = kept.FileContentType; row.FileSizeBytes = kept.FileSizeBytes; }
            row.IsLate = late;
            row.Status = SubmissionStatus.Submitted;
            row.SubmittedAtUtc = now;
        }
        await db.SaveChangesAsync(cancellationToken);
        // The file this one replaced is no longer anyone's, unless another submission (a teammate's copy, say) still points at it.
        foreach (var key in replaced)
            if (key != stored!.StorageKey && !await db.AssignmentSubmissions.AnyAsync(item => item.FileStorageKey == key, cancellationToken)) await storage.DeleteAsync(key!, cancellationToken);
        return Results.Ok(ToSubmissionResponse(rows.First(item => item.LearnerUserId == userId), null));
    }

    private static async Task<IResult> ListSubmissionsAsync(Guid assignmentId, LmsDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Assignments.AnyAsync(item => item.Id == assignmentId, cancellationToken)) return Results.NotFound();
        var submissions = await db.AssignmentSubmissions.AsNoTracking().Where(item => item.AssignmentId == assignmentId).OrderBy(item => item.SubmittedAtUtc).ThenBy(item => item.Id).ToListAsync(cancellationToken);
        var ids = submissions.Select(item => item.LearnerUserId).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(item => ids.Contains(item.Id)).ToDictionaryAsync(item => item.Id, item => item.DisplayName, cancellationToken);
        var groupNames = await db.AssignmentGroups.AsNoTracking().Where(item => item.AssignmentId == assignmentId).ToDictionaryAsync(item => item.Id, item => item.Name, cancellationToken);
        return Results.Ok(submissions.Select(item => ToSubmissionResponse(item, names.GetValueOrDefault(item.LearnerUserId, "Unknown"), item.GroupId is Guid group ? groupNames.GetValueOrDefault(group) : null)));
    }

    private static async Task<IResult> GradeAsync(Guid submissionId, HttpContext httpContext, LmsDbContext db, NotificationService notifications, GradeSubmissionRequest request, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid graderId) return Results.Unauthorized();
        var submission = await db.AssignmentSubmissions.SingleOrDefaultAsync(item => item.Id == submissionId, cancellationToken);
        if (submission is null) return Results.NotFound();
        var assignment = await db.Assignments.SingleAsync(item => item.Id == submission.AssignmentId, cancellationToken);
        if (request.Feedback is { Length: > MaximumTextLength }) return Results.BadRequest(new { message = "Feedback is too long." });

        // With a rubric the score is what the criteria add up to; without one the grader types it.
        var score = request.ScorePoints;
        string? rubricScores = null;
        if (assignment.RubricId is Guid rubricId)
        {
            var rubric = await db.Rubrics.AsNoTracking().SingleOrDefaultAsync(item => item.Id == rubricId, cancellationToken);
            if (rubric is null) return Results.Conflict(new { message = "This assignment's rubric no longer exists." });
            if (RubricRules.Score(RubricRules.Read(rubric.CriteriaJson), request.CriterionScores, out var scores) is { } problem) return Results.BadRequest(new { message = problem });
            score = scores.Sum(item => item.Points);
            rubricScores = JsonSerializer.Serialize(scores);
        }
        else if (request.ScorePoints < 0 || request.ScorePoints > assignment.MaxPoints)
            return Results.BadRequest(new { message = $"Score must be between 0 and {assignment.MaxPoints}." });

        // Group work: everyone in the group gets the same grade and feedback.
        var rows = submission.GroupId is Guid groupId
            ? await db.AssignmentSubmissions.Where(item => item.AssignmentId == assignment.Id && item.GroupId == groupId).ToListAsync(cancellationToken)
            : [submission];
        foreach (var row in rows)
        {
            var final = row.IsLate && assignment.LatePenaltyPercent > 0 ? Math.Round(score * (100 - assignment.LatePenaltyPercent) / 100m, 2) : score;
            row.ScorePoints = score;
            row.FinalPoints = final;
            row.Feedback = string.IsNullOrWhiteSpace(request.Feedback) ? null : request.Feedback.Trim();
            row.RubricScoresJson = rubricScores;
            row.Status = SubmissionStatus.Graded;
            row.GradedByUserId = graderId;
            row.GradedAtUtc = DateTimeOffset.UtcNow;
            await notifications.QueueManyAsync(db, row.TenantId, [row.LearnerUserId], "ASSIGNMENT_GRADED", new Dictionary<string, string>
            {
                ["Title"] = assignment.Title, ["Score"] = final.ToString("0.##"), ["MaxPoints"] = assignment.MaxPoints.ToString()
            }, $"assignment-graded:{row.Id}:{final:0.##}", cancellationToken);
        }
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

    // ---------- groups ----------
    private static async Task<IResult> ListGroupsAsync(Guid assignmentId, LmsDbContext db, CancellationToken cancellationToken)
    {
        var assignment = await db.Assignments.AsNoTracking().SingleOrDefaultAsync(item => item.Id == assignmentId, cancellationToken);
        if (assignment is null) return Results.NotFound();
        var groups = await db.AssignmentGroups.AsNoTracking().Where(item => item.AssignmentId == assignmentId).OrderBy(item => item.Name).ToListAsync(cancellationToken);
        var members = await db.AssignmentGroupMembers.AsNoTracking().Where(item => item.AssignmentId == assignmentId).ToListAsync(cancellationToken);
        var enrolled = await db.Enrollments.AsNoTracking().Where(item => item.CourseId == assignment.CourseId && (item.Status == EnrollmentStatus.Active || item.Status == EnrollmentStatus.Completed)).Select(item => item.LearnerUserId).ToListAsync(cancellationToken);
        var submitted = await db.AssignmentSubmissions.AsNoTracking().Where(item => item.AssignmentId == assignmentId && item.GroupId != null).Select(item => item.GroupId!.Value).Distinct().ToListAsync(cancellationToken);
        var ids = members.Select(item => item.LearnerUserId).Concat(enrolled).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(item => ids.Contains(item.Id)).ToDictionaryAsync(item => item.Id, item => item.DisplayName, cancellationToken);
        var placed = members.Select(item => item.LearnerUserId).ToHashSet();
        return Results.Ok(new GroupsResponse(
            groups.Select(group => new GroupView(group.Id, group.Name, members.Where(item => item.GroupId == group.Id).Select(item => new PersonRef(item.LearnerUserId, names.GetValueOrDefault(item.LearnerUserId, "Unknown"))).OrderBy(item => item.Name).ToList(), submitted.Contains(group.Id))).ToList(),
            enrolled.Where(id => !placed.Contains(id)).Select(id => new PersonRef(id, names.GetValueOrDefault(id, "Unknown"))).OrderBy(item => item.Name).ToList()));
    }

    private static async Task<IResult> CreateGroupAsync(Guid assignmentId, ITenantContext tenantContext, LmsDbContext db, GroupRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId) return Results.Unauthorized();
        var assignment = await db.Assignments.AsNoTracking().SingleOrDefaultAsync(item => item.Id == assignmentId, cancellationToken);
        if (assignment is null) return Results.NotFound();
        if (await ValidateGroupAsync(db, assignment, null, request, cancellationToken) is { } problem) return problem;
        var group = new AssignmentGroup { Id = Guid.NewGuid(), TenantId = tenantId, AssignmentId = assignmentId, Name = request.Name!.Trim(), CreatedAtUtc = DateTimeOffset.UtcNow };
        db.AssignmentGroups.Add(group);
        db.AssignmentGroupMembers.AddRange(request.MemberUserIds!.Distinct().Select(id => new AssignmentGroupMember { Id = Guid.NewGuid(), TenantId = tenantId, AssignmentId = assignmentId, GroupId = group.Id, LearnerUserId = id }));
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/assignments/{assignmentId}/groups/{group.Id}", new { group.Id });
    }

    private static async Task<IResult> UpdateGroupAsync(Guid assignmentId, Guid groupId, LmsDbContext db, ITenantContext tenantContext, GroupRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId) return Results.Unauthorized();
        var assignment = await db.Assignments.AsNoTracking().SingleOrDefaultAsync(item => item.Id == assignmentId, cancellationToken);
        var group = await db.AssignmentGroups.SingleOrDefaultAsync(item => item.Id == groupId && item.AssignmentId == assignmentId, cancellationToken);
        if (assignment is null || group is null) return Results.NotFound();
        if (await db.AssignmentSubmissions.AnyAsync(item => item.GroupId == groupId, cancellationToken))
            return Results.Conflict(new { message = "This group has already submitted its work, so it can no longer be changed." });
        if (await ValidateGroupAsync(db, assignment, groupId, request, cancellationToken) is { } problem) return problem;
        group.Name = request.Name!.Trim();
        db.AssignmentGroupMembers.RemoveRange(await db.AssignmentGroupMembers.Where(item => item.GroupId == groupId).ToListAsync(cancellationToken));
        db.AssignmentGroupMembers.AddRange(request.MemberUserIds!.Distinct().Select(id => new AssignmentGroupMember { Id = Guid.NewGuid(), TenantId = tenantId, AssignmentId = assignmentId, GroupId = groupId, LearnerUserId = id }));
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteGroupAsync(Guid assignmentId, Guid groupId, LmsDbContext db, CancellationToken cancellationToken)
    {
        var group = await db.AssignmentGroups.SingleOrDefaultAsync(item => item.Id == groupId && item.AssignmentId == assignmentId, cancellationToken);
        if (group is null) return Results.NotFound();
        if (await db.AssignmentSubmissions.AnyAsync(item => item.GroupId == groupId, cancellationToken))
            return Results.Conflict(new { message = "This group has already submitted its work, so it cannot be removed." });
        db.AssignmentGroupMembers.RemoveRange(await db.AssignmentGroupMembers.Where(item => item.GroupId == groupId).ToListAsync(cancellationToken));
        db.AssignmentGroups.Remove(group);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Checks a group's name and members: the assignment is group work, the members are enrolled, and nobody is in two groups.</summary>
    private static async Task<IResult?> ValidateGroupAsync(LmsDbContext db, Assignment assignment, Guid? groupId, GroupRequest request, CancellationToken cancellationToken)
    {
        if (!assignment.IsGroup) return Results.Conflict(new { message = "This assignment is not group work." });
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 100) return Results.BadRequest(new { message = "Enter a group name of up to 100 characters." });
        var members = request.MemberUserIds?.Distinct().ToList() ?? [];
        if (members.Count == 0) return Results.BadRequest(new { message = "Put at least one learner in the group." });
        if (members.Count > MaximumGroupSize) return Results.BadRequest(new { message = $"A group can have at most {MaximumGroupSize} members." });
        if (await db.AssignmentGroups.AnyAsync(item => item.AssignmentId == assignment.Id && item.Name == name && item.Id != groupId, cancellationToken))
            return Results.Conflict(new { message = "Another group already has that name." });
        var enrolled = await db.Enrollments.AsNoTracking().Where(item => item.CourseId == assignment.CourseId && members.Contains(item.LearnerUserId) && (item.Status == EnrollmentStatus.Active || item.Status == EnrollmentStatus.Completed)).Select(item => item.LearnerUserId).ToListAsync(cancellationToken);
        if (members.Except(enrolled).Any()) return Results.BadRequest(new { message = "Everyone in a group must be enrolled in the course." });
        var taken = await db.AssignmentGroupMembers.AsNoTracking().Where(item => item.AssignmentId == assignment.Id && item.GroupId != groupId && members.Contains(item.LearnerUserId)).Select(item => item.LearnerUserId).ToListAsync(cancellationToken);
        if (taken.Count > 0)
        {
            var names = await db.Users.AsNoTracking().Where(item => taken.Contains(item.Id)).Select(item => item.DisplayName).ToListAsync(cancellationToken);
            return Results.Conflict(new { message = $"{string.Join(", ", names)} {(names.Count == 1 ? "is" : "are")} already in another group." });
        }
        return null;
    }

    // ---------- similarity ----------
    /// <summary>
    /// Compares learners' written work (and text files) with each other and lists the pairs that share a lot of wording. It is a pointer for a teacher to
    /// read both pieces, not a verdict: common phrases, quoted sources and shared instructions can all produce overlap. Teammates in a group are never compared.
    /// </summary>
    private static async Task<IResult> SimilarityAsync(Guid assignmentId, int? threshold, LmsDbContext db, IContentAssetStorage storage, CancellationToken cancellationToken)
    {
        var assignment = await db.Assignments.AsNoTracking().SingleOrDefaultAsync(item => item.Id == assignmentId, cancellationToken);
        if (assignment is null) return Results.NotFound();
        var limit = Math.Clamp(threshold ?? 30, 5, 100);
        var submissions = await db.AssignmentSubmissions.AsNoTracking().Where(item => item.AssignmentId == assignmentId).OrderBy(item => item.SubmittedAtUtc).Take(MaximumSimilarSubmissions).ToListAsync(cancellationToken);
        var names = await db.Users.AsNoTracking().Where(item => submissions.Select(row => row.LearnerUserId).Contains(item.Id)).ToDictionaryAsync(item => item.Id, item => item.DisplayName, cancellationToken);
        var groupNames = await db.AssignmentGroups.AsNoTracking().Where(item => item.AssignmentId == assignmentId).ToDictionaryAsync(item => item.Id, item => item.Name, cancellationToken);

        var ignore = TextSimilarity.Shingles(TextSimilarity.Words(assignment.Instructions)).ToHashSet();
        var documents = new List<(AssignmentSubmission Row, List<string> Shingles)>();
        var tooShort = 0;
        var filesNotRead = 0;
        foreach (var row in submissions)
        {
            var text = new StringBuilder(row.TextResponse);
            if (row.FileStorageKey is not null)
            {
                var fileText = await ReadTextFileAsync(storage, row, cancellationToken);
                if (fileText is null) filesNotRead++; else text.Append('\n').Append(fileText);
            }
            var words = TextSimilarity.Words(text.ToString());
            if (words.Count < TextSimilarity.MinimumWords) { tooShort++; continue; }
            documents.Add((row, TextSimilarity.Shingles(words, ignore)));
        }

        var pairs = new List<SimilarPair>();
        for (var first = 0; first < documents.Count; first++)
            for (var second = first + 1; second < documents.Count; second++)
            {
                var (a, b) = (documents[first], documents[second]);
                if (a.Row.GroupId is Guid group && group == b.Row.GroupId) continue;   // teammates share one piece of work on purpose
                var match = TextSimilarity.Compare(a.Shingles, b.Shingles);
                if (match.SharedPhrases == 0 || match.Percent < limit) continue;
                pairs.Add(new SimilarPair(
                    a.Row.Id, Label(a.Row, names, groupNames), b.Row.Id, Label(b.Row, names, groupNames), match.Percent, match.SharedPhrases, match.Examples));
            }
        return Results.Ok(new SimilarityReport(submissions.Count, documents.Count, tooShort, filesNotRead, limit, pairs.OrderByDescending(item => item.Percent).ThenByDescending(item => item.SharedPhrases).Take(100).ToList()));
    }

    private static string Label(AssignmentSubmission row, Dictionary<Guid, string> names, Dictionary<Guid, string> groupNames)
        => row.GroupId is Guid group && groupNames.TryGetValue(group, out var groupName) ? groupName : names.GetValueOrDefault(row.LearnerUserId, "Unknown");

    /// <summary>The text of a plain-text upload (up to 1 MB), or null for formats this cannot read (PDF, Word, images).</summary>
    private static async Task<string?> ReadTextFileAsync(IContentAssetStorage storage, AssignmentSubmission row, CancellationToken cancellationToken)
    {
        var readable = ReadableExtensions.Contains(Path.GetExtension(row.FileName ?? string.Empty)) || (row.FileContentType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ?? false);
        if (!readable || row.FileSizeBytes is > MaximumReadableFileBytes) return null;
        await using var stream = await storage.OpenReadAsync(row.FileStorageKey!, cancellationToken);
        if (stream is null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        var buffer = new char[MaximumReadableFileBytes];
        var read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
        return new string(buffer, 0, read);
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

    private static async Task<Dictionary<Guid, RubricView>> RubricsAsync(LmsDbContext db, IEnumerable<Assignment> assignments, CancellationToken cancellationToken)
    {
        var ids = assignments.Where(item => item.RubricId is not null).Select(item => item.RubricId!.Value).Distinct().ToList();
        if (ids.Count == 0) return [];
        return (await db.Rubrics.AsNoTracking().Where(item => ids.Contains(item.Id)).ToListAsync(cancellationToken))
            .ToDictionary(item => item.Id, item => new RubricView(item.Id, item.Name, item.TotalPoints, RubricRules.Read(item.CriteriaJson)));
    }

    /// <summary>For each group-work assignment, the group this learner is in and who is in it.</summary>
    private static async Task<Dictionary<Guid, MyGroupView>> MyGroupsAsync(LmsDbContext db, IReadOnlyCollection<Guid> assignmentIds, Guid userId, CancellationToken cancellationToken)
    {
        var mine = await db.AssignmentGroupMembers.AsNoTracking().Where(item => assignmentIds.Contains(item.AssignmentId) && item.LearnerUserId == userId).ToListAsync(cancellationToken);
        if (mine.Count == 0) return [];
        var groupIds = mine.Select(item => item.GroupId).ToList();
        var groups = await db.AssignmentGroups.AsNoTracking().Where(item => groupIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, item => item.Name, cancellationToken);
        var members = await db.AssignmentGroupMembers.AsNoTracking().Where(item => groupIds.Contains(item.GroupId)).ToListAsync(cancellationToken);
        var names = await db.Users.AsNoTracking().Where(item => members.Select(row => row.LearnerUserId).Contains(item.Id)).ToDictionaryAsync(item => item.Id, item => item.DisplayName, cancellationToken);
        return mine.ToDictionary(item => item.AssignmentId, item => new MyGroupView(item.GroupId, groups.GetValueOrDefault(item.GroupId, "Group"),
            members.Where(row => row.GroupId == item.GroupId).Select(row => names.GetValueOrDefault(row.LearnerUserId, "Unknown")).Order().ToList()));
    }

    private static AssignmentResponse ToSummary(Assignment item, string courseTitle, int submissionCount, int gradedCount, SubmissionResponse? mine, RubricView? rubric = null, MyGroupView? group = null)
        => new(item.Id, item.CourseId, courseTitle, item.Title, item.Instructions, item.MaxPoints, item.DueAtUtc, item.AllowLate, item.LatePenaltyPercent,
            item.Status.ToString(), submissionCount, gradedCount, mine, item.RubricId, item.IsGroup, rubric, group);

    private static SubmissionResponse ToSubmissionResponse(AssignmentSubmission item, string? learnerName, string? groupName = null)
        => new(item.Id, item.AssignmentId, item.LearnerUserId, learnerName, item.TextResponse, item.FileName, item.FileSizeBytes,
            item.SubmissionCount, item.IsLate, item.Status.ToString(), item.ScorePoints, item.FinalPoints, item.Feedback, item.SubmittedAtUtc, item.GradedAtUtc,
            item.GroupId, groupName, item.RubricScoresJson is null ? null : JsonSerializer.Deserialize<List<CriterionScore>>(item.RubricScoresJson));

    private static Guid? GetUserId(HttpContext context)
        => Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub"), out var id) ? id : null;

    private static bool HasPermission(HttpContext context, string permission)
        => context.User.Claims.Any(claim => claim.Type == "permission" && claim.Value.Equals(permission, StringComparison.OrdinalIgnoreCase));
}

public sealed record CreateAssignmentRequest(Guid CourseId, string? Title, string? Instructions, int MaxPoints, DateTimeOffset? DueAtUtc, bool AllowLate, int LatePenaltyPercent, Guid? RubricId = null, bool? IsGroup = null);
/// <summary>With a rubric assignment, send <see cref="CriterionScores"/> (the score is worked out); otherwise <see cref="ScorePoints"/>.</summary>
public sealed record GradeSubmissionRequest(decimal ScorePoints, string? Feedback, List<CriterionScoreInput>? CriterionScores = null);
public sealed record GroupRequest(string? Name, List<Guid>? MemberUserIds);
public sealed record PersonRef(Guid UserId, string Name);
public sealed record GroupView(Guid Id, string Name, IReadOnlyList<PersonRef> Members, bool HasSubmitted);
public sealed record GroupsResponse(IReadOnlyList<GroupView> Groups, IReadOnlyList<PersonRef> Unassigned);
public sealed record MyGroupView(Guid Id, string Name, IReadOnlyList<string> Members);
public sealed record SimilarPair(Guid FirstSubmissionId, string FirstName, Guid SecondSubmissionId, string SecondName, int Percent, int SharedPhrases, IReadOnlyList<string> Examples);
public sealed record SimilarityReport(int Submissions, int Compared, int TooShort, int FilesNotRead, int Threshold, IReadOnlyList<SimilarPair> Pairs);
public sealed record SubmissionResponse(Guid Id, Guid AssignmentId, Guid LearnerUserId, string? LearnerName, string? TextResponse, string? FileName, long? FileSizeBytes,
    int SubmissionCount, bool IsLate, string Status, decimal? ScorePoints, decimal? FinalPoints, string? Feedback, DateTimeOffset SubmittedAtUtc, DateTimeOffset? GradedAtUtc,
    Guid? GroupId = null, string? GroupName = null, List<CriterionScore>? RubricScores = null);
public sealed record AssignmentResponse(Guid Id, Guid CourseId, string CourseTitle, string Title, string? Instructions, int MaxPoints, DateTimeOffset? DueAtUtc, bool AllowLate,
    int LatePenaltyPercent, string Status, int SubmissionCount, int GradedCount, SubmissionResponse? MySubmission,
    Guid? RubricId = null, bool IsGroup = false, RubricView? Rubric = null, MyGroupView? MyGroup = null);
