using System.Security.Claims;
using Lms.Api.Domain.Courses;
using Lms.Api.Domain.Identity;
using Lms.Api.Domain.Learning;
using Lms.Api.Infrastructure.CourseAccess;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Cohorts;

/// <summary>Cohorts: named groups of learners that staff enroll in, or invite to, a course in one step.</summary>
public static class CohortEndpoints
{
    private const int MaxMembersPerRequest = 500;

    public static void MapCohortEndpoints(this WebApplication app)
    {
        var tenant = app.MapGroup("/api/v1/tenant/cohorts").RequireAuthorization("tenant.authenticated");
        tenant.AddEndpointFilter(async (context, next) =>
        {
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (!tenantContext.IsResolved)
                return Results.BadRequest(new { message = "A tenant is required. Use the X-Tenant-Slug header or a tenant subdomain." });
            return await next(context);
        });

        // Cohorts expose who is in which group, so every call needs the ability to manage enrollments.
        tenant.MapGet("", ListAsync).RequireAuthorization("tenant.enrollment.manage");
        tenant.MapPost("", CreateAsync).RequireAuthorization("tenant.enrollment.manage");
        tenant.MapGet("/{cohortId:guid}", GetAsync).RequireAuthorization("tenant.enrollment.manage");
        tenant.MapPut("/{cohortId:guid}", UpdateAsync).RequireAuthorization("tenant.enrollment.manage");
        tenant.MapDelete("/{cohortId:guid}", DeleteAsync).RequireAuthorization("tenant.enrollment.manage");
        tenant.MapPost("/{cohortId:guid}/members", AddMembersAsync).RequireAuthorization("tenant.enrollment.manage");
        tenant.MapDelete("/{cohortId:guid}/members/{userId:guid}", RemoveMemberAsync).RequireAuthorization("tenant.enrollment.manage");
        tenant.MapPost("/{cohortId:guid}/enroll", EnrollAsync).RequireAuthorization("tenant.enrollment.manage");
        tenant.MapPost("/{cohortId:guid}/invite", InviteAsync).RequireAuthorization("tenant.enrollment.manage");
    }

    private static async Task<IResult> ListAsync(LmsDbContext db, CancellationToken cancellationToken)
    {
        var cohorts = await db.Cohorts.AsNoTracking().OrderBy(item => item.Name).ToListAsync(cancellationToken);
        var counts = await db.CohortMembers.AsNoTracking().GroupBy(item => item.CohortId).Select(group => new { group.Key, Count = group.Count() }).ToDictionaryAsync(item => item.Key, item => item.Count, cancellationToken);
        return Results.Ok(cohorts.Select(item => new CohortSummary(item.Id, item.Name, item.Description, item.StartDateAd, item.EndDateAd, counts.GetValueOrDefault(item.Id), item.CreatedAtUtc)));
    }

    private static async Task<IResult> CreateAsync(HttpContext httpContext, ITenantContext tenantContext, LmsDbContext db, SaveCohortRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var error = await ValidateAsync(db, null, request, cancellationToken);
        if (error is not null) return Problem(error);
        var cohort = new Cohort { Id = Guid.NewGuid(), TenantId = tenantId, Name = request.Name!.Trim(), Description = Clean(request.Description), StartDateAd = request.StartDateAd, EndDateAd = request.EndDateAd, CreatedByUserId = userId, CreatedAtUtc = DateTimeOffset.UtcNow };
        db.Cohorts.Add(cohort);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/cohorts/{cohort.Id}", await DetailAsync(db, cohort, cancellationToken));
    }

    private static async Task<IResult> GetAsync(Guid cohortId, LmsDbContext db, CancellationToken cancellationToken)
    {
        var cohort = await db.Cohorts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == cohortId, cancellationToken);
        return cohort is null ? Results.NotFound() : Results.Ok(await DetailAsync(db, cohort, cancellationToken));
    }

    private static async Task<IResult> UpdateAsync(Guid cohortId, LmsDbContext db, SaveCohortRequest request, CancellationToken cancellationToken)
    {
        var cohort = await db.Cohorts.SingleOrDefaultAsync(item => item.Id == cohortId, cancellationToken);
        if (cohort is null) return Results.NotFound();
        var error = await ValidateAsync(db, cohortId, request, cancellationToken);
        if (error is not null) return Problem(error);
        cohort.Name = request.Name!.Trim(); cohort.Description = Clean(request.Description); cohort.StartDateAd = request.StartDateAd; cohort.EndDateAd = request.EndDateAd;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(await DetailAsync(db, cohort, cancellationToken));
    }

    /// <summary>Deleting a cohort removes the group only. Enrollments already made from it are untouched.</summary>
    private static async Task<IResult> DeleteAsync(Guid cohortId, LmsDbContext db, CancellationToken cancellationToken)
    {
        var cohort = await db.Cohorts.SingleOrDefaultAsync(item => item.Id == cohortId, cancellationToken);
        if (cohort is null) return Results.NotFound();
        db.CohortMembers.RemoveRange(await db.CohortMembers.Where(item => item.CohortId == cohortId).ToListAsync(cancellationToken));
        db.Cohorts.Remove(cohort);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> AddMembersAsync(Guid cohortId, ITenantContext tenantContext, LmsDbContext db, AddMembersRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId) return Results.Unauthorized();
        var cohort = await db.Cohorts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == cohortId, cancellationToken);
        if (cohort is null) return Results.NotFound();
        var wanted = (request.UserIds ?? []).Distinct().ToList();
        if (wanted.Count == 0) return Problem("Choose at least one person.");
        if (wanted.Count > MaxMembersPerRequest) return Problem($"Add at most {MaxMembersPerRequest} people at a time.");

        var active = await db.TenantMemberships.AsNoTracking().Where(item => wanted.Contains(item.UserId) && item.Status == MembershipStatus.Active).Select(item => item.UserId).ToListAsync(cancellationToken);
        var already = await db.CohortMembers.AsNoTracking().Where(item => item.CohortId == cohortId && wanted.Contains(item.UserId)).Select(item => item.UserId).ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        foreach (var userId in active.Except(already))
            db.CohortMembers.Add(new CohortMember { Id = Guid.NewGuid(), TenantId = tenantId, CohortId = cohortId, UserId = userId, AddedAtUtc = now });
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok((await DetailAsync(db, cohort, cancellationToken)) with { Skipped = wanted.Except(active).Count() });
    }

    private static async Task<IResult> RemoveMemberAsync(Guid cohortId, Guid userId, LmsDbContext db, CancellationToken cancellationToken)
    {
        var member = await db.CohortMembers.SingleOrDefaultAsync(item => item.CohortId == cohortId && item.UserId == userId, cancellationToken);
        if (member is null) return Results.NotFound();
        db.CohortMembers.Remove(member);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Enrolls every member straight away. Each person gets their own outcome: enrolled, waitlisted, already in, or blocked by a prerequisite.</summary>
    private static async Task<IResult> EnrollAsync(Guid cohortId, ITenantContext tenantContext, LmsDbContext db, EnrollmentService enrollments, CohortCourseRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId) return Results.Unauthorized();
        var cohort = await db.Cohorts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == cohortId, cancellationToken);
        if (cohort is null) return Results.NotFound();
        var course = await db.Courses.SingleOrDefaultAsync(item => item.Id == request.CourseId && item.Status == CourseStatus.Published, cancellationToken);
        if (course is null) return Results.NotFound(new { message = "A published course was not found." });

        var members = await db.CohortMembers.AsNoTracking().Where(item => item.CohortId == cohortId).OrderBy(item => item.AddedAtUtc).ToListAsync(cancellationToken);
        if (members.Count == 0) return Problem("This cohort has no members yet.");
        var users = await db.Users.AsNoTracking().Where(item => members.Select(m => m.UserId).Contains(item.Id)).ToDictionaryAsync(item => item.Id, cancellationToken);

        var results = new List<MemberOutcome>();
        foreach (var member in members)
        {
            var name = users.GetValueOrDefault(member.UserId)?.DisplayName ?? "Unknown";
            var result = await enrollments.EnrollAsync(db, tenantId, course, member.UserId, EnrollmentSource.Cohort, checkPrerequisites: !request.OverridePrerequisites, enforceDates: false, cancellationToken);
            results.Add(new MemberOutcome(member.UserId, name, result.Outcome.ToString(), result.Message, result.MissingPrerequisites));
        }
        return Results.Ok(Summarise(results));
    }

    /// <summary>Sends each member an invitation to accept. Members without an account cannot be reached and are reported as skipped.</summary>
    private static async Task<IResult> InviteAsync(Guid cohortId, HttpContext httpContext, ITenantContext tenantContext, LmsDbContext db, InvitationService invitations, CohortInviteRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || GetUserId(httpContext) is not Guid inviterId) return Results.Unauthorized();
        var cohort = await db.Cohorts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == cohortId, cancellationToken);
        if (cohort is null) return Results.NotFound();
        var course = await db.Courses.AsNoTracking().SingleOrDefaultAsync(item => item.Id == request.CourseId && item.Status == CourseStatus.Published, cancellationToken);
        if (course is null) return Results.NotFound(new { message = "A published course was not found." });
        var days = request.ExpiresInDays ?? InvitationService.DefaultDays;
        if (days is < 1 or > 90) return Problem("An invitation can last 1 to 90 days.");

        var memberIds = await db.CohortMembers.AsNoTracking().Where(item => item.CohortId == cohortId).Select(item => item.UserId).ToListAsync(cancellationToken);
        if (memberIds.Count == 0) return Problem("This cohort has no members yet.");
        var users = await db.Users.AsNoTracking().Where(item => memberIds.Contains(item.Id)).ToListAsync(cancellationToken);
        var inviter = httpContext.User.FindFirstValue(ClaimTypes.Name) ?? "A teacher";

        var results = new List<MemberOutcome>();
        foreach (var user in users.OrderBy(item => item.DisplayName))
        {
            var result = await invitations.InviteAsync(db, tenantId, course, user.Email, inviterId, inviter, request.Message, days, cancellationToken);
            results.Add(new MemberOutcome(user.Id, user.DisplayName, result.Outcome == InviteOutcome.Created ? "Invited" : "AlreadyEnrolled", null, []));
        }
        return Results.Ok(Summarise(results));
    }

    private static BulkResult Summarise(List<MemberOutcome> results) => new(results,
        results.Count(item => item.Outcome is "Enrolled" or "Invited"), results.Count(item => item.Outcome == "Waitlisted"),
        results.Count(item => item.Outcome == "AlreadyEnrolled"), results.Count(item => item.Outcome is "MissingPrerequisites" or "NotAvailable"));

    private static async Task<CohortDetail> DetailAsync(LmsDbContext db, Cohort cohort, CancellationToken cancellationToken)
    {
        var members = await db.CohortMembers.AsNoTracking().Where(item => item.CohortId == cohort.Id).ToListAsync(cancellationToken);
        var ids = members.Select(item => item.UserId).ToList();
        var users = await db.Users.AsNoTracking().Where(item => ids.Contains(item.Id)).ToDictionaryAsync(item => item.Id, cancellationToken);
        return new CohortDetail(cohort.Id, cohort.Name, cohort.Description, cohort.StartDateAd, cohort.EndDateAd,
            members.Where(item => users.ContainsKey(item.UserId)).Select(item => new CohortMemberResponse(item.UserId, users[item.UserId].DisplayName, users[item.UserId].Email, item.AddedAtUtc))
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList(), 0);
    }

    private static async Task<string?> ValidateAsync(LmsDbContext db, Guid? cohortId, SaveCohortRequest request, CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is < 2 or > 120) return "The cohort name must be between 2 and 120 characters.";
        if (request.Description is { Length: > 1000 }) return "The description must be 1000 characters or fewer.";
        if (request.StartDateAd is DateOnly start && request.EndDateAd is DateOnly end && end < start) return "The end date must be on or after the start date.";
        var lower = name.ToLowerInvariant();
        if (await db.Cohorts.AnyAsync(item => item.Name.ToLower() == lower && item.Id != cohortId, cancellationToken)) return "A cohort with this name already exists.";
        return null;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static IResult Problem(string message) => Results.BadRequest(new { message });

    private static Guid? GetUserId(HttpContext context)
        => Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub"), out var id) ? id : null;
}

public sealed record SaveCohortRequest(string? Name, string? Description, DateOnly? StartDateAd, DateOnly? EndDateAd);
public sealed record AddMembersRequest(List<Guid>? UserIds);
public sealed record CohortCourseRequest(Guid CourseId, bool OverridePrerequisites = false);
public sealed record CohortInviteRequest(Guid CourseId, string? Message, int? ExpiresInDays);
public sealed record CohortSummary(Guid Id, string Name, string? Description, DateOnly? StartDateAd, DateOnly? EndDateAd, int MemberCount, DateTimeOffset CreatedAtUtc);
public sealed record CohortMemberResponse(Guid UserId, string Name, string Email, DateTimeOffset AddedAtUtc);
public sealed record CohortDetail(Guid Id, string Name, string? Description, DateOnly? StartDateAd, DateOnly? EndDateAd, IReadOnlyList<CohortMemberResponse> Members, int Skipped);
public sealed record MemberOutcome(Guid UserId, string Name, string Outcome, string? Message, IReadOnlyList<string> MissingPrerequisites);
public sealed record BulkResult(IReadOnlyList<MemberOutcome> Results, int Succeeded, int Waitlisted, int AlreadyIn, int Blocked);
