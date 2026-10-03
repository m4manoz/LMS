using System.Net.Mail;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Lms.Api.Domain.Identity;
using Lms.Api.Domain.Tenants;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Security;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Identity;

public static class IdentityEndpoints
{
    private static readonly DefaultRole[] DefaultRoles =
    [
        new("TENANT_ADMIN", "Tenant Administrator", LmsPermissions.All),
        new("TEACHER", "Teacher", [LmsPermissions.TenantRead, LmsPermissions.CourseRead, LmsPermissions.CourseManage, LmsPermissions.CourseReview, LmsPermissions.TeacherRead, LmsPermissions.TeacherManage, LmsPermissions.LearnerRead, LmsPermissions.EnrollmentRead, LmsPermissions.EnrollmentManage, LmsPermissions.ProgressRead, LmsPermissions.AssessmentRead, LmsPermissions.AssessmentManage, LmsPermissions.GradeRead, LmsPermissions.GradeManage, LmsPermissions.NotificationRead, LmsPermissions.CertificateRead, LmsPermissions.CertificateManage, LmsPermissions.ReportRead, LmsPermissions.ReportExport, LmsPermissions.AiRead, LmsPermissions.AiManage, LmsPermissions.AiUse, LmsPermissions.LiveClassRead, LmsPermissions.LiveClassManage, LmsPermissions.AttendanceRead, LmsPermissions.AttendanceManage, LmsPermissions.CollaborationRead, LmsPermissions.CollaborationManage, LmsPermissions.SecurityRead, LmsPermissions.ForumModerate, LmsPermissions.AnnouncementManage]),
        new("LEARNER", "Learner", [LmsPermissions.TenantRead, LmsPermissions.CourseRead, LmsPermissions.LearnerRead, LmsPermissions.EnrollmentRead, LmsPermissions.ProgressRead, LmsPermissions.ProgressManage, LmsPermissions.AssessmentRead, LmsPermissions.AssessmentAttempt, LmsPermissions.GradeRead, LmsPermissions.NotificationRead, LmsPermissions.CertificateRead, LmsPermissions.AiUse, LmsPermissions.LiveClassRead, LmsPermissions.AttendanceRead, LmsPermissions.CollaborationRead, LmsPermissions.CollaborationManage, LmsPermissions.RecommendationRead, LmsPermissions.GamificationRead, LmsPermissions.VirtualLabRead]),
        new("GUARDIAN", "Guardian", [LmsPermissions.TenantRead, LmsPermissions.CourseRead, LmsPermissions.LearnerRead, LmsPermissions.GuardianRead, LmsPermissions.EnrollmentRead, LmsPermissions.ProgressRead, LmsPermissions.AssessmentRead, LmsPermissions.GradeRead, LmsPermissions.NotificationRead, LmsPermissions.CertificateRead, LmsPermissions.LiveClassRead, LmsPermissions.AttendanceRead, LmsPermissions.CollaborationRead]),
        new("CONTENT_EDITOR", "Content Editor", [LmsPermissions.TenantRead, LmsPermissions.CourseRead, LmsPermissions.CourseManage, LmsPermissions.CourseReview, LmsPermissions.CoursePublish, LmsPermissions.AssessmentRead, LmsPermissions.AssessmentManage, LmsPermissions.NotificationRead, LmsPermissions.ReportRead, LmsPermissions.AiRead, LmsPermissions.AiManage, LmsPermissions.AiUse, LmsPermissions.LiveClassRead, LmsPermissions.LiveClassManage, LmsPermissions.CollaborationRead, LmsPermissions.CollaborationManage, LmsPermissions.VirtualLabRead, LmsPermissions.VirtualLabManage])
    ];

    public static void MapIdentityEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/api/v1/auth");
        auth.MapPost("/login", LoginAsync);
        auth.MapPost("/refresh", RefreshAsync);

        var platform = app.MapGroup("/api/v1/platform");
        platform.MapPost("/tenants/{slug}/bootstrap-admin", BootstrapAdminAsync);

        var tenant = app.MapGroup("/api/v1/tenant").RequireAuthorization("tenant.authenticated");
        tenant.AddEndpointFilter(async (context, next) =>
        {
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (!tenantContext.IsResolved)
            {
                return Results.BadRequest(new { message = "A tenant is required. Use the X-Tenant-Slug header or a tenant subdomain." });
            }

            return await next(context);
        });
        tenant.MapGet("/me", GetCurrentUser).RequireAuthorization("tenant.authenticated");
        tenant.MapGet("/roles", GetRoles).RequireAuthorization("tenant.role.read");
        tenant.MapPost("/roles", CreateRoleAsync).RequireAuthorization("tenant.role.manage");
        tenant.MapPut("/roles/{roleId:guid}", UpdateRoleAsync).RequireAuthorization("tenant.role.manage");
        tenant.MapGet("/users", GetUsers).RequireAuthorization("tenant.user.read");
        tenant.MapPost("/users", CreateUserAsync).RequireAuthorization("tenant.user.manage");
        tenant.MapPut("/users/{userId:guid}/role", AssignRoleAsync).RequireAuthorization("tenant.role.manage");
        tenant.MapDelete("/users/{userId:guid}/roles/{roleId:guid}", RemoveRoleAsync).RequireAuthorization("tenant.role.manage");
        tenant.MapPost("/guardian-links", CreateGuardianLinkAsync).RequireAuthorization("tenant.guardian.manage");
    }

    private static async Task<IResult> LoginAsync(
        HttpContext httpContext,
        LmsDbContext db,
        ITenantContext tenantContext,
        PasswordService passwordService,
        JwtTokenService tokenService,
        LoginRequest request,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var slug = TenantSlug.Normalize(request.TenantSlug);
        var email = NormalizeEmail(request.Email);
        if (!TenantSlug.IsValid(slug) || !IsEmail(email) || string.IsNullOrWhiteSpace(request.Password))
            return Results.Unauthorized();

        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(
            item => item.Slug == slug && item.Status == TenantStatus.Active, cancellationToken);
        if (tenant is null) return Results.Unauthorized();

        tenantContext.Set(tenant.Id, tenant.Slug);
        var user = await db.Users.SingleOrDefaultAsync(item => item.NormalizedEmail == email, cancellationToken);
        var memberships = user is null
            ? []
            : await db.TenantMemberships.Include(item => item.Role).ThenInclude(item => item.Permissions)
                .Where(item => item.UserId == user.Id && item.Status == MembershipStatus.Active)
                .ToListAsync(cancellationToken);

        if (user is null || memberships.Count == 0 || user.Status != UserStatus.Active || !passwordService.Verify(request.Password, user.PasswordHash))
            return Results.Unauthorized();

        var rawRefreshToken = RefreshTokenService.CreateToken();
        var refreshSession = new RefreshSession
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, UserId = user.Id,
            TokenHash = RefreshTokenService.HashToken(rawRefreshToken),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(Math.Clamp(configuration.GetValue("Auth:RefreshTokenDays", 30), 1, 90)),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CreatedIp = httpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = httpContext.Request.Headers.UserAgent.ToString()
        };
        db.RefreshSessions.Add(refreshSession);
        var roles = memberships.Select(item => item.Role.Code).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var permissions = memberships.SelectMany(item => item.Role.Permissions).Select(item => item.PermissionCode).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
        var token = tokenService.Create(user, tenant, roles, permissions, refreshSession.Id);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new AuthSessionResponse(
            token.Token, token.ExpiresAtUtc, rawRefreshToken,
            new TenantAuthResponse(tenant.Id, tenant.Slug, tenant.Name),
            new UserAuthResponse(user.Id, user.Email, user.DisplayName),
            SelectPrimaryRole(roles), roles, permissions));
    }

    private static async Task<IResult> RefreshAsync(
        HttpContext httpContext,
        LmsDbContext db,
        ITenantContext tenantContext,
        JwtTokenService tokenService,
        RefreshRequest request,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var slug = TenantSlug.Normalize(request.TenantSlug);
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(item => item.Slug == slug && item.Status == TenantStatus.Active, cancellationToken);
        if (tenant is null || string.IsNullOrWhiteSpace(request.RefreshToken)) return Results.Unauthorized();
        tenantContext.Set(tenant.Id, tenant.Slug);
        var hash = RefreshTokenService.HashToken(request.RefreshToken);
        var session = await db.RefreshSessions.IgnoreQueryFilters().Include(item => item.User)
            .SingleOrDefaultAsync(item => item.TenantId == tenant.Id && item.TokenHash == hash, cancellationToken);
        if (session is null || session.RevokedAtUtc is not null || session.ExpiresAtUtc <= DateTimeOffset.UtcNow || session.User.Status != UserStatus.Active)
            return Results.Unauthorized();
        var memberships = await db.TenantMemberships.IgnoreQueryFilters().Include(item => item.Role).ThenInclude(item => item.Permissions)
            .Where(item => item.TenantId == tenant.Id && item.UserId == session.UserId && item.Status == MembershipStatus.Active)
            .ToListAsync(cancellationToken);
        if (memberships.Count == 0) return Results.Unauthorized();

        session.RevokedAtUtc = DateTimeOffset.UtcNow;
        session.LastUsedAtUtc = DateTimeOffset.UtcNow;
        var nextRefreshToken = RefreshTokenService.CreateToken();
        var nextSession = new RefreshSession
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, UserId = session.UserId,
            TokenHash = RefreshTokenService.HashToken(nextRefreshToken),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(Math.Clamp(configuration.GetValue("Auth:RefreshTokenDays", 30), 1, 90)),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CreatedIp = httpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = httpContext.Request.Headers.UserAgent.ToString()
        };
        db.RefreshSessions.Add(nextSession);
        var roles = memberships.Select(item => item.Role.Code).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var permissions = memberships.SelectMany(item => item.Role.Permissions).Select(item => item.PermissionCode).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
        var token = tokenService.Create(session.User, tenant, roles, permissions, nextSession.Id);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new AuthSessionResponse(token.Token, token.ExpiresAtUtc, nextRefreshToken,
            new TenantAuthResponse(tenant.Id, tenant.Slug, tenant.Name),
            new UserAuthResponse(session.User.Id, session.User.Email, session.User.DisplayName), SelectPrimaryRole(roles), roles, permissions));
    }

    private static async Task<IResult> BootstrapAdminAsync(
        HttpContext httpContext,
        IConfiguration configuration,
        LmsDbContext db,
        PasswordService passwordService,
        string slug,
        BootstrapAdminRequest request,
        CancellationToken cancellationToken)
    {
        if (!HasProvisioningAccess(httpContext, configuration)) return Results.Unauthorized();
        var tenant = await db.Tenants.SingleOrDefaultAsync(item => item.Slug == TenantSlug.Normalize(slug), cancellationToken);
        if (tenant is null) return Results.NotFound();
        var email = NormalizeEmail(request.Email);
        if (!IsEmail(email) || string.IsNullOrWhiteSpace(request.DisplayName))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["email"] = ["A valid email is required."], ["displayName"] = ["Display name is required."] });
        try { PasswordService.Validate(request.Password); }
        catch (ArgumentException exception) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = [exception.Message] }); }

        await EnsureDefaultRolesAsync(db, tenant.Id, cancellationToken);
        var role = await db.Roles.IgnoreQueryFilters().SingleAsync(item => item.TenantId == tenant.Id && item.Code == "TENANT_ADMIN", cancellationToken);
        var user = await db.Users.SingleOrDefaultAsync(item => item.NormalizedEmail == email, cancellationToken);
        if (user is null)
        {
            user = new AppUser { Id = Guid.NewGuid(), Email = request.Email.Trim(), NormalizedEmail = email, DisplayName = request.DisplayName.Trim(), PasswordHash = passwordService.Hash(request.Password), Status = UserStatus.Active, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow };
            db.Users.Add(user);
        }
        var existing = await db.TenantMemberships.IgnoreQueryFilters().AnyAsync(item => item.TenantId == tenant.Id && item.UserId == user.Id, cancellationToken);
        if (existing) return Results.Conflict(new { message = "This user is already a member of the tenant." });
        db.TenantMemberships.Add(new TenantMembership { Id = Guid.NewGuid(), TenantId = tenant.Id, UserId = user.Id, RoleId = role.Id, Status = MembershipStatus.Active, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/platform/tenants/{tenant.Slug}/bootstrap-admin", new { tenant = new { tenant.Id, tenant.Slug }, user = new { user.Id, user.Email, user.DisplayName }, role = role.Code });
    }

    private static IResult GetCurrentUser(HttpContext httpContext, ITenantContext tenantContext) => Results.Ok(new
    {
        userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier),
        displayName = httpContext.User.FindFirstValue(ClaimTypes.Name),
        email = httpContext.User.FindFirstValue(ClaimTypes.Email),
        tenantId = tenantContext.TenantId,
        tenantSlug = tenantContext.TenantSlug,
        role = SelectPrimaryRole(httpContext.User.FindAll(ClaimTypes.Role).Select(item => item.Value)),
        roles = httpContext.User.FindAll(ClaimTypes.Role).Select(item => item.Value).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray(),
        permissions = httpContext.User.FindAll("permission").Select(item => item.Value).Order().ToArray()
    });

    private static async Task<IResult> GetRoles(LmsDbContext db, CancellationToken cancellationToken)
    {
        var roles = await db.Roles.AsNoTracking().Include(item => item.Permissions).OrderBy(item => item.Name)
            .Select(item => new RoleResponse(item.Id, item.Code, item.Name, item.IsSystemRole, item.Permissions.Select(permission => permission.PermissionCode).Order().ToArray()))
            .ToListAsync(cancellationToken);
        return Results.Ok(roles);
    }

    private static async Task<IResult> CreateRoleAsync(
        HttpContext httpContext,
        LmsDbContext db,
        ITenantContext tenantContext,
        SecurityAuditService audit,
        CreateRoleRequest request,
        CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId) return Results.BadRequest(new { message = "A tenant is required." });
        var validation = ValidateRole(request.Code, request.Name, request.Permissions);
        if (validation is not null) return validation;
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Roles.AnyAsync(item => item.Code == code, cancellationToken)) return Results.Conflict(new { message = "A role with this code already exists." });
        var now = DateTimeOffset.UtcNow;
        var role = new Role { Id = Guid.NewGuid(), TenantId = tenantId, Code = code, Name = request.Name.Trim(), IsSystemRole = false, CreatedAtUtc = now };
        db.Roles.Add(role);
        db.RolePermissions.AddRange((request.Permissions ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Select(permission => new RolePermission { RoleId = role.Id, PermissionCode = permission }));
        audit.Add(db, httpContext, tenantId, "role.created", "role", role.Id, new { role.Code, permissions = request.Permissions });
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/roles/{role.Id:D}", new RoleResponse(role.Id, role.Code, role.Name, role.IsSystemRole, (request.Permissions ?? []).Order(StringComparer.OrdinalIgnoreCase).ToArray()));
    }

    private static async Task<IResult> UpdateRoleAsync(
        HttpContext httpContext,
        LmsDbContext db,
        ITenantContext tenantContext,
        SecurityAuditService audit,
        Guid roleId,
        UpdateRoleRequest request,
        CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId) return Results.BadRequest(new { message = "A tenant is required." });
        var validation = ValidateRole(request.Code, request.Name, request.Permissions);
        if (validation is not null) return validation;
        var role = await db.Roles.Include(item => item.Permissions).SingleOrDefaultAsync(item => item.Id == roleId, cancellationToken);
        if (role is null) return Results.NotFound();
        if (role.IsSystemRole) return Results.Conflict(new { message = "System roles cannot be edited. Create a custom role instead." });
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Roles.AnyAsync(item => item.Id != roleId && item.Code == code, cancellationToken)) return Results.Conflict(new { message = "A role with this code already exists." });
        role.Code = code; role.Name = request.Name.Trim();
        db.RolePermissions.RemoveRange(role.Permissions);
        db.RolePermissions.AddRange((request.Permissions ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Select(permission => new RolePermission { RoleId = role.Id, PermissionCode = permission }));
        audit.Add(db, httpContext, tenantId, "role.updated", "role", role.Id, new { role.Code, permissions = request.Permissions });
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new RoleResponse(role.Id, role.Code, role.Name, role.IsSystemRole, (request.Permissions ?? []).Order(StringComparer.OrdinalIgnoreCase).ToArray()));
    }

    private static async Task<IResult> AssignRoleAsync(
        HttpContext httpContext,
        LmsDbContext db,
        ITenantContext tenantContext,
        SecurityAuditService audit,
        Guid userId,
        AssignRoleRequest request,
        CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId) return Results.BadRequest(new { message = "A tenant is required." });
        var role = await db.Roles.SingleOrDefaultAsync(item => item.Id == request.RoleId, cancellationToken);
        var userExists = await db.Users.AnyAsync(item => item.Id == userId, cancellationToken);
        if (!userExists || role is null) return Results.NotFound();
        var membership = await db.TenantMemberships.SingleOrDefaultAsync(item => item.UserId == userId && item.RoleId == role.Id, cancellationToken);
        if (membership is not null && membership.Status == MembershipStatus.Active)
            return Results.Conflict(new { message = "This role is already assigned to the user." });
        var now = DateTimeOffset.UtcNow;
        if (membership is null)
        {
            membership = new TenantMembership { Id = Guid.NewGuid(), TenantId = tenantId, UserId = userId, RoleId = role.Id, Status = MembershipStatus.Active, CreatedAtUtc = now, UpdatedAtUtc = now };
            db.TenantMemberships.Add(membership);
        }
        else
        {
            membership.Status = MembershipStatus.Active;
            membership.UpdatedAtUtc = now;
        }
        audit.Add(db, httpContext, tenantId, "user.role_assigned", "user", userId, new { roleId = role.Id, role.Code });
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new { userId, roleId = role.Id, role.Code, role.Name, updatedAtUtc = membership.UpdatedAtUtc });
    }

    private static async Task<IResult> RemoveRoleAsync(
        HttpContext httpContext,
        LmsDbContext db,
        ITenantContext tenantContext,
        SecurityAuditService audit,
        Guid userId,
        Guid roleId,
        CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId) return Results.BadRequest(new { message = "A tenant is required." });
        var membership = await db.TenantMemberships.Include(item => item.Role).SingleOrDefaultAsync(item => item.UserId == userId && item.RoleId == roleId && item.Status == MembershipStatus.Active, cancellationToken);
        if (membership is null) return Results.NotFound();
        var activeRoleCount = await db.TenantMemberships.CountAsync(item => item.UserId == userId && item.Status == MembershipStatus.Active, cancellationToken);
        if (activeRoleCount <= 1) return Results.Conflict(new { message = "A user must retain at least one active role." });
        membership.Status = MembershipStatus.Revoked;
        membership.UpdatedAtUtc = DateTimeOffset.UtcNow;
        audit.Add(db, httpContext, tenantId, "user.role_revoked", "user", userId, new { roleId, roleCode = membership.Role.Code });
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static IResult? ValidateRole(string? codeValue, string? nameValue, string[]? permissions)
    {
        var errors = new Dictionary<string, string[]>();
        var code = codeValue?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!Regex.IsMatch(code, "^[A-Z][A-Z0-9_]{1,79}$")) errors[nameof(codeValue)] = ["Use 2-80 uppercase letters, numbers, or underscores; it must start with a letter."];
        if (string.IsNullOrWhiteSpace(nameValue) || nameValue.Trim().Length > 150) errors[nameof(nameValue)] = ["Role name is required and must be at most 150 characters."];
        var selected = (permissions ?? []).Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim().ToLowerInvariant()).Distinct().ToArray();
        var invalid = selected.Except(LmsPermissions.All, StringComparer.OrdinalIgnoreCase).ToArray();
        if (invalid.Length > 0) errors[nameof(permissions)] = [$"Unknown permissions: {string.Join(", ", invalid)}."];
        return errors.Count == 0 ? null : Results.ValidationProblem(errors);
    }

    private static async Task<IResult> GetUsers(LmsDbContext db, CancellationToken cancellationToken)
    {
        var memberships = await db.TenantMemberships.AsNoTracking().Include(item => item.User).Include(item => item.Role)
            .Where(item => item.Status == MembershipStatus.Active)
            .OrderBy(item => item.User.DisplayName)
            .ToListAsync(cancellationToken);
        var users = memberships.GroupBy(item => item.UserId).Select(group =>
        {
            var roles = group.Select(item => item.Role).OrderBy(item => item.Code, StringComparer.OrdinalIgnoreCase).ToArray();
            var primary = roles.FirstOrDefault(item => item.Code.Equals("TENANT_ADMIN", StringComparison.OrdinalIgnoreCase)) ?? roles[0];
            return new UserResponse(group.Key, group.First().User.Email, group.First().User.DisplayName, group.First().User.Status.ToString(), MembershipStatus.Active.ToString(), primary.Code, primary.Name, roles.Select(item => item.Code).ToArray());
        }).ToArray();
        return Results.Ok(users);
    }

    private static async Task<IResult> CreateUserAsync(
        LmsDbContext db,
        ITenantContext tenantContext,
        PasswordService passwordService,
        CreateTenantUserRequest request,
        CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId) return Results.BadRequest(new { message = "A tenant is required." });
        var email = NormalizeEmail(request.Email);
        if (!IsEmail(email) || string.IsNullOrWhiteSpace(request.DisplayName))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["email"] = ["A valid email is required."], ["displayName"] = ["Display name is required."] });
        try { PasswordService.Validate(request.Password); }
        catch (ArgumentException exception) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = [exception.Message] }); }
        var role = await db.Roles.SingleOrDefaultAsync(item => item.Code == request.RoleCode.Trim().ToUpperInvariant(), cancellationToken);
        if (role is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["roleCode"] = ["The selected role does not exist."] });
        if (await db.Users.AnyAsync(item => item.NormalizedEmail == email, cancellationToken)) return Results.Conflict(new { message = "A user with this email already exists." });

        var user = new AppUser { Id = Guid.NewGuid(), Email = request.Email.Trim(), NormalizedEmail = email, DisplayName = request.DisplayName.Trim(), PasswordHash = passwordService.Hash(request.Password), Status = UserStatus.Active, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow };
        var membership = new TenantMembership { Id = Guid.NewGuid(), TenantId = tenantId, UserId = user.Id, RoleId = role.Id, Status = MembershipStatus.Active, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow };
        db.Users.Add(user);
        db.TenantMemberships.Add(membership);
        var profileType = string.IsNullOrWhiteSpace(request.ProfileType) ? role.Code : request.ProfileType.Trim().ToUpperInvariant();
        if (profileType is "LEARNER" or "STUDENT") db.LearnerProfiles.Add(new LearnerProfile { Id = Guid.NewGuid(), TenantId = tenantId, UserId = user.Id, DateOfBirthAd = request.DateOfBirthAd, StudentNumber = request.StudentNumber, GradeLevel = request.GradeLevel, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        else if (profileType is "TEACHER" or "INSTRUCTOR") db.TeacherProfiles.Add(new TeacherProfile { Id = Guid.NewGuid(), TenantId = tenantId, UserId = user.Id, EmployeeNumber = request.EmployeeNumber, SubjectSpecialty = request.SubjectSpecialty, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        else if (profileType is "GUARDIAN" or "PARENT") db.GuardianProfiles.Add(new GuardianProfile { Id = Guid.NewGuid(), TenantId = tenantId, UserId = user.Id, PhoneNumber = request.PhoneNumber, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/users/{user.Id:D}", new UserResponse(user.Id, user.Email, user.DisplayName, user.Status.ToString(), membership.Status.ToString(), role.Code, role.Name, [role.Code]));
    }

    private static async Task<IResult> CreateGuardianLinkAsync(LmsDbContext db, ITenantContext tenantContext, GuardianLinkRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId) return Results.BadRequest(new { message = "A tenant is required." });
        if (request.GuardianUserId == request.LearnerUserId) return Results.ValidationProblem(new Dictionary<string, string[]> { ["learnerUserId"] = ["Guardian and learner must be different users."] });
        var validUsers = await db.TenantMemberships.AnyAsync(item => item.TenantId == tenantId && item.UserId == request.GuardianUserId && item.Status == MembershipStatus.Active, cancellationToken)
            && await db.TenantMemberships.AnyAsync(item => item.TenantId == tenantId && item.UserId == request.LearnerUserId && item.Status == MembershipStatus.Active, cancellationToken);
        if (!validUsers) return Results.NotFound(new { message = "Both users must be active members of the tenant." });
        if (await db.GuardianLearners.AnyAsync(item => item.GuardianUserId == request.GuardianUserId && item.LearnerUserId == request.LearnerUserId, cancellationToken)) return Results.Conflict(new { message = "This guardian relationship already exists." });
        var link = new GuardianLearner { Id = Guid.NewGuid(), TenantId = tenantId, GuardianUserId = request.GuardianUserId, LearnerUserId = request.LearnerUserId, Relationship = request.Relationship.Trim(), CreatedAtUtc = DateTimeOffset.UtcNow };
        db.GuardianLearners.Add(link);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/guardian-links/{link.Id:D}", link);
    }

    public static async Task EnsureDefaultRolesForAllTenantsAsync(LmsDbContext db, CancellationToken cancellationToken)
    {
        var tenantIds = await db.Tenants.Select(item => item.Id).ToListAsync(cancellationToken);
        foreach (var tenantId in tenantIds) await EnsureDefaultRolesAsync(db, tenantId, cancellationToken);
    }

    public static async Task EnsureDefaultRolesAsync(LmsDbContext db, Guid tenantId, CancellationToken cancellationToken)
    {
        foreach (var definition in DefaultRoles)
        {
            var role = await db.Roles.IgnoreQueryFilters().SingleOrDefaultAsync(item => item.TenantId == tenantId && item.Code == definition.Code, cancellationToken);
            if (role is null)
            {
                role = new Role { Id = Guid.NewGuid(), TenantId = tenantId, Code = definition.Code, Name = definition.Name, IsSystemRole = true, CreatedAtUtc = DateTimeOffset.UtcNow };
                db.Roles.Add(role);
            }
            var existing = await db.RolePermissions.IgnoreQueryFilters().Where(item => item.RoleId == role.Id).Select(item => item.PermissionCode).ToListAsync(cancellationToken);
            db.RolePermissions.AddRange(definition.Permissions.Except(existing, StringComparer.OrdinalIgnoreCase).Select(permission => new RolePermission { RoleId = role.Id, PermissionCode = permission }));
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool HasProvisioningAccess(HttpContext httpContext, IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration["Platform:ProvisioningKey"])
        && string.Equals(configuration["Platform:ProvisioningKey"], httpContext.Request.Headers["X-Platform-Key"].FirstOrDefault(), StringComparison.Ordinal);

    private static string NormalizeEmail(string? value) => value?.Trim().ToUpperInvariant() ?? string.Empty;

    private static string SelectPrimaryRole(IEnumerable<string> roles)
    {
        var roleCodes = roles.ToArray();
        return roleCodes.FirstOrDefault(item => item.Equals("TENANT_ADMIN", StringComparison.OrdinalIgnoreCase)) ?? roleCodes.FirstOrDefault() ?? "USER";
    }

    private static bool IsEmail(string value)
    {
        try { return new MailAddress(value).Address.Equals(value, StringComparison.OrdinalIgnoreCase); }
        catch (FormatException) { return false; }
    }

    private sealed record DefaultRole(string Code, string Name, string[] Permissions);
}

public sealed record LoginRequest(string TenantSlug, string Email, string Password);
public sealed record RefreshRequest(string TenantSlug, string RefreshToken);
public sealed record AuthSessionResponse(string AccessToken, DateTimeOffset ExpiresAtUtc, string RefreshToken, TenantAuthResponse Tenant, UserAuthResponse User, string Role, string[] Roles, string[] Permissions);
public sealed record TenantAuthResponse(Guid Id, string Slug, string Name);
public sealed record UserAuthResponse(Guid Id, string Email, string DisplayName);
public sealed record BootstrapAdminRequest(string Email, string DisplayName, string Password);
public sealed record CreateTenantUserRequest(string Email, string DisplayName, string Password, string RoleCode, string? ProfileType = null, DateOnly? DateOfBirthAd = null, string? StudentNumber = null, string? GradeLevel = null, string? EmployeeNumber = null, string? SubjectSpecialty = null, string? PhoneNumber = null);
public sealed record GuardianLinkRequest(Guid GuardianUserId, Guid LearnerUserId, string Relationship);
public sealed record CreateRoleRequest(string Code, string Name, string[]? Permissions);
public sealed record UpdateRoleRequest(string Code, string Name, string[]? Permissions);
public sealed record AssignRoleRequest(Guid RoleId);
public sealed record UserResponse(Guid Id, string Email, string DisplayName, string UserStatus, string MembershipStatus, string RoleCode, string RoleName, string[]? RoleCodes = null);
public sealed record RoleResponse(Guid Id, string Code, string Name, bool IsSystemRole, string[] Permissions);
