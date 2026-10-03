namespace Lms.Api.Domain.Identity;

public enum UserStatus
{
    Invited = 1,
    Active = 2,
    Suspended = 3,
    Deactivated = 4
}

public enum MembershipStatus
{
    Pending = 1,
    Active = 2,
    Suspended = 3,
    Revoked = 4
}

public sealed class AppUser
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string NormalizedEmail { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public UserStatus Status { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public ICollection<TenantMembership> TenantMemberships { get; } = [];
    public ICollection<RefreshSession> RefreshSessions { get; } = [];
}

public sealed class RefreshSession
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? LastUsedAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public string? CreatedIp { get; set; }
    public string? UserAgent { get; set; }
    public AppUser User { get; set; } = null!;
}

public sealed class TenantMembership
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
    public MembershipStatus Status { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public AppUser User { get; set; } = null!;
    public Role Role { get; set; } = null!;
}

public sealed class Role
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsSystemRole { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public ICollection<RolePermission> Permissions { get; } = [];
    public ICollection<TenantMembership> Memberships { get; } = [];
}

public sealed class RolePermission
{
    public Guid RoleId { get; set; }
    public string PermissionCode { get; set; } = string.Empty;
    public Role Role { get; set; } = null!;
}

public sealed class LearnerProfile
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public DateOnly? DateOfBirthAd { get; set; }
    public string? StudentNumber { get; set; }
    public string? GradeLevel { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class TeacherProfile
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string? EmployeeNumber { get; set; }
    public string? SubjectSpecialty { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class GuardianProfile
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string? PhoneNumber { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class GuardianLearner
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid GuardianUserId { get; set; }
    public Guid LearnerUserId { get; set; }
    public string Relationship { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public static class LmsPermissions
{
    public const string TenantRead = "tenant.read";
    public const string TenantManage = "tenant.manage";
    public const string UserRead = "user.read";
    public const string UserManage = "user.manage";
    public const string RoleRead = "role.read";
    public const string RoleManage = "role.manage";
    public const string LearnerRead = "learner.read";
    public const string LearnerManage = "learner.manage";
    public const string TeacherRead = "teacher.read";
    public const string TeacherManage = "teacher.manage";
    public const string GuardianRead = "guardian.read";
    public const string GuardianManage = "guardian.manage";
    public const string CourseRead = "course.read";
    public const string CourseManage = "course.manage";
    public const string CourseReview = "course.review";
    public const string CoursePublish = "course.publish";
    public const string EnrollmentRead = "enrollment.read";
    public const string EnrollmentManage = "enrollment.manage";
    public const string ProgressRead = "progress.read";
    public const string ProgressManage = "progress.manage";
    public const string AssessmentRead = "assessment.read";
    public const string AssessmentManage = "assessment.manage";
    public const string AssessmentAttempt = "assessment.attempt";
    public const string GradeRead = "grade.read";
    public const string GradeManage = "grade.manage";
    public const string NotificationRead = "notification.read";
    public const string NotificationManage = "notification.manage";
    public const string CertificateRead = "certificate.read";
    public const string CertificateManage = "certificate.manage";
    public const string ReportRead = "report.read";
    public const string ReportExport = "report.export";
    public const string AiRead = "ai.read";
    public const string AiManage = "ai.manage";
    public const string AiUse = "ai.use";
    public const string LiveClassRead = "liveclass.read";
    public const string LiveClassManage = "liveclass.manage";
    public const string AttendanceRead = "attendance.read";
    public const string AttendanceManage = "attendance.manage";
    public const string CollaborationRead = "collaboration.read";
    public const string CollaborationManage = "collaboration.manage";
    public const string SecurityRead = "security.read";
    public const string SecurityManage = "security.manage";
    public const string RecommendationRead = "recommendation.read";
    public const string GamificationRead = "gamification.read";
    public const string GamificationManage = "gamification.manage";
    public const string VirtualLabRead = "virtuallab.read";
    public const string VirtualLabManage = "virtuallab.manage";
    public const string ForumModerate = "forum.moderate";
    public const string AnnouncementManage = "announcement.manage";

    public static readonly string[] All =
    [
        TenantRead, TenantManage, UserRead, UserManage, RoleRead, RoleManage,
        LearnerRead, LearnerManage, TeacherRead, TeacherManage,
        GuardianRead, GuardianManage, CourseRead, CourseManage, CourseReview, CoursePublish,
        EnrollmentRead, EnrollmentManage, ProgressRead, ProgressManage,
        AssessmentRead, AssessmentManage, AssessmentAttempt, GradeRead, GradeManage,
        NotificationRead, NotificationManage, CertificateRead, CertificateManage,
        ReportRead, ReportExport, AiRead, AiManage, AiUse,
        LiveClassRead, LiveClassManage, AttendanceRead, AttendanceManage,
        CollaborationRead, CollaborationManage, SecurityRead, SecurityManage,
        RecommendationRead, GamificationRead, GamificationManage, VirtualLabRead, VirtualLabManage,
        ForumModerate, AnnouncementManage
    ];
}
