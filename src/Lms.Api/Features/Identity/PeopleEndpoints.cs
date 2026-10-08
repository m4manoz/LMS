using System.Security.Claims;
using Lms.Api.Domain.Courses;
using Lms.Api.Domain.Identity;
using Lms.Api.Domain.Learning;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Security;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Identity;

/// <summary>
/// Dedicated views of the organization's learners and instructors: a searchable directory, and one person's profile with what they are doing
/// (a learner's courses and progress, an instructor's courses and learners). Staff only; a learner or guardian never sees other people's details.
/// Date of birth is shown only to people who can manage users.
/// </summary>
public static class PeopleEndpoints
{
    private const int MaxRows = 500;
    private const string LearnerRole = "LEARNER";
    private const string InstructorRole = "TEACHER";

    public static void MapPeopleEndpoints(this WebApplication app)
    {
        var people = app.MapGroup("/api/v1/tenant/people").RequireAuthorization("tenant.authenticated");
        people.AddEndpointFilter(async (context, next) =>
            context.HttpContext.RequestServices.GetRequiredService<ITenantContext>().IsResolved
                ? await next(context)
                : Results.BadRequest(new { message = "A tenant is required. Use the X-Tenant-Slug header or a tenant subdomain." }));
        people.MapGet("", ListAsync);
        people.MapGet("/{userId:guid}", GetAsync);
        people.MapPut("/{userId:guid}/profile", SaveProfileAsync);
    }

    private static bool Has(HttpContext httpContext, string permission) => httpContext.User.HasClaim("permission", permission);
    /// <summary>Administrators and anyone who manages enrollments (teachers).</summary>
    private static bool IsStaff(HttpContext httpContext) => Has(httpContext, LmsPermissions.UserRead) || Has(httpContext, LmsPermissions.EnrollmentManage);

    private static string? RoleFor(string? kind) => kind?.ToLowerInvariant() switch { "learners" => LearnerRole, "instructors" => InstructorRole, _ => null };

    private static async Task<IResult> ListAsync(string? kind, string? q, HttpContext httpContext, LmsDbContext db, CancellationToken cancellationToken)
    {
        if (!IsStaff(httpContext)) return Results.Forbid();
        if (RoleFor(kind) is not { } roleCode) return Results.BadRequest(new { message = "Kind must be learners or instructors." });

        var members = db.TenantMemberships.AsNoTracking().Where(item => item.Status == MembershipStatus.Active && item.Role.Code == roleCode);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            members = members.Where(item => item.User.DisplayName.ToLower().Contains(term) || item.User.Email.ToLower().Contains(term));
        }
        var rows = await members.OrderBy(item => item.User.DisplayName).Take(MaxRows)
            .Select(item => new { item.UserId, item.User.DisplayName, item.User.Email, Status = item.User.Status.ToString(), item.CreatedAtUtc }).ToListAsync(cancellationToken);
        var ids = rows.Select(item => item.UserId).ToList();

        if (roleCode == LearnerRole)
        {
            var profiles = await db.LearnerProfiles.AsNoTracking().Where(item => ids.Contains(item.UserId)).ToDictionaryAsync(item => item.UserId, cancellationToken);
            var counts = (await db.Enrollments.AsNoTracking().Where(item => ids.Contains(item.LearnerUserId) && (item.Status == EnrollmentStatus.Active || item.Status == EnrollmentStatus.Completed))
                .GroupBy(item => item.LearnerUserId).Select(group => new { group.Key, Count = group.Count() }).ToListAsync(cancellationToken)).ToDictionary(item => item.Key, item => item.Count);
            return Results.Ok(rows.Select(item => new PersonRow(item.UserId, item.DisplayName, item.Email, item.Status, item.CreatedAtUtc,
                profiles.GetValueOrDefault(item.UserId)?.StudentNumber, profiles.GetValueOrDefault(item.UserId)?.GradeLevel, counts.GetValueOrDefault(item.UserId))));
        }

        var teachers = await db.TeacherProfiles.AsNoTracking().Where(item => ids.Contains(item.UserId)).ToDictionaryAsync(item => item.UserId, cancellationToken);
        var owned = (await db.Courses.AsNoTracking().Where(item => ids.Contains(item.OwnerUserId))
            .GroupBy(item => item.OwnerUserId).Select(group => new { group.Key, Count = group.Count() }).ToListAsync(cancellationToken)).ToDictionary(item => item.Key, item => item.Count);
        return Results.Ok(rows.Select(item => new PersonRow(item.UserId, item.DisplayName, item.Email, item.Status, item.CreatedAtUtc,
            teachers.GetValueOrDefault(item.UserId)?.EmployeeNumber, teachers.GetValueOrDefault(item.UserId)?.SubjectSpecialty, owned.GetValueOrDefault(item.UserId))));
    }

    private static async Task<IResult> GetAsync(Guid userId, HttpContext httpContext, LmsDbContext db, CancellationToken cancellationToken)
    {
        if (!IsStaff(httpContext)) return Results.Forbid();
        // Users are shared across organizations, so being a member here is what makes someone visible here.
        var memberships = await db.TenantMemberships.AsNoTracking().Include(item => item.User).Include(item => item.Role)
            .Where(item => item.UserId == userId && item.Status == MembershipStatus.Active).ToListAsync(cancellationToken);
        if (memberships.Count == 0) return Results.NotFound();
        var user = memberships[0].User;
        var roles = memberships.Select(item => item.Role.Code).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var canSeeBirth = Has(httpContext, LmsPermissions.UserRead);
        var learner = await db.LearnerProfiles.AsNoTracking().SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        var teacher = await db.TeacherProfiles.AsNoTracking().SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);

        var enrollments = await db.Enrollments.AsNoTracking().Where(item => item.LearnerUserId == userId && item.Status != EnrollmentStatus.Withdrawn)
            .Join(db.Courses.AsNoTracking(), enrollment => enrollment.CourseId, course => course.Id, (enrollment, course) => new LearnerCourse(course.Id, course.Code, course.Title, enrollment.Status.ToString(), enrollment.ProgressPercent, enrollment.EnrolledAtUtc, enrollment.LastAccessedAtUtc, enrollment.CompletedAtUtc))
            .OrderByDescending(item => item.EnrolledAtUtc).Take(100).ToListAsync(cancellationToken);
        var courses = await db.Courses.AsNoTracking().Where(item => item.OwnerUserId == userId).OrderBy(item => item.Title).Take(100).ToListAsync(cancellationToken);
        var courseIds = courses.Select(item => item.Id).ToList();
        var enrolled = (await db.Enrollments.AsNoTracking().Where(item => courseIds.Contains(item.CourseId) && (item.Status == EnrollmentStatus.Active || item.Status == EnrollmentStatus.Completed))
            .GroupBy(item => item.CourseId).Select(group => new { group.Key, Count = group.Count() }).ToListAsync(cancellationToken)).ToDictionary(item => item.Key, item => item.Count);
        var taught = courses.Select(item => new InstructorCourse(item.Id, item.Code, item.Title, item.Status.ToString(), enrolled.GetValueOrDefault(item.Id))).ToList();

        var active = enrollments.Where(item => item.Status == nameof(EnrollmentStatus.Active)).ToList();
        var summary = new PersonSummary(
            active.Count, enrollments.Count(item => item.Status == nameof(EnrollmentStatus.Completed)),
            active.Count == 0 ? 0 : (int)Math.Round(active.Average(item => item.ProgressPercent)),
            taught.Count, taught.Sum(item => item.Learners));

        return Results.Ok(new PersonProfile(user.Id, user.DisplayName, user.Email, user.Status.ToString(), roles, memberships.Min(item => item.CreatedAtUtc),
            learner is null ? null : new LearnerProfileView(learner.StudentNumber, learner.GradeLevel, canSeeBirth ? learner.DateOfBirthAd : null),
            teacher is null ? null : new TeacherProfileView(teacher.EmployeeNumber, teacher.SubjectSpecialty),
            summary, enrollments, taught));
    }

    private static async Task<IResult> SaveProfileAsync(Guid userId, SaveProfileRequest request, HttpContext httpContext, LmsDbContext db, ITenantContext tenantContext, SecurityAuditService audit, CancellationToken cancellationToken)
    {
        if (!Has(httpContext, LmsPermissions.UserManage)) return Results.Forbid();
        if (tenantContext.TenantId is not Guid tenantId) return Results.BadRequest(new { message = "A tenant is required." });
        var roles = await db.TenantMemberships.AsNoTracking().Where(item => item.UserId == userId && item.Status == MembershipStatus.Active).Select(item => item.Role.Code).ToListAsync(cancellationToken);
        if (roles.Count == 0) return Results.NotFound();

        var errors = new Dictionary<string, string[]>();
        string? Clean(string? value, int max, string field)
        {
            var text = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (text is not null && text.Length > max) errors[field] = [$"Use at most {max} characters."];
            return text;
        }
        var kind = request.Kind?.ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        if (kind == "learner")
        {
            var number = Clean(request.StudentNumber, 80, "studentNumber");
            var grade = Clean(request.GradeLevel, 80, "gradeLevel");
            if (request.DateOfBirthAd is { } birth && (birth > DateOnly.FromDateTime(DateTime.UtcNow) || birth.Year < 1900)) errors["dateOfBirthAd"] = ["Enter a real date of birth."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var profile = await db.LearnerProfiles.SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
            if (profile is null) db.LearnerProfiles.Add(profile = new LearnerProfile { Id = Guid.NewGuid(), TenantId = tenantId, UserId = userId, CreatedAtUtc = now });
            profile.StudentNumber = number; profile.GradeLevel = grade; profile.DateOfBirthAd = request.DateOfBirthAd; profile.UpdatedAtUtc = now;
        }
        else if (kind == "instructor")
        {
            var number = Clean(request.EmployeeNumber, 80, "employeeNumber");
            var specialty = Clean(request.SubjectSpecialty, 200, "subjectSpecialty");
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var profile = await db.TeacherProfiles.SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
            if (profile is null) db.TeacherProfiles.Add(profile = new TeacherProfile { Id = Guid.NewGuid(), TenantId = tenantId, UserId = userId, CreatedAtUtc = now });
            profile.EmployeeNumber = number; profile.SubjectSpecialty = specialty; profile.UpdatedAtUtc = now;
        }
        else return Results.BadRequest(new { message = "Kind must be learner or instructor." });

        audit.Add(db, httpContext, tenantId, "user.profile.updated", "user", userId, new { kind });
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }
}

/// <summary>For a learner, Detail1 is the student number and Detail2 the grade level; for an instructor, the employee number and specialty. Count is courses taken or owned.</summary>
public sealed record PersonRow(Guid UserId, string DisplayName, string Email, string Status, DateTimeOffset JoinedAtUtc, string? Detail1, string? Detail2, int Count);
public sealed record LearnerProfileView(string? StudentNumber, string? GradeLevel, DateOnly? DateOfBirthAd);
public sealed record TeacherProfileView(string? EmployeeNumber, string? SubjectSpecialty);
public sealed record LearnerCourse(Guid CourseId, string Code, string Title, string Status, int ProgressPercent, DateTimeOffset EnrolledAtUtc, DateTimeOffset? LastAccessedAtUtc, DateTimeOffset? CompletedAtUtc);
public sealed record InstructorCourse(Guid CourseId, string Code, string Title, string Status, int Learners);
public sealed record PersonSummary(int ActiveCourses, int CompletedCourses, int AverageProgressPercent, int CoursesTaught, int LearnersTaught);
public sealed record PersonProfile(Guid UserId, string DisplayName, string Email, string Status, string[] Roles, DateTimeOffset JoinedAtUtc,
    LearnerProfileView? Learner, TeacherProfileView? Teacher, PersonSummary Summary, IReadOnlyList<LearnerCourse> Enrollments, IReadOnlyList<InstructorCourse> Courses);
public sealed record SaveProfileRequest(string? Kind, string? StudentNumber, string? GradeLevel, DateOnly? DateOfBirthAd, string? EmployeeNumber, string? SubjectSpecialty);
