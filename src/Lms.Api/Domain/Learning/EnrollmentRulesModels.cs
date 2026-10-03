namespace Lms.Api.Domain.Learning;

/// <summary>A learner must have completed <see cref="RequiredCourseId"/> before enrolling in <see cref="CourseId"/>.</summary>
public sealed class CoursePrerequisite
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CourseId { get; set; }
    public Guid RequiredCourseId { get; set; }
}

/// <summary>
/// When a module opens for a learner. All conditions that are set must be met:
/// a calendar date, a number of days after the learner's enrollment, and completion of an earlier module.
/// </summary>
public sealed class ModuleAccessRule
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ModuleId { get; set; }
    public int? ReleaseAfterDays { get; set; }
    public DateTimeOffset? ReleaseOnUtc { get; set; }
    public Guid? RequiresModuleId { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>A named group of learners (a class or batch) that can be enrolled or invited together.</summary>
public sealed class Cohort
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateOnly? StartDateAd { get; set; }
    public DateOnly? EndDateAd { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class CohortMember
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CohortId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset AddedAtUtc { get; set; }
}

public enum InvitationStatus
{
    Pending = 1,
    Accepted = 2,
    Declined = 3,
    Revoked = 4
}

/// <summary>
/// An invitation to a course, addressed to an email address. Only a hash of the acceptance token is stored,
/// so the token can be shown once to the inviter and never recovered. Accepting requires being signed in as that address.
/// </summary>
public sealed class CourseInvitation
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CourseId { get; set; }
    /// <summary>Lower-cased email address the invitation is for.</summary>
    public string Email { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public InvitationStatus Status { get; set; }
    public string? Message { get; set; }
    public Guid InvitedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? RespondedAtUtc { get; set; }
    public Guid? AcceptedByUserId { get; set; }
    /// <summary>When the invitation email was handed to the mail provider; null if it was not sent.</summary>
    public DateTimeOffset? EmailSentAtUtc { get; set; }
    /// <summary>Why the last attempt to email the invitation failed.</summary>
    public string? EmailError { get; set; }
}
