using System.Net.Mail;
using System.Security.Claims;
using Lms.Api.Domain.Courses;
using Lms.Api.Domain.Identity;
using Lms.Api.Domain.Learning;
using Lms.Api.Infrastructure.CourseAccess;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Security;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Invitations;

/// <summary>
/// Course invitations. Staff send them to an email address; the person accepts while signed in as that address,
/// either from their invitation list or with the one-time token the inviter received. People without an account get the code by email
/// and can create their account with it (the public endpoints below), which also enrolls them.
/// </summary>
public static class InvitationEndpoints
{
    public static void MapInvitationEndpoints(this WebApplication app)
    {
        var tenant = app.MapGroup("/api/v1/tenant/invitations").RequireAuthorization("tenant.authenticated");
        tenant.AddEndpointFilter(async (context, next) =>
        {
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (!tenantContext.IsResolved)
                return Results.BadRequest(new { message = "A tenant is required. Use the X-Tenant-Slug header or a tenant subdomain." });
            return await next(context);
        });

        tenant.MapPost("", CreateAsync).RequireAuthorization("tenant.enrollment.manage");
        tenant.MapGet("", ListAsync).RequireAuthorization("tenant.enrollment.manage");
        tenant.MapPost("/{invitationId:guid}/revoke", RevokeAsync).RequireAuthorization("tenant.enrollment.manage");

        tenant.MapGet("/mine", MineAsync).RequireAuthorization("tenant.enrollment.read");
        tenant.MapPost("/{invitationId:guid}/accept", AcceptByIdAsync).RequireAuthorization("tenant.enrollment.read");
        tenant.MapPost("/{invitationId:guid}/decline", DeclineAsync).RequireAuthorization("tenant.enrollment.read");
        tenant.MapPost("/accept", AcceptByTokenAsync).RequireAuthorization("tenant.enrollment.read");

        // Anonymous: the invitation code is the credential. Same tenant requirement; rate limited like sign-in.
        var open = app.MapGroup("/api/v1/tenant/invitations/public").AllowAnonymous();
        open.AddEndpointFilter(async (context, next) =>
        {
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (!tenantContext.IsResolved)
                return Results.BadRequest(new { message = "A tenant is required. Use the X-Tenant-Slug header or a tenant subdomain." });
            return await next(context);
        });
        open.MapPost("/lookup", LookupAsync);
        open.MapPost("/register", RegisterAsync);
    }

    // ---------- staff ----------
    private static async Task<IResult> CreateAsync(HttpContext httpContext, ITenantContext tenantContext, LmsDbContext db, InvitationService invitations, IConfiguration configuration, CreateInvitationRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        if (!IsEmail(request.Email)) return Problem("Enter a valid email address.");
        var days = request.ExpiresInDays ?? InvitationService.DefaultDays;
        if (days is < 1 or > 90) return Problem("An invitation can last 1 to 90 days.");
        if (request.Message is { Length: > 1000 }) return Problem("The message must be 1000 characters or fewer.");
        var course = await db.Courses.AsNoTracking().SingleOrDefaultAsync(item => item.Id == request.CourseId && item.Status == CourseStatus.Published, cancellationToken);
        if (course is null) return Results.NotFound(new { message = "A published course was not found." });

        var inviter = httpContext.User.FindFirstValue(ClaimTypes.Name) ?? "A teacher";
        var result = await invitations.InviteAsync(db, tenantId, course, request.Email!, userId, inviter, request.Message, days, cancellationToken);
        if (result.Outcome == InviteOutcome.AlreadyEnrolled) return Results.Conflict(new { message = "That person is already enrolled in, or waiting for, this course." });
        // People without an account get the code and a sign-up link by email. People with one are told in the app instead.
        var link = InvitationService.BuildLink(WebBaseUrl(httpContext, configuration), tenantContext.TenantSlug ?? string.Empty, result.Token!);
        var emailResult = result.HasAccount
            ? new InviteEmailResult("NotNeeded", null)
            : await invitations.SendInvitationEmailAsync(db, result.Invitation!, course, result.Token!, inviter, link, cancellationToken);
        // The token is shown once, here. Only its hash is stored.
        return Results.Created($"/api/v1/tenant/invitations/{result.Invitation!.Id}", new CreatedInvitation(result.Invitation.Id, result.Token!, result.Invitation.ExpiresAtUtc, link, result.HasAccount, emailResult.Status, emailResult.Error));
    }

    private static async Task<IResult> ListAsync(LmsDbContext db, Guid? courseId, string? status, CancellationToken cancellationToken)
    {
        var query = db.CourseInvitations.AsNoTracking().AsQueryable();
        if (courseId is Guid selected) query = query.Where(item => item.CourseId == selected);
        var rows = await query.OrderByDescending(item => item.CreatedAtUtc).Take(300).ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(status)) rows = rows.Where(item => InvitationService.DisplayStatus(item, now).Equals(status, StringComparison.OrdinalIgnoreCase)).ToList();
        var courseIds = rows.Select(item => item.CourseId).Distinct().ToList();
        var titles = await db.Courses.AsNoTracking().Where(item => courseIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, item => item.Title, cancellationToken);
        var inviterIds = rows.Select(item => item.InvitedByUserId).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(item => inviterIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, item => item.DisplayName, cancellationToken);
        return Results.Ok(rows.Select(item => new InvitationResponse(item.Id, item.CourseId, titles.GetValueOrDefault(item.CourseId, "Course"), item.Email, InvitationService.DisplayStatus(item, now),
            names.GetValueOrDefault(item.InvitedByUserId, "Unknown"), item.Message, item.CreatedAtUtc, item.ExpiresAtUtc, item.RespondedAtUtc,
            item.EmailSentAtUtc is not null ? "Sent" : item.EmailError is not null ? "Failed" : null)));
    }

    private static async Task<IResult> RevokeAsync(Guid invitationId, LmsDbContext db, CancellationToken cancellationToken)
    {
        var invitation = await db.CourseInvitations.SingleOrDefaultAsync(item => item.Id == invitationId, cancellationToken);
        if (invitation is null) return Results.NotFound();
        if (invitation.Status != InvitationStatus.Pending) return Results.Conflict(new { message = $"This invitation is already {invitation.Status.ToString().ToLowerInvariant()}." });
        invitation.Status = InvitationStatus.Revoked; invitation.RespondedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    // ---------- the invited person ----------
    private static async Task<IResult> MineAsync(HttpContext httpContext, LmsDbContext db, CancellationToken cancellationToken)
    {
        if (MyEmail(httpContext) is not string email) return Results.Ok(Array.Empty<MyInvitation>());
        var now = DateTimeOffset.UtcNow;
        var rows = await db.CourseInvitations.AsNoTracking().Where(item => item.Email == email && item.Status == InvitationStatus.Pending && item.ExpiresAtUtc > now)
            .OrderByDescending(item => item.CreatedAtUtc).ToListAsync(cancellationToken);
        var courseIds = rows.Select(item => item.CourseId).Distinct().ToList();
        var courses = await db.Courses.AsNoTracking().Where(item => courseIds.Contains(item.Id) && item.Status == CourseStatus.Published).ToDictionaryAsync(item => item.Id, cancellationToken);
        var inviterIds = rows.Select(item => item.InvitedByUserId).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(item => inviterIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, item => item.DisplayName, cancellationToken);
        return Results.Ok(rows.Where(item => courses.ContainsKey(item.CourseId)).Select(item => new MyInvitation(item.Id, item.CourseId, courses[item.CourseId].Code, courses[item.CourseId].Title,
            names.GetValueOrDefault(item.InvitedByUserId, "Unknown"), item.Message, item.CreatedAtUtc, item.ExpiresAtUtc)));
    }

    private static async Task<IResult> AcceptByIdAsync(Guid invitationId, HttpContext httpContext, ITenantContext tenantContext, LmsDbContext db, InvitationService invitations, CancellationToken cancellationToken)
    {
        var invitation = await db.CourseInvitations.SingleOrDefaultAsync(item => item.Id == invitationId, cancellationToken);
        return await AcceptAsync(invitation, httpContext, tenantContext, db, invitations, cancellationToken);
    }

    private static async Task<IResult> AcceptByTokenAsync(HttpContext httpContext, ITenantContext tenantContext, LmsDbContext db, InvitationService invitations, AcceptTokenRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Token)) return Problem("Enter the invitation code.");
        var hash = InvitationService.HashToken(request.Token.Trim());
        var invitation = await db.CourseInvitations.SingleOrDefaultAsync(item => item.TokenHash == hash, cancellationToken);
        return await AcceptAsync(invitation, httpContext, tenantContext, db, invitations, cancellationToken);
    }

    private static async Task<IResult> AcceptAsync(CourseInvitation? invitation, HttpContext httpContext, ITenantContext tenantContext, LmsDbContext db, InvitationService invitations, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        // An invitation addressed to someone else looks exactly like one that does not exist.
        if (invitation is null || MyEmail(httpContext) != invitation.Email) return Results.NotFound(new { message = "No matching invitation was found for your account." });
        var now = DateTimeOffset.UtcNow;
        if (invitation.Status == InvitationStatus.Accepted) return Results.Conflict(new { message = "This invitation has already been accepted." });
        if (invitation.Status is InvitationStatus.Revoked or InvitationStatus.Declined) return Results.Conflict(new { message = "This invitation is no longer valid." });
        if (InvitationService.IsExpired(invitation, now)) return Results.Conflict(new { message = "This invitation has expired. Ask for a new one." });

        var course = await db.Courses.SingleOrDefaultAsync(item => item.Id == invitation.CourseId && item.Status == CourseStatus.Published, cancellationToken);
        if (course is null) return Results.Conflict(new { message = "This course is no longer open for enrollment." });

        var (result, accepted) = await invitations.AcceptAsync(db, tenantId, invitation, course, userId, cancellationToken);
        if (!accepted) return Results.Conflict(new { message = result.Message, missingPrerequisites = result.MissingPrerequisites });
        return Results.Ok(new AcceptedInvitation(result.Outcome.ToString(), course.Id, course.Title, result.Enrollment?.Id));
    }

    private static async Task<IResult> DeclineAsync(Guid invitationId, HttpContext httpContext, LmsDbContext db, CancellationToken cancellationToken)
    {
        var invitation = await db.CourseInvitations.SingleOrDefaultAsync(item => item.Id == invitationId, cancellationToken);
        if (invitation is null || MyEmail(httpContext) != invitation.Email) return Results.NotFound();
        if (invitation.Status != InvitationStatus.Pending) return Results.Conflict(new { message = "This invitation can no longer be declined." });
        invitation.Status = InvitationStatus.Declined; invitation.RespondedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    // ---------- people without an account ----------
    private static async Task<IResult> LookupAsync(LookupRequest request, ITenantContext tenantContext, LmsDbContext db, CancellationToken cancellationToken)
    {
        var invitation = await InvitationService.FindOpenAsync(db, request.Token, cancellationToken);
        var course = invitation is null ? null : await db.Courses.AsNoTracking().SingleOrDefaultAsync(item => item.Id == invitation.CourseId && item.Status == CourseStatus.Published, cancellationToken);
        if (invitation is null || course is null) return Results.NotFound(new { message = InvalidMessage });
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(item => item.Id == invitation.TenantId, cancellationToken);
        var inviter = await db.Users.AsNoTracking().Where(item => item.Id == invitation.InvitedByUserId).Select(item => item.DisplayName).SingleOrDefaultAsync(cancellationToken);
        var existing = await db.Users.AsNoTracking().Where(item => item.NormalizedEmail == invitation.Email.ToUpperInvariant()).Select(item => item.Id).SingleOrDefaultAsync(cancellationToken);
        var hasAccount = existing != Guid.Empty;
        // An account can exist because the person belongs to another organization; they then join this one with that account's password.
        var isMember = hasAccount && await db.TenantMemberships.AsNoTracking().AnyAsync(item => item.UserId == existing, cancellationToken);
        return Results.Ok(new InvitationPreview(tenant.Name, tenant.Slug, course.Title, invitation.Email, inviter ?? "A teacher", invitation.Message, invitation.ExpiresAtUtc, hasAccount, isMember));
    }

    private static async Task<IResult> RegisterAsync(RegisterRequest request, ITenantContext tenantContext, LmsDbContext db, PasswordService passwords, InvitationService invitations, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId) return Results.BadRequest(new { message = "A tenant is required." });
        var invitation = await InvitationService.FindOpenAsync(db, request.Token, cancellationToken);
        var course = invitation is null ? null : await db.Courses.SingleOrDefaultAsync(item => item.Id == invitation.CourseId && item.Status == CourseStatus.Published, cancellationToken);
        if (invitation is null || course is null) return Results.NotFound(new { message = InvalidMessage });

        // The address is the invited one, never something the caller supplies.
        var normalized = invitation.Email.ToUpperInvariant();
        var existingUser = await db.Users.SingleOrDefaultAsync(item => item.NormalizedEmail == normalized, cancellationToken);
        if (existingUser is not null) return await JoinWithExistingAccountAsync(existingUser, request.Password, tenantId, invitation, course, passwords, invitations, db, cancellationToken);

        var name = request.DisplayName?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200) return Results.ValidationProblem(new Dictionary<string, string[]> { ["displayName"] = ["Enter your name (200 characters or fewer)."] });
        try { PasswordService.Validate(request.Password ?? string.Empty); }
        catch (ArgumentException exception) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = [exception.Message] }); }

        var role = await db.Roles.SingleOrDefaultAsync(item => item.Code == "LEARNER", cancellationToken);
        if (role is null) return Results.Conflict(new { message = "This organization cannot accept sign-ups right now." });

        var now = DateTimeOffset.UtcNow;
        var user = new AppUser { Id = Guid.NewGuid(), Email = invitation.Email, NormalizedEmail = normalized, DisplayName = name, PasswordHash = passwords.Hash(request.Password!), Status = UserStatus.Active, CreatedAtUtc = now, UpdatedAtUtc = now };
        db.Users.Add(user);
        db.TenantMemberships.Add(new TenantMembership { Id = Guid.NewGuid(), TenantId = tenantId, UserId = user.Id, RoleId = role.Id, Status = MembershipStatus.Active, CreatedAtUtc = now, UpdatedAtUtc = now });
        db.LearnerProfiles.Add(new LearnerProfile { Id = Guid.NewGuid(), TenantId = tenantId, UserId = user.Id, CreatedAtUtc = now, UpdatedAtUtc = now });
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException)
        {
            // Someone registered the same address between the check and the save.
            return Results.Conflict(new { message = "An account already exists for this email address. Sign in to accept the invitation.", accountExists = true });
        }

        var (result, accepted) = await invitations.AcceptAsync(db, tenantId, invitation, course, user.Id, cancellationToken);
        // The account exists either way. If a prerequisite is missing the invitation stays open for after they have finished it.
        return Results.Created($"/api/v1/tenant/users/{user.Id:D}", new RegisteredViaInvitation(user.Email, course.Id, course.Title, accepted ? result.Outcome.ToString() : "NotEnrolled", accepted ? null : result.Message));
    }

    /// <summary>
    /// The address already has an account, because the person belongs to another organization. Knowing that account's password proves it is theirs
    /// (the code alone might have been passed along), and they become a learner here and are enrolled. A member already, they just sign in.
    /// </summary>
    private static async Task<IResult> JoinWithExistingAccountAsync(AppUser user, string? password, Guid tenantId, CourseInvitation invitation, Course course, PasswordService passwords, InvitationService invitations, LmsDbContext db, CancellationToken cancellationToken)
    {
        if (await db.TenantMemberships.AnyAsync(item => item.UserId == user.Id, cancellationToken))
            return Results.Conflict(new { message = "An account already exists for this email address. Sign in to accept the invitation.", accountExists = true });
        if (user.Status != UserStatus.Active || !passwords.Verify(password ?? string.Empty, user.PasswordHash))
            return Results.BadRequest(new { message = "That is not the password of the existing account for this email address.", needsExistingPassword = true });
        var role = await db.Roles.SingleOrDefaultAsync(item => item.Code == "LEARNER", cancellationToken);
        if (role is null) return Results.Conflict(new { message = "This organization cannot accept sign-ups right now." });

        var now = DateTimeOffset.UtcNow;
        db.TenantMemberships.Add(new TenantMembership { Id = Guid.NewGuid(), TenantId = tenantId, UserId = user.Id, RoleId = role.Id, Status = MembershipStatus.Active, CreatedAtUtc = now, UpdatedAtUtc = now });
        if (!await db.LearnerProfiles.AnyAsync(item => item.UserId == user.Id, cancellationToken))
            db.LearnerProfiles.Add(new LearnerProfile { Id = Guid.NewGuid(), TenantId = tenantId, UserId = user.Id, CreatedAtUtc = now, UpdatedAtUtc = now });
        await db.SaveChangesAsync(cancellationToken);

        var (result, accepted) = await invitations.AcceptAsync(db, tenantId, invitation, course, user.Id, cancellationToken);
        return Results.Created($"/api/v1/tenant/users/{user.Id:D}", new RegisteredViaInvitation(user.Email, course.Id, course.Title, accepted ? result.Outcome.ToString() : "NotEnrolled", accepted ? null : result.Message));
    }

    private const string InvalidMessage = "This invitation is not valid. It may have expired or already been used; ask for a new one.";

    /// <summary>Where the sign-up link points: the configured public address, else the browser origin of the staff member sending the invitation.</summary>
    private static string? WebBaseUrl(HttpContext context, IConfiguration configuration)
    {
        var configured = configuration["App:PublicUrl"];
        if (Uri.TryCreate(configured, UriKind.Absolute, out var set) && set.Scheme is "http" or "https") return set.ToString().TrimEnd('/');
        var origin = context.Request.Headers.Origin.FirstOrDefault();
        return Uri.TryCreate(origin, UriKind.Absolute, out var seen) && seen.Scheme is "http" or "https" ? seen.GetLeftPart(UriPartial.Authority) : null;
    }

    // ---------- helpers ----------
    private static string? MyEmail(HttpContext context)
        => context.User.FindFirstValue(ClaimTypes.Email) is { Length: > 0 } email ? InvitationService.NormaliseEmail(email) : null;

    private static bool IsEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { return new MailAddress(value.Trim()).Address.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase); }
        catch (FormatException) { return false; }
    }

    private static IResult Problem(string message) => Results.BadRequest(new { message });

    private static Guid? GetUserId(HttpContext context)
        => Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub"), out var id) ? id : null;
}

public sealed record CreateInvitationRequest(Guid CourseId, string? Email, string? Message, int? ExpiresInDays);
public sealed record AcceptTokenRequest(string? Token);
/// <summary>The token appears only in this response.</summary>
public sealed record CreatedInvitation(Guid Id, string Token, DateTimeOffset ExpiresAtUtc, string? Link, bool HasAccount, string EmailStatus, string? EmailError);
public sealed record LookupRequest(string? Token);
public sealed record RegisterRequest(string? Token, string? DisplayName, string? Password);
public sealed record InvitationPreview(string OrganizationName, string TenantSlug, string CourseTitle, string Email, string InvitedBy, string? Message, DateTimeOffset ExpiresAtUtc, bool HasAccount, bool IsMember = false);
public sealed record RegisteredViaInvitation(string Email, Guid CourseId, string CourseTitle, string Outcome, string? Message);
public sealed record InvitationResponse(Guid Id, Guid CourseId, string CourseTitle, string Email, string Status, string InvitedBy, string? Message, DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc, DateTimeOffset? RespondedAtUtc, string? EmailStatus = null);
public sealed record MyInvitation(Guid Id, Guid CourseId, string CourseCode, string CourseTitle, string InvitedBy, string? Message, DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc);
public sealed record AcceptedInvitation(string Outcome, Guid CourseId, string CourseTitle, Guid? EnrollmentId);
