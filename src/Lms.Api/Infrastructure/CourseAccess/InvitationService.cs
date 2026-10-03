using System.Security.Cryptography;
using System.Text;
using Lms.Api.Domain.Courses;
using Lms.Api.Domain.Identity;
using Lms.Api.Domain.Learning;
using Lms.Api.Infrastructure.Email;
using Lms.Api.Infrastructure.Notifications;
using Lms.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Infrastructure.CourseAccess;

public enum InviteOutcome { Created, AlreadyEnrolled }

public sealed record InviteResult(InviteOutcome Outcome, CourseInvitation? Invitation, string? Token, bool HasAccount = false);

/// <summary>What happened to the invitation email: Sent, Failed (see the error), or NotConfigured (the organization has email off).</summary>
public sealed record InviteEmailResult(string Status, string? Error);

/// <summary>Creates course invitations and turns accepted ones into enrollments.</summary>
public sealed class InvitationService(NotificationService notifications, EnrollmentService enrollments, EmailService email)
{
    public const int DefaultDays = 14;

    public static string NormaliseEmail(string email) => email.Trim().ToLowerInvariant();

    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Only this hash is stored, so a leaked database cannot be used to accept invitations.</summary>
    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>
    /// Invites an address to a course. Any earlier pending invitation for the same course and address is revoked first,
    /// so sending again is a "resend" with a fresh token and a fresh expiry. Saves its own changes.
    /// </summary>
    public async Task<InviteResult> InviteAsync(LmsDbContext db, Guid tenantId, Course course, string email, Guid invitedByUserId, string inviterName, string? message, int days, CancellationToken cancellationToken)
    {
        var address = NormaliseEmail(email);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.NormalizedEmail == address.ToUpperInvariant(), cancellationToken);
        if (user is not null && await db.Enrollments.AnyAsync(item => item.CourseId == course.Id && item.LearnerUserId == user.Id
            && (item.Status == EnrollmentStatus.Active || item.Status == EnrollmentStatus.Completed || item.Status == EnrollmentStatus.Waitlisted), cancellationToken))
            return new InviteResult(InviteOutcome.AlreadyEnrolled, null, null);

        var now = DateTimeOffset.UtcNow;
        foreach (var earlier in await db.CourseInvitations.Where(item => item.CourseId == course.Id && item.Email == address && item.Status == InvitationStatus.Pending).ToListAsync(cancellationToken))
        { earlier.Status = InvitationStatus.Revoked; earlier.RespondedAtUtc = now; }

        var token = NewToken();
        var invitation = new CourseInvitation
        {
            Id = Guid.NewGuid(), TenantId = tenantId, CourseId = course.Id, Email = address, TokenHash = HashToken(token), Status = InvitationStatus.Pending,
            Message = string.IsNullOrWhiteSpace(message) ? null : message.Trim(), InvitedByUserId = invitedByUserId, CreatedAtUtc = now, ExpiresAtUtc = now.AddDays(days)
        };
        db.CourseInvitations.Add(invitation);

        // People with an account hear about it in the app (and by email when the organization has email on). The token is never put in a notification.
        var isMember = user is not null && await db.TenantMemberships.AnyAsync(item => item.UserId == user.Id && item.Status == MembershipStatus.Active, cancellationToken);
        if (isMember)
            await notifications.QueueAsync(db, tenantId, user!.Id, "COURSE_INVITATION", new Dictionary<string, string> { ["CourseTitle"] = course.Title, ["InviterName"] = inviterName }, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return new InviteResult(InviteOutcome.Created, invitation, token, isMember);
    }

    /// <summary>The link in the email. The token is in the fragment so it never reaches server logs or Referer headers.</summary>
    public static string? BuildLink(string? webBaseUrl, string tenantSlug, string token)
        => string.IsNullOrWhiteSpace(webBaseUrl) ? null : $"{webBaseUrl.TrimEnd('/')}/#invite={Uri.EscapeDataString(token)}&tenant={Uri.EscapeDataString(tenantSlug)}";

    /// <summary>
    /// Emails the invitation, with its code, to a person who has no account yet. This goes out directly rather than through the
    /// notification queue so the code is never stored in clear text. A failure never fails the invitation; staff can still share the code.
    /// </summary>
    public async Task<InviteEmailResult> SendInvitationEmailAsync(LmsDbContext db, CourseInvitation invitation, Course course, string token, string inviterName, string? link, CancellationToken cancellationToken)
    {
        var settings = await db.EmailSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (settings is not { Enabled: true }) return new InviteEmailResult("NotConfigured", null);
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(item => item.Id == invitation.TenantId, cancellationToken);
        var lines = new List<string> { $"{inviterName} invited you to join \"{course.Title}\" at {tenant.Name}." };
        if (!string.IsNullOrWhiteSpace(invitation.Message)) lines.Add(invitation.Message!);
        lines.Add(link is null
            ? $"To join, open the {tenant.Name} learning site, choose \"Join with an invitation\" and enter this code:"
            : $"Create your account and join the course here:{Environment.NewLine}{link}{Environment.NewLine}{Environment.NewLine}Or enter this invitation code on the sign-in page:");
        lines.Add(token);
        lines.Add($"The invitation expires on {invitation.ExpiresAtUtc:yyyy-MM-dd}. If you were not expecting it, you can ignore this message.");
        try
        {
            await email.SendAsync(settings, new OutgoingEmail(invitation.Email, EmailText.SingleLine($"You are invited to {course.Title}"), string.Join(Environment.NewLine + Environment.NewLine, lines)), cancellationToken);
            invitation.EmailSentAtUtc = DateTimeOffset.UtcNow;
            invitation.EmailError = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            invitation.EmailSentAtUtc = null;
            invitation.EmailError = exception.Message.Length > 300 ? exception.Message[..300] : exception.Message;
        }
        await db.SaveChangesAsync(cancellationToken);
        return new InviteEmailResult(invitation.EmailSentAtUtc is null ? "Failed" : "Sent", invitation.EmailError);
    }

    /// <summary>The still-usable invitation for a token, or null. Wrong, used, revoked and expired tokens are indistinguishable to the caller.</summary>
    public static Task<CourseInvitation?> FindOpenAsync(LmsDbContext db, string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200) return Task.FromResult<CourseInvitation?>(null);
        var hash = HashToken(token.Trim());
        var now = DateTimeOffset.UtcNow;
        return db.CourseInvitations.SingleOrDefaultAsync(item => item.TokenHash == hash && item.Status == InvitationStatus.Pending && item.ExpiresAtUtc > now, cancellationToken);
    }

    public static bool IsExpired(CourseInvitation invitation, DateTimeOffset now) => invitation.Status == InvitationStatus.Pending && invitation.ExpiresAtUtc <= now;

    /// <summary>The status to show: a pending invitation past its expiry reads as Expired.</summary>
    public static string DisplayStatus(CourseInvitation invitation, DateTimeOffset now) => IsExpired(invitation, now) ? "Expired" : invitation.Status.ToString();

    /// <summary>
    /// Accepts an invitation on behalf of the signed-in person. The caller has already checked that the invitation is addressed to them.
    /// Prerequisites still apply; if they are not met the invitation stays open so it can be accepted later.
    /// </summary>
    public async Task<(EnrollResult Result, bool Accepted)> AcceptAsync(LmsDbContext db, Guid tenantId, CourseInvitation invitation, Course course, Guid learnerUserId, CancellationToken cancellationToken)
    {
        var result = await enrollments.EnrollAsync(db, tenantId, course, learnerUserId, EnrollmentSource.Invitation, checkPrerequisites: true, enforceDates: true, cancellationToken);
        var accepted = result.Outcome is EnrollOutcome.Enrolled or EnrollOutcome.Waitlisted or EnrollOutcome.AlreadyEnrolled;
        if (accepted)
        {
            invitation.Status = InvitationStatus.Accepted; invitation.RespondedAtUtc = DateTimeOffset.UtcNow; invitation.AcceptedByUserId = learnerUserId;
            await db.SaveChangesAsync(cancellationToken);
        }
        return (result, accepted);
    }
}
