using System.Security.Claims;
using Lms.Api.Domain.Courses;
using Lms.Api.Domain.Identity;
using Lms.Api.Domain.Learning;
using Lms.Api.Infrastructure.CourseAccess;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Enrollments;

/// <summary>Withdrawing, capacity and the waitlist, plus the rules that decide who may enroll and when content opens.</summary>
public static class EnrollmentManagementEndpoints
{
    private const int MaxPrerequisites = 10;

    public static void MapEnrollmentManagementEndpoints(this WebApplication app)
    {
        var tenant = app.MapGroup("/api/v1/tenant").RequireAuthorization("tenant.authenticated");
        tenant.AddEndpointFilter(async (context, next) =>
        {
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (!tenantContext.IsResolved)
                return Results.BadRequest(new { message = "A tenant is required. Use the X-Tenant-Slug header or a tenant subdomain." });
            return await next(context);
        });

        tenant.MapPost("/enrollments/{enrollmentId:guid}/withdraw", WithdrawAsync).RequireAuthorization("tenant.enrollment.read");
        tenant.MapGet("/courses/{courseId:guid}/enrollment-summary", SummaryAsync).RequireAuthorization("tenant.enrollment.manage");
        tenant.MapGet("/courses/{courseId:guid}/enrollments", RosterAsync).RequireAuthorization("tenant.enrollment.manage");
        tenant.MapGet("/courses/{courseId:guid}/enrollable-learners", EnrollableAsync).RequireAuthorization("tenant.enrollment.manage");
        tenant.MapPost("/courses/{courseId:guid}/enrollments", EnrollManyAsync).RequireAuthorization("tenant.enrollment.manage");
        tenant.MapPut("/courses/{courseId:guid}/capacity", SetCapacityAsync).RequireAuthorization("tenant.course.manage");
        tenant.MapPost("/courses/{courseId:guid}/waitlist/promote", PromoteAsync).RequireAuthorization("tenant.enrollment.manage");

        tenant.MapGet("/courses/{courseId:guid}/access-rules", GetRulesAsync).RequireAuthorization("tenant.course.manage");
        tenant.MapPut("/courses/{courseId:guid}/prerequisites", SetPrerequisitesAsync).RequireAuthorization("tenant.course.manage");
        tenant.MapPut("/courses/{courseId:guid}/modules/{moduleId:guid}/access", SetModuleAccessAsync).RequireAuthorization("tenant.course.manage");
    }

    // ---------- withdraw, capacity, waitlist ----------
    private static async Task<IResult> WithdrawAsync(Guid enrollmentId, HttpContext httpContext, LmsDbContext db, EnrollmentService enrollments, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var enrollment = await db.Enrollments.SingleOrDefaultAsync(item => item.Id == enrollmentId, cancellationToken);
        // Learners may only withdraw themselves; to anyone else the enrollment simply does not exist.
        if (enrollment is null || (enrollment.LearnerUserId != userId && !HasPermission(httpContext, LmsPermissions.EnrollmentManage))) return Results.NotFound();
        if (enrollment.Status == EnrollmentStatus.Completed) return Results.Conflict(new { message = "A completed course cannot be withdrawn from." });
        if (enrollment.Status == EnrollmentStatus.Withdrawn) return Results.Ok(new { status = enrollment.Status.ToString(), promoted = 0 });

        var releasedSeat = enrollment.Status == EnrollmentStatus.Active;
        enrollment.Status = EnrollmentStatus.Withdrawn;
        enrollment.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        // A freed seat goes to the person who has waited longest.
        var promoted = 0;
        if (releasedSeat && await db.Courses.SingleOrDefaultAsync(item => item.Id == enrollment.CourseId, cancellationToken) is { } course)
            promoted = (await enrollments.PromoteWaitlistAsync(db, course, null, cancellationToken)).Count;
        return Results.Ok(new { status = enrollment.Status.ToString(), promoted });
    }

    private static async Task<IResult> SummaryAsync(Guid courseId, LmsDbContext db, CancellationToken cancellationToken)
    {
        var course = await db.Courses.AsNoTracking().SingleOrDefaultAsync(item => item.Id == courseId, cancellationToken);
        return course is null ? Results.NotFound() : Results.Ok(await BuildSummaryAsync(db, course, cancellationToken));
    }

    private static readonly EnrollmentStatus[] TakingPart = [EnrollmentStatus.Active, EnrollmentStatus.Completed, EnrollmentStatus.Waitlisted, EnrollmentStatus.Suspended];

    /// <summary>Who is in the course: enrolled, completed or suspended. People waiting are listed with the waitlist.</summary>
    private static async Task<IResult> RosterAsync(Guid courseId, LmsDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Courses.AnyAsync(item => item.Id == courseId, cancellationToken)) return Results.NotFound();
        var rows = await (from enrollment in db.Enrollments.AsNoTracking()
                          join user in db.Users.AsNoTracking() on enrollment.LearnerUserId equals user.Id
                          where enrollment.CourseId == courseId && (enrollment.Status == EnrollmentStatus.Active || enrollment.Status == EnrollmentStatus.Completed || enrollment.Status == EnrollmentStatus.Suspended)
                          orderby user.DisplayName
                          select new { enrollment.Id, enrollment.LearnerUserId, user.DisplayName, user.Email, enrollment.Status, enrollment.Source, enrollment.ProgressPercent, enrollment.EnrolledAtUtc }).Take(1000).ToListAsync(cancellationToken);
        return Results.Ok(rows.Select(item => new RosterEntry(item.Id, item.LearnerUserId, item.DisplayName, item.Email, item.Status.ToString(), item.Source.ToString(), item.ProgressPercent, item.EnrolledAtUtc)).ToList());
    }

    /// <summary>People of the organization who could be put in the course: active members who are not already in it or waiting for it.</summary>
    private static async Task<IResult> EnrollableAsync(Guid courseId, string? q, LmsDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Courses.AnyAsync(item => item.Id == courseId, cancellationToken)) return Results.NotFound();
        var taken = db.Enrollments.AsNoTracking().Where(item => item.CourseId == courseId && TakingPart.Contains(item.Status)).Select(item => item.LearnerUserId);
        var query = db.TenantMemberships.AsNoTracking().Where(item => item.Status == MembershipStatus.Active && item.User.Status == UserStatus.Active && !taken.Contains(item.UserId));
        var needle = (q ?? string.Empty).Trim().ToLower();
        if (needle.Length > 100) return Results.BadRequest(new { message = "Search for 100 characters or fewer." });
        if (needle.Length > 0) query = query.Where(item => item.User.DisplayName.ToLower().Contains(needle) || item.User.Email.ToLower().Contains(needle));
        var people = await query.OrderBy(item => item.User.DisplayName).Take(50).Select(item => new { item.UserId, item.User.DisplayName, item.User.Email, RoleName = item.Role.Name }).ToListAsync(cancellationToken);
        return Results.Ok(people.Select(item => new EnrollableLearner(item.UserId, item.DisplayName, item.Email, item.RoleName)).ToList());
    }

    /// <summary>Puts several people in a course at once. Each is enrolled, or put on the waitlist when the course is full, and gets a notification.</summary>
    private static async Task<IResult> EnrollManyAsync(Guid courseId, HttpContext httpContext, LmsDbContext db, ITenantContext tenantContext, EnrollmentService enrollments, EnrollManyRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId) return Results.Unauthorized();
        var ids = (request.LearnerUserIds ?? []).Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count is 0 or > 100) return Results.BadRequest(new { message = "Choose between 1 and 100 people to enroll." });
        var course = await db.Courses.SingleOrDefaultAsync(item => item.Id == courseId, cancellationToken);
        if (course is null) return Results.NotFound();
        if (course.Status != CourseStatus.Published) return Results.Conflict(new { message = "Publish the course before enrolling learners." });

        var people = await db.TenantMemberships.AsNoTracking().Where(item => ids.Contains(item.UserId) && item.Status == MembershipStatus.Active && item.User.Status == UserStatus.Active)
            .Select(item => new { item.UserId, item.User.DisplayName }).ToDictionaryAsync(item => item.UserId, item => item.DisplayName, cancellationToken);
        var results = new List<EnrollManyResult>();
        foreach (var id in ids)
        {
            if (!people.TryGetValue(id, out var name)) { results.Add(new EnrollManyResult(id, "Unknown", "NotAvailable", "This person is not an active member of the organization.")); continue; }
            var result = await enrollments.EnrollAsync(db, tenantId, course, id, EnrollmentSource.Administrator, checkPrerequisites: !request.OverridePrerequisites, enforceDates: false, cancellationToken);
            results.Add(new EnrollManyResult(id, name, result.Outcome.ToString(), result.Message));
        }
        return Results.Ok(new EnrollManyResponse(results.Count(item => item.Outcome == nameof(EnrollOutcome.Enrolled)), results.Count(item => item.Outcome == nameof(EnrollOutcome.Waitlisted)), results));
    }

    private static async Task<IResult> SetCapacityAsync(Guid courseId, LmsDbContext db, EnrollmentService enrollments, SetCapacityRequest request, CancellationToken cancellationToken)
    {
        var course = await db.Courses.SingleOrDefaultAsync(item => item.Id == courseId, cancellationToken);
        if (course is null) return Results.NotFound();
        if (course.Status == CourseStatus.Archived) return Results.Conflict(new { message = "An archived course cannot change capacity." });
        if (request.Capacity is < 1 or > 1_000_000) return Results.BadRequest(new { message = "Capacity must be between 1 and 1,000,000, or empty for no limit." });
        course.Capacity = request.Capacity;
        course.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        // More seats means people waiting can come in straight away. Fewer seats never removes anyone who is already in.
        var promoted = await enrollments.PromoteWaitlistAsync(db, course, null, cancellationToken);
        var summary = await BuildSummaryAsync(db, course, cancellationToken);
        return Results.Ok(summary with { JustPromoted = promoted.Count });
    }

    private static async Task<IResult> PromoteAsync(Guid courseId, LmsDbContext db, EnrollmentService enrollments, PromoteRequest? request, CancellationToken cancellationToken)
    {
        var course = await db.Courses.SingleOrDefaultAsync(item => item.Id == courseId, cancellationToken);
        if (course is null) return Results.NotFound();
        if (course.Status != CourseStatus.Published) return Results.Conflict(new { message = "Only published courses have a waitlist." });
        if (request?.Count is < 1 or > 1000) return Results.BadRequest(new { message = "Count must be between 1 and 1000." });
        var promoted = await enrollments.PromoteWaitlistAsync(db, course, request?.Count, cancellationToken);
        return Results.Ok((await BuildSummaryAsync(db, course, cancellationToken)) with { JustPromoted = promoted.Count });
    }

    private static async Task<EnrollmentSummary> BuildSummaryAsync(LmsDbContext db, Course course, CancellationToken cancellationToken)
    {
        var counts = await db.Enrollments.AsNoTracking().Where(item => item.CourseId == course.Id).GroupBy(item => item.Status).Select(group => new { group.Key, Count = group.Count() }).ToListAsync(cancellationToken);
        int Count(EnrollmentStatus status) => counts.FirstOrDefault(item => item.Key == status)?.Count ?? 0;
        var waiting = await db.Enrollments.AsNoTracking().Where(item => item.CourseId == course.Id && item.Status == EnrollmentStatus.Waitlisted).OrderBy(item => item.EnrolledAtUtc).ToListAsync(cancellationToken);
        var ids = waiting.Select(item => item.LearnerUserId).ToList();
        var users = await db.Users.AsNoTracking().Where(item => ids.Contains(item.Id)).ToDictionaryAsync(item => item.Id, cancellationToken);
        var active = Count(EnrollmentStatus.Active);
        return new EnrollmentSummary(course.Id, course.Capacity, active, Count(EnrollmentStatus.Completed), course.Capacity is int cap ? Math.Max(0, cap - active) : null,
            waiting.Select((item, index) => new WaitlistEntry(item.Id, item.LearnerUserId, users.GetValueOrDefault(item.LearnerUserId)?.DisplayName ?? "Unknown", users.GetValueOrDefault(item.LearnerUserId)?.Email ?? string.Empty, index + 1, item.EnrolledAtUtc)).ToList(), 0);
    }

    // ---------- prerequisites and drip rules ----------
    private static async Task<IResult> GetRulesAsync(Guid courseId, LmsDbContext db, CancellationToken cancellationToken)
    {
        var course = await db.Courses.AsNoTracking().SingleOrDefaultAsync(item => item.Id == courseId, cancellationToken);
        if (course is null) return Results.NotFound();
        return Results.Ok(await BuildRulesAsync(db, course, cancellationToken));
    }

    private static async Task<IResult> SetPrerequisitesAsync(Guid courseId, ITenantContext tenantContext, LmsDbContext db, SetPrerequisitesRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId) return Results.Unauthorized();
        var course = await db.Courses.AsNoTracking().SingleOrDefaultAsync(item => item.Id == courseId, cancellationToken);
        if (course is null) return Results.NotFound();
        var wanted = (request.CourseIds ?? []).Distinct().ToList();
        if (wanted.Count > MaxPrerequisites) return Problem($"A course can have at most {MaxPrerequisites} prerequisites.");
        if (wanted.Contains(courseId)) return Problem("A course cannot be a prerequisite of itself.");
        var known = await db.Courses.AsNoTracking().Where(item => wanted.Contains(item.Id)).Select(item => item.Id).ToListAsync(cancellationToken);
        if (known.Count != wanted.Count) return Problem("One or more of the chosen courses do not exist.");

        // Reject a loop such as A needs B and B needs A: follow the existing rules from each new prerequisite and see if we reach this course.
        var edges = await db.CoursePrerequisites.AsNoTracking().Where(item => item.CourseId != courseId).ToListAsync(cancellationToken);
        if (wanted.Any(required => Reaches(required, courseId, edges))) return Problem("That would create a loop: one of those courses already depends on this one.");

        var existing = await db.CoursePrerequisites.Where(item => item.CourseId == courseId).ToListAsync(cancellationToken);
        db.CoursePrerequisites.RemoveRange(existing.Where(item => !wanted.Contains(item.RequiredCourseId)));
        foreach (var id in wanted.Where(id => existing.All(item => item.RequiredCourseId != id)))
            db.CoursePrerequisites.Add(new CoursePrerequisite { Id = Guid.NewGuid(), TenantId = tenantId, CourseId = courseId, RequiredCourseId = id });
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(await BuildRulesAsync(db, course, cancellationToken));
    }

    private static bool Reaches(Guid from, Guid target, List<CoursePrerequisite> edges)
    {
        var seen = new HashSet<Guid>();
        var stack = new Stack<Guid>([from]);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == target) return true;
            if (!seen.Add(current)) continue;
            foreach (var edge in edges.Where(item => item.CourseId == current)) stack.Push(edge.RequiredCourseId);
        }
        return false;
    }

    private static async Task<IResult> SetModuleAccessAsync(Guid courseId, Guid moduleId, ITenantContext tenantContext, LmsDbContext db, SetModuleAccessRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId) return Results.Unauthorized();
        var course = await db.Courses.AsNoTracking().SingleOrDefaultAsync(item => item.Id == courseId, cancellationToken);
        if (course?.CurrentVersionId is not Guid versionId) return Results.NotFound();
        var modules = await db.CourseModules.AsNoTracking().Where(item => item.CourseVersionId == versionId).ToListAsync(cancellationToken);
        var module = modules.FirstOrDefault(item => item.Id == moduleId);
        if (module is null) return Results.NotFound();

        if (request.ReleaseAfterDays is < 0 or > 3650) return Problem("Days after enrollment must be between 0 and 3650.");
        if (request.RequiresModuleId is Guid requiredId)
        {
            var required = modules.FirstOrDefault(item => item.Id == requiredId);
            if (required is null) return Problem("The required module is not part of this course.");
            // Only an earlier module can be required, which also makes loops impossible.
            if (required.DisplayOrder >= module.DisplayOrder) return Problem("A module can only require one that comes before it.");
        }

        var rule = await db.ModuleAccessRules.SingleOrDefaultAsync(item => item.ModuleId == moduleId, cancellationToken);
        if (request is { ReleaseAfterDays: null, ReleaseOnUtc: null, RequiresModuleId: null })
        {
            if (rule is not null) db.ModuleAccessRules.Remove(rule); // no conditions left: the module is simply open
        }
        else
        {
            if (rule is null) { rule = new ModuleAccessRule { Id = Guid.NewGuid(), TenantId = tenantId, ModuleId = moduleId }; db.ModuleAccessRules.Add(rule); }
            rule.ReleaseAfterDays = request.ReleaseAfterDays; rule.ReleaseOnUtc = request.ReleaseOnUtc; rule.RequiresModuleId = request.RequiresModuleId; rule.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(await BuildRulesAsync(db, course, cancellationToken));
    }

    private static async Task<AccessRules> BuildRulesAsync(LmsDbContext db, Course course, CancellationToken cancellationToken)
    {
        var requiredIds = await db.CoursePrerequisites.AsNoTracking().Where(item => item.CourseId == course.Id).Select(item => item.RequiredCourseId).ToListAsync(cancellationToken);
        var prerequisites = await db.Courses.AsNoTracking().Where(item => requiredIds.Contains(item.Id)).OrderBy(item => item.Title).Select(item => new PrerequisiteCourse(item.Id, item.Code, item.Title)).ToListAsync(cancellationToken);
        var modules = course.CurrentVersionId is Guid versionId
            ? await db.CourseModules.AsNoTracking().Where(item => item.CourseVersionId == versionId).OrderBy(item => item.DisplayOrder).ToListAsync(cancellationToken)
            : [];
        var moduleIds = modules.Select(item => item.Id).ToList();
        var rules = await db.ModuleAccessRules.AsNoTracking().Where(item => moduleIds.Contains(item.ModuleId)).ToDictionaryAsync(item => item.ModuleId, cancellationToken);
        return new AccessRules(prerequisites, modules.Select(module =>
        {
            rules.TryGetValue(module.Id, out var rule);
            return new ModuleRule(module.Id, module.Title, module.DisplayOrder, rule?.ReleaseAfterDays, rule?.ReleaseOnUtc, rule?.RequiresModuleId);
        }).ToList());
    }

    private static IResult Problem(string message) => Results.BadRequest(new { message });

    private static Guid? GetUserId(HttpContext context)
        => Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub"), out var id) ? id : null;

    private static bool HasPermission(HttpContext context, string permission)
        => context.User.Claims.Any(claim => claim.Type == "permission" && claim.Value.Equals(permission, StringComparison.OrdinalIgnoreCase));
}

public sealed record SetCapacityRequest(int? Capacity);
public sealed record PromoteRequest(int? Count);
public sealed record WaitlistEntry(Guid EnrollmentId, Guid LearnerUserId, string Name, string Email, int Position, DateTimeOffset JoinedAtUtc);
/// <summary>FreeSeats is null for unlimited capacity. JustPromoted is how many people the last action moved off the waitlist.</summary>
public sealed record EnrollmentSummary(Guid CourseId, int? Capacity, int Active, int Completed, int? FreeSeats, IReadOnlyList<WaitlistEntry> Waitlist, int JustPromoted);
public sealed record SetPrerequisitesRequest(List<Guid>? CourseIds);
public sealed record SetModuleAccessRequest(int? ReleaseAfterDays, DateTimeOffset? ReleaseOnUtc, Guid? RequiresModuleId);
public sealed record PrerequisiteCourse(Guid CourseId, string Code, string Title);
public sealed record ModuleRule(Guid ModuleId, string Title, int DisplayOrder, int? ReleaseAfterDays, DateTimeOffset? ReleaseOnUtc, Guid? RequiresModuleId);
public sealed record AccessRules(IReadOnlyList<PrerequisiteCourse> Prerequisites, IReadOnlyList<ModuleRule> Modules);

public sealed record RosterEntry(Guid EnrollmentId, Guid LearnerUserId, string Name, string Email, string Status, string Source, int ProgressPercent, DateTimeOffset EnrolledAtUtc);
public sealed record EnrollableLearner(Guid UserId, string Name, string Email, string Role);
public sealed record EnrollManyRequest(Guid[]? LearnerUserIds, bool OverridePrerequisites = false);
public sealed record EnrollManyResult(Guid LearnerUserId, string Name, string Outcome, string? Message);
public sealed record EnrollManyResponse(int Enrolled, int Waitlisted, List<EnrollManyResult> Results);
