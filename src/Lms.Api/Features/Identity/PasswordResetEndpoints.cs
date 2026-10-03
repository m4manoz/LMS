using System.Net.Mail;
using Lms.Api.Domain.Identity;
using Lms.Api.Infrastructure.CourseAccess;
using Lms.Api.Infrastructure.Email;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Security;
using Lms.Api.Infrastructure.Tenancy;
using Lms.Api.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Identity;

/// <summary>
/// Forgotten passwords. A reset code is emailed to the account's address; nothing is revealed about whether an account exists.
/// The code is single-use, expires quickly, is stored only as a hash, and signs the person out everywhere when used.
/// </summary>
public static class PasswordResetEndpoints
{
    public const int LifetimeMinutes = 60;
    private const string Accepted = "If that address belongs to an account in this organization, we have sent instructions to reset the password.";
    private const string Invalid = "This reset code is not valid. It may have expired or already been used; request a new one.";

    public static void MapPasswordResetEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/auth/password-reset").AllowAnonymous();
        group.MapPost("/request", RequestAsync);
        group.MapPost("/confirm", ConfirmAsync);
    }

    private static async Task<IResult> RequestAsync(RequestResetBody body, LmsDbContext db, ITenantContext tenantContext, EmailService email, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var slug = TenantSlug.Normalize(body.TenantSlug);
        if (!TenantSlug.IsValid(slug) || !IsEmail(body.Email)) return Results.BadRequest(new { message = "Enter your organization and email address." });

        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(item => item.Slug == slug && item.Status == TenantStatus.Active, cancellationToken);
        if (tenant is not null)
        {
            tenantContext.Set(tenant.Id, tenant.Slug);
            await IssueAsync(db, tenant, body.Email!, email, configuration, cancellationToken);
        }
        // The same answer whether or not the organization, the account or the email setup exists.
        return Results.Accepted(value: new { message = Accepted });
    }

    private static async Task IssueAsync(LmsDbContext db, Tenant tenant, string address, EmailService email, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var normalized = address.Trim().ToUpperInvariant();
        var user = await db.Users.SingleOrDefaultAsync(item => item.NormalizedEmail == normalized, cancellationToken);
        if (user is null || user.Status != UserStatus.Active
            || !await db.TenantMemberships.AnyAsync(item => item.UserId == user.Id && item.Status == MembershipStatus.Active, cancellationToken)) return;
        var settings = await db.EmailSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (settings is not { Enabled: true }) return;

        var now = DateTimeOffset.UtcNow;
        // One message a minute per person, so this cannot be used to flood someone's inbox.
        if (await db.PasswordResetTokens.AnyAsync(item => item.UserId == user.Id && item.UsedAtUtc == null && item.CreatedAtUtc > now.AddMinutes(-1), cancellationToken)) return;
        foreach (var earlier in await db.PasswordResetTokens.Where(item => item.UserId == user.Id && item.UsedAtUtc == null).ToListAsync(cancellationToken)) earlier.UsedAtUtc = now;

        var token = InvitationService.NewToken();
        db.PasswordResetTokens.Add(new PasswordResetToken { Id = Guid.NewGuid(), TenantId = tenant.Id, UserId = user.Id, TokenHash = InvitationService.HashToken(token), CreatedAtUtc = now, ExpiresAtUtc = now.AddMinutes(LifetimeMinutes) });
        await db.SaveChangesAsync(cancellationToken);

        // The link only ever uses the configured public address. A request header would let anyone point a victim's reset link at their own site.
        var baseUrl = configuration["App:PublicUrl"];
        var link = Uri.TryCreate(baseUrl, UriKind.Absolute, out var set) && set.Scheme is "http" or "https"
            ? $"{set.ToString().TrimEnd('/')}/#reset={Uri.EscapeDataString(token)}&tenant={Uri.EscapeDataString(tenant.Slug)}"
            : null;
        var lines = new List<string> { $"Someone asked to reset the password for your {tenant.Name} account." };
        lines.Add(link is null ? "Open the sign-in page, choose \"Forgot your password?\" and enter this code:" : $"Choose a new password here:{Environment.NewLine}{link}{Environment.NewLine}{Environment.NewLine}Or enter this code on the reset page:");
        lines.Add(token);
        lines.Add($"It works once and expires in {LifetimeMinutes} minutes. If you did not ask for this, ignore this message; your password has not changed.");
        try { await email.SendAsync(settings, new OutgoingEmail(user.Email, EmailText.SingleLine($"Reset your {tenant.Name} password"), string.Join(Environment.NewLine + Environment.NewLine, lines)), cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException) { /* the person simply asks again; nothing about the failure is shown to an anonymous caller */ }
    }

    private static async Task<IResult> ConfirmAsync(ConfirmResetBody body, LmsDbContext db, ITenantContext tenantContext, PasswordService passwords, CancellationToken cancellationToken)
    {
        var slug = TenantSlug.Normalize(body.TenantSlug);
        var tenant = TenantSlug.IsValid(slug) ? await db.Tenants.AsNoTracking().SingleOrDefaultAsync(item => item.Slug == slug && item.Status == TenantStatus.Active, cancellationToken) : null;
        if (tenant is null || string.IsNullOrWhiteSpace(body.Token) || body.Token.Length > 200) return Results.BadRequest(new { message = Invalid });
        tenantContext.Set(tenant.Id, tenant.Slug);

        var hash = InvitationService.HashToken(body.Token.Trim());
        var now = DateTimeOffset.UtcNow;
        var reset = await db.PasswordResetTokens.SingleOrDefaultAsync(item => item.TokenHash == hash && item.UsedAtUtc == null && item.ExpiresAtUtc > now, cancellationToken);
        if (reset is null) return Results.BadRequest(new { message = Invalid });
        try { PasswordService.Validate(body.NewPassword ?? string.Empty); }
        catch (ArgumentException exception) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["newPassword"] = [exception.Message] }); }

        var user = await db.Users.SingleOrDefaultAsync(item => item.Id == reset.UserId, cancellationToken);
        if (user is null || user.Status != UserStatus.Active) return Results.BadRequest(new { message = Invalid });
        user.PasswordHash = passwords.Hash(body.NewPassword!);
        user.UpdatedAtUtc = now;
        foreach (var other in await db.PasswordResetTokens.Where(item => item.UserId == user.Id && item.UsedAtUtc == null).ToListAsync(cancellationToken)) other.UsedAtUtc = now;
        // Whoever held the old password, or a stolen session, is signed out in every organization.
        foreach (var session in await db.RefreshSessions.IgnoreQueryFilters().Where(item => item.UserId == user.Id && item.RevokedAtUtc == null).ToListAsync(cancellationToken)) session.RevokedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static bool IsEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { return new MailAddress(value.Trim()).Address.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase); }
        catch (FormatException) { return false; }
    }
}

public sealed record RequestResetBody(string? TenantSlug, string? Email);
public sealed record ConfirmResetBody(string? TenantSlug, string? Token, string? NewPassword);
