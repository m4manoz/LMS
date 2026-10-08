namespace Lms.Api.Domain.Identity;

public sealed record PermissionEntry(string Code, string Module, string ModuleTitle, int ModuleOrder, string Label, string Description);

/// <summary>
/// Every permission grouped by the part of the product it belongs to, with a plain-language name and what it lets a person do.
/// The role screens show permissions by module from this list. Every permission in <see cref="LmsPermissions.All"/> must be here (a test checks it).
/// </summary>
public static class PermissionCatalog
{
    private static PermissionEntry E(string code, string module, string title, int order, string label, string description) => new(code, module, title, order, label, description);

    public static readonly PermissionEntry[] All =
    [
        E(LmsPermissions.TenantRead, "organization", "Organization and access", 1, "See organization settings", "Open the organization's name, calendar and other settings."),
        E(LmsPermissions.TenantManage, "organization", "Organization and access", 1, "Manage the organization", "Change organization settings, integrations (email, live classes, storage, AI), the landing page and invitations."),
        E(LmsPermissions.UserRead, "organization", "Organization and access", 1, "See users", "Browse the list of users and their roles."),
        E(LmsPermissions.UserManage, "organization", "Organization and access", 1, "Manage users", "Create users and change their details and status."),
        E(LmsPermissions.RoleRead, "organization", "Organization and access", 1, "See roles", "Browse roles and the permissions they grant."),
        E(LmsPermissions.RoleManage, "organization", "Organization and access", 1, "Manage roles", "Create roles and give or take away roles from users."),
        E(LmsPermissions.SecurityRead, "organization", "Organization and access", 1, "See the audit trail", "Read the security audit trail and sign-in history."),
        E(LmsPermissions.SecurityManage, "organization", "Organization and access", 1, "Manage security", "Change security settings and review access."),

        E(LmsPermissions.LearnerRead, "people", "People", 2, "See learners", "Browse learners and their profiles."),
        E(LmsPermissions.LearnerManage, "people", "People", 2, "Manage learners", "Edit learners' details and group them in cohorts."),
        E(LmsPermissions.TeacherRead, "people", "People", 2, "See teachers", "Browse teachers and their profiles."),
        E(LmsPermissions.TeacherManage, "people", "People", 2, "Manage teachers", "Edit teachers' details and assignments."),
        E(LmsPermissions.GuardianRead, "people", "People", 2, "See guardians", "See the guardians linked to learners."),
        E(LmsPermissions.GuardianManage, "people", "People", 2, "Manage guardians", "Link guardians to learners and edit their details."),

        E(LmsPermissions.CourseRead, "courses", "Courses and content", 3, "See courses", "Browse courses, their outlines and their content, and watch library videos of courses the person is in."),
        E(LmsPermissions.CourseManage, "courses", "Courses and content", 3, "Create and edit courses", "Create courses, build outlines and lessons, add content, manage categories and the video library, and start new versions."),
        E(LmsPermissions.CourseReview, "courses", "Courses and content", 3, "Submit courses for review", "Send a finished draft course for review."),
        E(LmsPermissions.CoursePublish, "courses", "Courses and content", 3, "Publish courses", "Publish a course that is in review, so learners can see it."),

        E(LmsPermissions.EnrollmentRead, "learning", "Enrollment and progress", 4, "See enrollments", "See who is enrolled in which course."),
        E(LmsPermissions.EnrollmentManage, "learning", "Enrollment and progress", 4, "Manage enrollments", "Enroll, suspend or withdraw learners, and decide applications and invitations."),
        E(LmsPermissions.ProgressRead, "learning", "Enrollment and progress", 4, "See progress", "See learners' progress through lessons and courses."),
        E(LmsPermissions.ProgressManage, "learning", "Enrollment and progress", 4, "Record own progress", "Mark lessons started and completed, and keep bookmarks and notes (given to learners)."),

        E(LmsPermissions.AssessmentRead, "assessments", "Assessments and grades", 5, "See assessments", "Open assessments, assignments and their results."),
        E(LmsPermissions.AssessmentManage, "assessments", "Assessments and grades", 5, "Create and mark assessments", "Create assessments, rubrics and assignments, and grade submissions."),
        E(LmsPermissions.AssessmentAttempt, "assessments", "Assessments and grades", 5, "Take assessments", "Start attempts and submit answers (given to learners)."),
        E(LmsPermissions.GradeRead, "assessments", "Assessments and grades", 5, "See grades", "See grades and the gradebook."),
        E(LmsPermissions.GradeManage, "assessments", "Assessments and grades", 5, "Manage grades", "Enter and change grades and the grading scheme."),

        E(LmsPermissions.LiveClassRead, "live", "Live classes and attendance", 6, "See and join live classes", "See scheduled classes and join them."),
        E(LmsPermissions.LiveClassManage, "live", "Live classes and attendance", 6, "Host live classes", "Schedule and close classes, let people in, mute others, and record."),
        E(LmsPermissions.AttendanceRead, "live", "Live classes and attendance", 6, "See attendance", "See who attended a class."),
        E(LmsPermissions.AttendanceManage, "live", "Live classes and attendance", 6, "Manage attendance", "Correct attendance records."),

        E(LmsPermissions.CollaborationRead, "community", "Community and messages", 7, "Read forums and messages", "Read forums, polls, chat and announcements."),
        E(LmsPermissions.CollaborationManage, "community", "Community and messages", 7, "Post and message", "Post in forums, send messages, take part in polls and raise a hand."),
        E(LmsPermissions.ForumModerate, "community", "Community and messages", 7, "Moderate forums", "Pin, lock, hide and delete forum threads and posts."),
        E(LmsPermissions.AnnouncementManage, "community", "Community and messages", 7, "Send announcements", "Publish announcements to the organization or a course."),

        E(LmsPermissions.NotificationRead, "notifications", "Notifications", 8, "See notifications", "Read the person's own notifications."),
        E(LmsPermissions.NotificationManage, "notifications", "Notifications", 8, "Manage notifications", "Change notification templates and send notices."),

        E(LmsPermissions.CertificateRead, "certificates", "Certificates", 9, "See certificates", "See issued certificates."),
        E(LmsPermissions.CertificateManage, "certificates", "Certificates", 9, "Manage certificates", "Design certificate templates and issue or revoke certificates."),

        E(LmsPermissions.ReportRead, "reports", "Reports", 10, "See reports", "Open reports and dashboards."),
        E(LmsPermissions.ReportExport, "reports", "Reports", 10, "Export reports", "Download report data."),

        E(LmsPermissions.AiRead, "ai", "AI assistance", 11, "See AI results", "See transcripts, summaries and other AI-made material."),
        E(LmsPermissions.AiManage, "ai", "AI assistance", 11, "Manage AI", "Set up AI services and generate and publish AI material."),
        E(LmsPermissions.AiUse, "ai", "AI assistance", 11, "Use AI tools", "Use AI study aids (given to learners)."),

        E(LmsPermissions.GamificationRead, "engagement", "Engagement", 12, "See points and badges", "See points, badges and leaderboards."),
        E(LmsPermissions.GamificationManage, "engagement", "Engagement", 12, "Manage points and badges", "Change how points and badges are earned."),
        E(LmsPermissions.RecommendationRead, "engagement", "Engagement", 12, "See recommendations", "See suggested courses."),

        E(LmsPermissions.VirtualLabRead, "labs", "Virtual labs", 13, "Use virtual labs", "Open virtual labs."),
        E(LmsPermissions.VirtualLabManage, "labs", "Virtual labs", 13, "Manage virtual labs", "Set up labs and review their results."),
    ];
}
