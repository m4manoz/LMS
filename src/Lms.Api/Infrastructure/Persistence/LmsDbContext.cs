using Lms.Api.Domain.Identity;
using Lms.Api.Domain.Courses;
using Lms.Api.Domain.Learning;
using Lms.Api.Domain.Assessments;
using Lms.Api.Domain.Notifications;
using Lms.Api.Domain.Certificates;
using Lms.Api.Domain.Community;
using Lms.Api.Domain.Gradebook;
using Lms.Api.Domain.Integrations;
using Lms.Api.Domain.Messaging;
using Lms.Api.Domain.Assignments;
using Lms.Api.Domain.AI;
using Lms.Api.Domain.LiveClasses;
using Lms.Api.Domain.Videos;
using Lms.Api.Domain.Landing;
using Lms.Api.Domain.Security;
using Lms.Api.Domain.Tenants;
using Lms.Api.Domain.Gamification;
using Lms.Api.Domain.VirtualLabs;
using Lms.Api.Domain.Recommendations;
using Lms.Api.Domain.Offline;
using Lms.Api.Domain.Telemetry;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Infrastructure.Persistence;

public sealed class LmsDbContext(
    DbContextOptions<LmsDbContext> options,
    ITenantContext tenantContext) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<RefreshSession> RefreshSessions => Set<RefreshSession>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<TenantMembership> TenantMemberships => Set<TenantMembership>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<LearnerProfile> LearnerProfiles => Set<LearnerProfile>();
    public DbSet<TeacherProfile> TeacherProfiles => Set<TeacherProfile>();
    public DbSet<GuardianProfile> GuardianProfiles => Set<GuardianProfile>();
    public DbSet<GuardianLearner> GuardianLearners => Set<GuardianLearner>();
    public DbSet<Course> Courses => Set<Course>();
    public DbSet<CourseVersion> CourseVersions => Set<CourseVersion>();
    public DbSet<CourseModule> CourseModules => Set<CourseModule>();
    public DbSet<CourseChapter> CourseChapters => Set<CourseChapter>();
    public DbSet<CourseLesson> CourseLessons => Set<CourseLesson>();
    public DbSet<CourseTopic> CourseTopics => Set<CourseTopic>();
    public DbSet<CourseActivity> CourseActivities => Set<CourseActivity>();
    public DbSet<CourseCategory> CourseCategories => Set<CourseCategory>();
    public DbSet<CourseTag> CourseTags => Set<CourseTag>();
    public DbSet<CourseTagLink> CourseTagLinks => Set<CourseTagLink>();
    public DbSet<ContentAsset> ContentAssets => Set<ContentAsset>();
    public DbSet<LessonBlock> LessonBlocks => Set<LessonBlock>();
    public DbSet<CourseWorkflowEvent> CourseWorkflowEvents => Set<CourseWorkflowEvent>();
    public DbSet<Enrollment> Enrollments => Set<Enrollment>();
    public DbSet<LessonProgress> LessonProgress => Set<LessonProgress>();
    public DbSet<LearningProgressEvent> LearningProgressEvents => Set<LearningProgressEvent>();
    public DbSet<CourseBookmark> CourseBookmarks => Set<CourseBookmark>();
    public DbSet<LearnerNote> LearnerNotes => Set<LearnerNote>();
    public DbSet<QuestionBank> QuestionBanks => Set<QuestionBank>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<Assessment> Assessments => Set<Assessment>();
    public DbSet<AssessmentQuestion> AssessmentQuestions => Set<AssessmentQuestion>();
    public DbSet<AssessmentAttempt> AssessmentAttempts => Set<AssessmentAttempt>();
    public DbSet<AssessmentAnswer> AssessmentAnswers => Set<AssessmentAnswer>();
    public DbSet<AssessmentPool> AssessmentPools => Set<AssessmentPool>();
    public DbSet<Rubric> Rubrics => Set<Rubric>();
    public DbSet<LearnerAccommodation> LearnerAccommodations => Set<LearnerAccommodation>();
    public DbSet<NotificationTemplate> NotificationTemplates => Set<NotificationTemplate>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();
    public DbSet<NotificationMessage> NotificationMessages => Set<NotificationMessage>();
    public DbSet<CertificateTemplate> CertificateTemplates => Set<CertificateTemplate>();
    public DbSet<Certificate> Certificates => Set<Certificate>();
    public DbSet<TranscriptEntry> TranscriptEntries => Set<TranscriptEntry>();
    public DbSet<AiJob> AiJobs => Set<AiJob>();
    public DbSet<AiOutput> AiOutputs => Set<AiOutput>();
    public DbSet<AiCitation> AiCitations => Set<AiCitation>();
    public DbSet<AiReview> AiReviews => Set<AiReview>();
    public DbSet<LiveClassSession> LiveClassSessions => Set<LiveClassSession>();
    public DbSet<SessionAttendance> SessionAttendances => Set<SessionAttendance>();
    public DbSet<SessionAnnouncement> SessionAnnouncements => Set<SessionAnnouncement>();
    public DbSet<Assignment> Assignments => Set<Assignment>();
    public DbSet<AssignmentSubmission> AssignmentSubmissions => Set<AssignmentSubmission>();
    public DbSet<AssignmentGroup> AssignmentGroups => Set<AssignmentGroup>();
    public DbSet<AssignmentGroupMember> AssignmentGroupMembers => Set<AssignmentGroupMember>();
    public DbSet<CoursePrerequisite> CoursePrerequisites => Set<CoursePrerequisite>();
    public DbSet<ModuleAccessRule> ModuleAccessRules => Set<ModuleAccessRule>();
    public DbSet<LiveClassSettings> LiveClassSettings => Set<LiveClassSettings>();
    public DbSet<Video> Videos => Set<Video>();
    public DbSet<VideoWatch> VideoWatches => Set<VideoWatch>();
    public DbSet<VideoAiSettings> VideoAiSettings => Set<VideoAiSettings>();
    public DbSet<VideoUpload> VideoUploads => Set<VideoUpload>();
    public DbSet<VideoChapter> VideoChapters => Set<VideoChapter>();
    public DbSet<VideoNote> VideoNotes => Set<VideoNote>();
    public DbSet<LandingPage> LandingPages => Set<LandingPage>();
    public DbSet<TenantDomain> TenantDomains => Set<TenantDomain>();
    public DbSet<LandingImage> LandingImages => Set<LandingImage>();
    public DbSet<CourseRating> CourseRatings => Set<CourseRating>();
    public DbSet<CourseApplication> CourseApplications => Set<CourseApplication>();
    public DbSet<VideoTranscript> VideoTranscripts => Set<VideoTranscript>();
    public DbSet<VideoTranscriptSegment> VideoTranscriptSegments => Set<VideoTranscriptSegment>();
    public DbSet<VideoInsight> VideoInsights => Set<VideoInsight>();
    public DbSet<Cohort> Cohorts => Set<Cohort>();
    public DbSet<CohortMember> CohortMembers => Set<CohortMember>();
    public DbSet<CourseInvitation> CourseInvitations => Set<CourseInvitation>();
    public DbSet<GradeScale> GradeScales => Set<GradeScale>();
    public DbSet<CourseGradingSettings> CourseGradingSettings => Set<CourseGradingSettings>();
    public DbSet<GradeCategory> GradeCategories => Set<GradeCategory>();
    public DbSet<GradeItemCategory> GradeItemCategories => Set<GradeItemCategory>();
    public DbSet<EmailSettings> EmailSettings => Set<EmailSettings>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationParticipant> ConversationParticipants => Set<ConversationParticipant>();
    public DbSet<ConversationMessage> ConversationMessages => Set<ConversationMessage>();
    public DbSet<ForumThread> ForumThreads => Set<ForumThread>();
    public DbSet<ForumReply> ForumReplies => Set<ForumReply>();
    public DbSet<ForumEdit> ForumEdits => Set<ForumEdit>();
    public DbSet<ForumAttachment> ForumAttachments => Set<ForumAttachment>();
    public DbSet<Announcement> Announcements => Set<Announcement>();
    public DbSet<LivePoll> LivePolls => Set<LivePoll>();
    public DbSet<LivePollResponse> LivePollResponses => Set<LivePollResponse>();
    public DbSet<SessionHandRaise> SessionHandRaises => Set<SessionHandRaise>();
    public DbSet<SessionJoinRequest> SessionJoinRequests => Set<SessionJoinRequest>();
    public DbSet<SessionChatMessage> SessionChatMessages => Set<SessionChatMessage>();
    public DbSet<SessionRecording> SessionRecordings => Set<SessionRecording>();
    public DbSet<SessionTrackRecording> SessionTrackRecordings => Set<SessionTrackRecording>();
    public DbSet<SessionConsent> SessionConsents => Set<SessionConsent>();
    public DbSet<SecurityAuditEvent> SecurityAuditEvents => Set<SecurityAuditEvent>();
    public DbSet<GamificationProfile> GamificationProfiles => Set<GamificationProfile>();
    public DbSet<GamificationSettings> GamificationSettings => Set<GamificationSettings>();
    public DbSet<GamificationEvent> GamificationEvents => Set<GamificationEvent>();
    public DbSet<BadgeDefinition> BadgeDefinitions => Set<BadgeDefinition>();
    public DbSet<UserBadge> UserBadges => Set<UserBadge>();
    public DbSet<VirtualLab> VirtualLabs => Set<VirtualLab>();
    public DbSet<VirtualLabResult> VirtualLabResults => Set<VirtualLabResult>();
    public DbSet<RecommendationDismissal> RecommendationDismissals => Set<RecommendationDismissal>();
    public DbSet<OfflineDevice> OfflineDevices => Set<OfflineDevice>();
    public DbSet<OfflinePackageLicense> OfflinePackageLicenses => Set<OfflinePackageLicense>();
    public DbSet<OfflineSyncConflict> OfflineSyncConflicts => Set<OfflineSyncConflict>();
    public DbSet<ProductTelemetryEvent> ProductTelemetryEvents => Set<ProductTelemetryEvent>();
    public DbSet<GamificationAbuseReview> GamificationAbuseReviews => Set<GamificationAbuseReview>();
    public DbSet<VirtualLabHealthCheck> VirtualLabHealthChecks => Set<VirtualLabHealthCheck>();
    public DbSet<VirtualLabWebhookEvent> VirtualLabWebhookEvents => Set<VirtualLabWebhookEvent>();
    public DbSet<LearningProgram> LearningPrograms => Set<LearningProgram>();
    public DbSet<Subject> Subjects => Set<Subject>();
    public DbSet<LearningPath> LearningPaths => Set<LearningPath>();
    public DbSet<LearningResource> LearningResources => Set<LearningResource>();
    public DbSet<LearningHistory> LearningHistories => Set<LearningHistory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.ToTable("tenants");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.Slug).IsUnique();
            entity.Property(item => item.Slug).HasMaxLength(63).IsRequired();
            entity.Property(item => item.Name).HasMaxLength(250).IsRequired();
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
        });

        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.NormalizedEmail).IsUnique();
            entity.Property(item => item.Email).HasMaxLength(320).IsRequired();
            entity.Property(item => item.NormalizedEmail).HasMaxLength(320).IsRequired();
            entity.Property(item => item.DisplayName).HasMaxLength(200).IsRequired();
            entity.Property(item => item.PasswordHash).HasMaxLength(500).IsRequired();
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
        });

        modelBuilder.Entity<Video>(entity =>
        {
            entity.ToTable("videos"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.CourseId, item.CreatedAtUtc });
            entity.Property(item => item.Title).HasMaxLength(250).IsRequired();
            entity.Property(item => item.Description).HasMaxLength(2000);
            entity.Property(item => item.Type).HasConversion<string>().HasMaxLength(20);
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(item => item.StatusMessage).HasMaxLength(500);
            entity.Property(item => item.ExternalUrl).HasMaxLength(2000);
            entity.Property(item => item.ContentType).HasMaxLength(100);
            entity.Property(item => item.HlsLayout).HasMaxLength(200);
            entity.Property(item => item.Tags).HasMaxLength(500).IsRequired();
            entity.HasOne<Course>().WithMany().HasForeignKey(item => item.CourseId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<VideoWatch>(entity =>
        {
            entity.ToTable("video_watches"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.VideoId, item.UserId }).IsUnique();
            entity.HasIndex(item => new { item.TenantId, item.UserId });
            entity.HasOne<Video>().WithMany().HasForeignKey(item => item.VideoId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<VideoChapter>(entity =>
        {
            entity.ToTable("video_chapters"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.VideoId, item.StartSeconds }).IsUnique();
            entity.Property(item => item.Title).HasMaxLength(120).IsRequired();
            entity.HasOne<Video>().WithMany().HasForeignKey(item => item.VideoId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<VideoNote>(entity =>
        {
            entity.ToTable("video_notes"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.VideoId, item.UserId, item.PositionSeconds });
            entity.Property(item => item.Text).HasMaxLength(1000).IsRequired();
            entity.HasOne<Video>().WithMany().HasForeignKey(item => item.VideoId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<VideoUpload>(entity =>
        {
            entity.ToTable("video_uploads"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.UserId });
            entity.HasIndex(item => item.UpdatedAtUtc);
            entity.Property(item => item.Title).HasMaxLength(250).IsRequired();
            entity.Property(item => item.Description).HasMaxLength(2000);
            entity.Property(item => item.FileName).HasMaxLength(255).IsRequired();
            entity.Property(item => item.ContentType).HasMaxLength(150).IsRequired();
            entity.Property(item => item.ChunkMap).HasMaxLength(20480).IsRequired();
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        // Addresses are looked up before any organization is known, so this table is not filtered by organization.
        modelBuilder.Entity<TenantDomain>(entity =>
        {
            entity.ToTable("tenant_domains"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.Host).IsUnique();
            entity.HasIndex(item => item.TenantId);
            entity.Property(item => item.Host).HasMaxLength(253).IsRequired();
            entity.HasOne<Lms.Api.Domain.Tenants.Tenant>().WithMany().HasForeignKey(item => item.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LandingPage>(entity =>
        {
            entity.ToTable("landing_pages"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.TenantId).IsUnique();
            entity.Property(item => item.ContentJson).IsRequired();
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<CourseApplication>(entity =>
        {
            entity.ToTable("course_applications"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.Status, item.CreatedAtUtc });
            entity.HasIndex(item => new { item.CourseId, item.Email });
            entity.Property(item => item.FullName).HasMaxLength(120).IsRequired();
            entity.Property(item => item.Email).HasMaxLength(200).IsRequired();
            entity.Property(item => item.Phone).HasMaxLength(40);
            entity.Property(item => item.Message).HasMaxLength(1000);
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(20);
            entity.HasOne<Course>().WithMany().HasForeignKey(item => item.CourseId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<VideoAiSettings>(entity =>
        {
            entity.ToTable("video_ai_settings"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.TenantId).IsUnique();
            entity.Property(item => item.Provider).HasMaxLength(30).IsRequired();
            entity.Property(item => item.BaseUrl).HasMaxLength(500);
            entity.Property(item => item.ApiKeyProtected).HasMaxLength(2000);
            entity.Property(item => item.TranscriptionModel).HasMaxLength(100);
            entity.Property(item => item.ChatModel).HasMaxLength(100);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<VideoTranscript>(entity =>
        {
            entity.ToTable("video_transcripts"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.VideoId).IsUnique();
            entity.HasIndex(item => new { item.Status, item.UpdatedAtUtc });
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(item => item.Source).HasMaxLength(20).IsRequired();
            entity.Property(item => item.Language).HasMaxLength(40);
            entity.Property(item => item.Provider).HasMaxLength(30);
            entity.Property(item => item.StatusMessage).HasMaxLength(500);
            entity.HasOne<Video>().WithMany().HasForeignKey(item => item.VideoId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<VideoTranscriptSegment>(entity =>
        {
            entity.ToTable("video_transcript_segments"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.VideoId, item.Index });
            entity.Property(item => item.Text).HasMaxLength(1000).IsRequired();
            entity.HasOne<Video>().WithMany().HasForeignKey(item => item.VideoId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<VideoInsight>(entity =>
        {
            entity.ToTable("video_insights"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.VideoId, item.Kind });
            entity.Property(item => item.Kind).HasConversion<string>().HasMaxLength(20);
            entity.Property(item => item.Content).IsRequired();
            entity.Property(item => item.Provider).HasMaxLength(30).IsRequired();
            entity.Property(item => item.Model).HasMaxLength(100);
            entity.HasOne<Video>().WithMany().HasForeignKey(item => item.VideoId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<LiveClassSettings>(entity =>
        {
            entity.ToTable("live_class_settings"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.TenantId).IsUnique();
            entity.Property(item => item.Provider).HasMaxLength(20).IsRequired();
            entity.Property(item => item.JitsiBaseUrl).HasMaxLength(500);
            entity.Property(item => item.LiveKitUrl).HasMaxLength(500);
            entity.Property(item => item.LiveKitApiKey).HasMaxLength(200);
            entity.Property(item => item.LiveKitSecretProtected).HasMaxLength(2000);
            entity.Property(item => item.LiveKitSecretReference).HasMaxLength(200);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<PasswordResetToken>(entity =>
        {
            entity.ToTable("password_reset_tokens");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.TokenHash }).IsUnique();
            entity.HasIndex(item => new { item.TenantId, item.UserId, item.UsedAtUtc });
            entity.Property(item => item.TokenHash).HasMaxLength(100).IsRequired();
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<RefreshSession>(entity =>
        {
            entity.ToTable("refresh_sessions");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.TokenHash }).IsUnique();
            entity.HasIndex(item => new { item.TenantId, item.UserId, item.RevokedAtUtc, item.ExpiresAtUtc });
            entity.Property(item => item.TokenHash).HasMaxLength(128).IsRequired();
            entity.Property(item => item.CreatedIp).HasMaxLength(64);
            entity.Property(item => item.UserAgent).HasMaxLength(500);
            entity.HasOne(item => item.User).WithMany(item => item.RefreshSessions).HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<TenantMembership>(entity =>
        {
            entity.ToTable("tenant_memberships");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.UserId, item.RoleId }).IsUnique();
            entity.HasIndex(item => new { item.TenantId, item.RoleId });
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
            entity.HasOne(item => item.User).WithMany(item => item.TenantMemberships).HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.Role).WithMany(item => item.Memberships).HasForeignKey(item => item.RoleId).OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<Role>(entity =>
        {
            entity.ToTable("roles");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.Code }).IsUnique();
            entity.Property(item => item.Code).HasMaxLength(80).IsRequired();
            entity.Property(item => item.Name).HasMaxLength(150).IsRequired();
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<RolePermission>(entity =>
        {
            entity.ToTable("role_permissions");
            entity.HasKey(item => new { item.RoleId, item.PermissionCode });
            entity.Property(item => item.PermissionCode).HasMaxLength(100).IsRequired();
            entity.HasOne(item => item.Role).WithMany(item => item.Permissions).HasForeignKey(item => item.RoleId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.Role.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<LearnerProfile>(entity =>
        {
            entity.ToTable("learner_profiles");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.UserId }).IsUnique();
            entity.Property(item => item.StudentNumber).HasMaxLength(80);
            entity.Property(item => item.GradeLevel).HasMaxLength(80);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<TeacherProfile>(entity =>
        {
            entity.ToTable("teacher_profiles");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.UserId }).IsUnique();
            entity.Property(item => item.EmployeeNumber).HasMaxLength(80);
            entity.Property(item => item.SubjectSpecialty).HasMaxLength(200);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<GuardianProfile>(entity =>
        {
            entity.ToTable("guardian_profiles");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.UserId }).IsUnique();
            entity.Property(item => item.PhoneNumber).HasMaxLength(40);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<GuardianLearner>(entity =>
        {
            entity.ToTable("guardian_learners");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.GuardianUserId, item.LearnerUserId }).IsUnique();
            entity.Property(item => item.Relationship).HasMaxLength(80).IsRequired();
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<Course>(entity =>
        {
            entity.ToTable("courses");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.Code }).IsUnique();
            entity.HasIndex(item => new { item.TenantId, item.Slug }).IsUnique();
            entity.Property(item => item.Code).HasMaxLength(80).IsRequired();
            entity.Property(item => item.Slug).HasMaxLength(160).IsRequired();
            entity.Property(item => item.Title).HasMaxLength(250).IsRequired();
            entity.Property(item => item.Description).HasMaxLength(4000);
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.Capacity);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<CourseVersion>(entity =>
        {
            entity.ToTable("course_versions");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.CourseId, item.VersionNumber }).IsUnique();
            entity.HasIndex(item => new { item.TenantId, item.CourseId, item.Status });
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.ChangeSummary).HasMaxLength(2000);
            entity.HasOne<Course>().WithMany().HasForeignKey(item => item.CourseId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<CourseModule>(entity =>
        {
            entity.ToTable("course_modules");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.CourseVersionId, item.DisplayOrder });
            entity.Property(item => item.Title).HasMaxLength(250).IsRequired();
            entity.Property(item => item.Description).HasMaxLength(2000);
            entity.HasOne<CourseVersion>().WithMany().HasForeignKey(item => item.CourseVersionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<CourseChapter>(entity =>
        {
            entity.ToTable("course_chapters");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.CourseModuleId, item.DisplayOrder });
            entity.Property(item => item.Title).HasMaxLength(250).IsRequired();
            entity.Property(item => item.Description).HasMaxLength(2000);
            entity.HasOne<CourseModule>().WithMany().HasForeignKey(item => item.CourseModuleId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<CourseLesson>(entity =>
        {
            entity.ToTable("course_lessons");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.CourseModuleId, item.DisplayOrder });
            entity.Property(item => item.Title).HasMaxLength(250).IsRequired();
            entity.Property(item => item.Summary).HasMaxLength(2000);
            entity.Property(item => item.ContentHtml).HasMaxLength(200000);
            entity.HasOne<CourseModule>().WithMany().HasForeignKey(item => item.CourseModuleId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<CourseTopic>(entity =>
        {
            entity.ToTable("course_topics");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.CourseLessonId, item.DisplayOrder });
            entity.Property(item => item.Title).HasMaxLength(250).IsRequired();
            entity.HasOne<CourseLesson>().WithMany().HasForeignKey(item => item.CourseLessonId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<CourseActivity>(entity =>
        {
            entity.ToTable("course_activities");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.CourseLessonId, item.DisplayOrder });
            entity.Property(item => item.ActivityType).HasMaxLength(80).IsRequired();
            entity.Property(item => item.Title).HasMaxLength(250).IsRequired();
            entity.Property(item => item.ConfigurationJson).HasMaxLength(200000);
            entity.HasOne<CourseLesson>().WithMany().HasForeignKey(item => item.CourseLessonId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<CourseCategory>(entity =>
        {
            entity.ToTable("course_categories");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.Slug }).IsUnique();
            entity.Property(item => item.Name).HasMaxLength(150).IsRequired();
            entity.Property(item => item.Slug).HasMaxLength(160).IsRequired();
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<CourseTag>(entity =>
        {
            entity.ToTable("course_tags");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.Slug }).IsUnique();
            entity.Property(item => item.Name).HasMaxLength(100).IsRequired();
            entity.Property(item => item.Slug).HasMaxLength(120).IsRequired();
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<CourseTagLink>(entity =>
        {
            entity.ToTable("course_tag_links");
            entity.HasKey(item => new { item.CourseId, item.TagId });
            entity.HasOne<Course>().WithMany().HasForeignKey(item => item.CourseId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<CourseTag>().WithMany().HasForeignKey(item => item.TagId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<ContentAsset>(entity =>
        {
            entity.ToTable("content_assets");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.CourseId });
            entity.HasIndex(item => new { item.TenantId, item.StorageKey }).IsUnique();
            entity.Property(item => item.OriginalFileName).HasMaxLength(255).IsRequired();
            entity.Property(item => item.StorageKey).HasMaxLength(500).IsRequired();
            entity.Property(item => item.ContentType).HasMaxLength(150).IsRequired();
            entity.Property(item => item.Sha256).HasMaxLength(64).IsRequired();
            entity.HasOne<Course>().WithMany().HasForeignKey(item => item.CourseId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<CourseVersion>().WithMany().HasForeignKey(item => item.CourseVersionId).OnDelete(DeleteBehavior.SetNull);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<CourseWorkflowEvent>(entity =>
        {
            entity.ToTable("course_workflow_events");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.CourseId, item.CreatedAtUtc });
            entity.Property(item => item.EventType).HasMaxLength(80).IsRequired();
            entity.Property(item => item.FromStatus).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.ToStatus).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.Notes).HasMaxLength(2000);
            entity.HasOne<Course>().WithMany().HasForeignKey(item => item.CourseId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<Enrollment>(entity =>
        {
            entity.ToTable("enrollments");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.CourseId, item.LearnerUserId }).IsUnique();
            entity.HasIndex(item => new { item.TenantId, item.LearnerUserId, item.Status });
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.Source).HasConversion<string>().HasMaxLength(32);
            entity.HasOne<Course>().WithMany().HasForeignKey(item => item.CourseId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<GamificationProfile>(entity =>
        {
            entity.ToTable("gamification_profiles");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.UserId }).IsUnique();
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<GamificationSettings>(entity =>
        {
            entity.ToTable("gamification_settings");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.TenantId).IsUnique();
            entity.Property(item => item.MaxAwardsPerHour).HasDefaultValue(10);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<GamificationAbuseReview>(entity =>
        {
            entity.ToTable("gamification_abuse_reviews");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.UserId, item.Status, item.WindowStartedAtUtc });
            entity.Property(item => item.Signal).HasMaxLength(120).IsRequired();
            entity.Property(item => item.Status).HasMaxLength(32).IsRequired();
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<GamificationEvent>(entity =>
        {
            entity.ToTable("gamification_events");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.UserId, item.IdempotencyKey }).IsUnique();
            entity.HasIndex(item => new { item.TenantId, item.UserId, item.OccurredAtUtc });
            entity.Property(item => item.Type).HasConversion<string>().HasMaxLength(40);
            entity.Property(item => item.Description).HasMaxLength(250).IsRequired();
            entity.Property(item => item.IdempotencyKey).HasMaxLength(180).IsRequired();
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<BadgeDefinition>(entity =>
        {
            entity.ToTable("badge_definitions");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.Code }).IsUnique();
            entity.Property(item => item.Code).HasMaxLength(80).IsRequired();
            entity.Property(item => item.Name).HasMaxLength(150).IsRequired();
            entity.Property(item => item.Description).HasMaxLength(500).IsRequired();
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<UserBadge>(entity =>
        {
            entity.ToTable("user_badges");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.UserId, item.BadgeDefinitionId }).IsUnique();
            entity.HasOne(item => item.BadgeDefinition).WithMany().HasForeignKey(item => item.BadgeDefinitionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<VirtualLab>(entity =>
        {
            entity.ToTable("virtual_labs");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.Code }).IsUnique();
            entity.Property(item => item.Code).HasMaxLength(80).IsRequired();
            entity.Property(item => item.Name).HasMaxLength(200).IsRequired();
            entity.Property(item => item.Description).HasMaxLength(4000);
            entity.Property(item => item.ProviderType).HasMaxLength(80).IsRequired();
            entity.Property(item => item.LaunchUrl).HasMaxLength(1000);
            entity.Property(item => item.HealthStatus).HasMaxLength(32).IsRequired();
            entity.Property(item => item.LastHealthError).HasMaxLength(1000);
            entity.Property(item => item.WebhookSecretProtected).HasMaxLength(4000);
            entity.Property(item => item.WebhookSecretReference).HasMaxLength(200);
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<VirtualLabResult>(entity =>
        {
            entity.ToTable("virtual_lab_results");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.VirtualLabId, item.UserId, item.ExternalAttemptId }).IsUnique();
            entity.Property(item => item.ExternalAttemptId).HasMaxLength(160).IsRequired();
            entity.Property(item => item.ScorePercent).HasPrecision(5, 2);
            entity.Property(item => item.PayloadJson).HasMaxLength(20000);
            entity.HasOne<VirtualLab>().WithMany().HasForeignKey(item => item.VirtualLabId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<VirtualLabHealthCheck>(entity =>
        {
            entity.ToTable("virtual_lab_health_checks");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.VirtualLabId, item.CheckedAtUtc });
            entity.Property(item => item.Status).HasMaxLength(32).IsRequired();
            entity.Property(item => item.Error).HasMaxLength(1000);
            entity.HasOne<VirtualLab>().WithMany().HasForeignKey(item => item.VirtualLabId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<VirtualLabWebhookEvent>(entity =>
        {
            entity.ToTable("virtual_lab_webhook_events");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.VirtualLabId, item.ExternalEventId }).IsUnique();
            entity.Property(item => item.ExternalEventId).HasMaxLength(160).IsRequired();
            entity.Property(item => item.PayloadJson).HasMaxLength(200000).IsRequired();
            entity.Property(item => item.Signature).HasMaxLength(256).IsRequired();
            entity.Property(item => item.Status).HasMaxLength(32).IsRequired();
            entity.Property(item => item.LastError).HasMaxLength(2000);
            entity.HasOne<VirtualLab>().WithMany().HasForeignKey(item => item.VirtualLabId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<LearningProgram>(entity =>
        {
            entity.ToTable("learning_programs");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.Slug }).IsUnique();
            entity.Property(item => item.Title).HasMaxLength(250).IsRequired();
            entity.Property(item => item.Description).HasMaxLength(4000);
            entity.Property(item => item.MetadataJson).HasMaxLength(20000);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<Subject>(entity =>
        {
            entity.ToTable("subjects");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.Name }).IsUnique();
            entity.Property(item => item.Name).HasMaxLength(200).IsRequired();
            entity.Property(item => item.Description).HasMaxLength(2000);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<LearningPath>(entity =>
        {
            entity.ToTable("learning_paths");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.Title });
            entity.Property(item => item.Title).HasMaxLength(250).IsRequired();
            entity.Property(item => item.ItemsJson).HasMaxLength(20000);
            entity.Property(item => item.PrerequisitesJson).HasMaxLength(20000);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<LearningResource>(entity =>
        {
            entity.ToTable("learning_resources");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.StorageKey }).IsUnique();
            entity.Property(item => item.Title).HasMaxLength(300).IsRequired();
            entity.Property(item => item.Description).HasMaxLength(4000);
            entity.Property(item => item.ContentType).HasMaxLength(150);
            entity.Property(item => item.MetadataJson).HasMaxLength(20000);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<LearningHistory>(entity =>
        {
            entity.ToTable("learning_history");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.UserId, item.ItemType, item.ItemId });
            entity.Property(item => item.Progress).HasPrecision(5, 2);
            entity.Property(item => item.Score).HasPrecision(5, 2);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<RecommendationDismissal>(entity =>
        {
            entity.ToTable("recommendation_dismissals");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.LearnerUserId, item.CourseId }).IsUnique();
            entity.Property(item => item.Variant).HasMaxLength(40).IsRequired();
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<OfflineDevice>(entity =>
        {
            entity.ToTable("offline_devices");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.UserId, item.FingerprintHash }).IsUnique();
            entity.HasIndex(item => new { item.TenantId, item.UserId, item.Status });
            entity.Property(item => item.Name).HasMaxLength(120).IsRequired();
            entity.Property(item => item.FingerprintHash).HasMaxLength(128).IsRequired();
            entity.Property(item => item.SecretHash).HasMaxLength(128).IsRequired();
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<OfflinePackageLicense>(entity =>
        {
            entity.ToTable("offline_package_licenses");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.OfflineDeviceId, item.CourseId }).IsUnique();
            entity.HasIndex(item => new { item.TenantId, item.UserId, item.ExpiresAtUtc });
            entity.HasOne<OfflineDevice>().WithMany().HasForeignKey(item => item.OfflineDeviceId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Course>().WithMany().HasForeignKey(item => item.CourseId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<OfflineSyncConflict>(entity =>
        {
            entity.ToTable("offline_sync_conflicts");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.UserId, item.Status, item.CreatedAtUtc });
            entity.Property(item => item.ClientStatus).HasMaxLength(32).IsRequired();
            entity.Property(item => item.ServerStatus).HasMaxLength(32).IsRequired();
            entity.Property(item => item.Status).HasMaxLength(32).IsRequired();
            entity.HasOne<OfflineDevice>().WithMany().HasForeignKey(item => item.OfflineDeviceId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<ProductTelemetryEvent>(entity =>
        {
            entity.ToTable("product_telemetry_events");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.Name, item.OccurredAtUtc });
            entity.Property(item => item.Name).HasMaxLength(100).IsRequired();
            entity.Property(item => item.PropertiesJson).HasMaxLength(10000);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<LessonProgress>(entity =>
        {
            entity.ToTable("lesson_progress");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.EnrollmentId, item.LessonId }).IsUnique();
            entity.HasIndex(item => new { item.TenantId, item.LearnerUserId, item.CourseId });
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
            entity.HasOne<Enrollment>().WithMany().HasForeignKey(item => item.EnrollmentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<CourseLesson>().WithMany().HasForeignKey(item => item.LessonId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<LearningProgressEvent>(entity =>
        {
            entity.ToTable("learning_progress_events");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.EnrollmentId, item.OccurredAtUtc });
            entity.HasIndex(item => new { item.TenantId, item.EnrollmentId, item.IdempotencyKey }).IsUnique();
            entity.Property(item => item.EventType).HasConversion<string>().HasMaxLength(40);
            entity.Property(item => item.IdempotencyKey).HasMaxLength(160);
            entity.HasOne<Enrollment>().WithMany().HasForeignKey(item => item.EnrollmentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<CourseBookmark>(entity =>
        {
            entity.ToTable("course_bookmarks");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.EnrollmentId, item.LessonId });
            entity.Property(item => item.Title).HasMaxLength(200);
            entity.Property(item => item.Note).HasMaxLength(2000);
            entity.HasOne<Enrollment>().WithMany().HasForeignKey(item => item.EnrollmentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<CourseLesson>().WithMany().HasForeignKey(item => item.LessonId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<LearnerNote>(entity =>
        {
            entity.ToTable("learner_notes");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.EnrollmentId, item.LessonId });
            entity.Property(item => item.Content).HasMaxLength(20000).IsRequired();
            entity.HasOne<Enrollment>().WithMany().HasForeignKey(item => item.EnrollmentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<CourseLesson>().WithMany().HasForeignKey(item => item.LessonId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<QuestionBank>(entity =>
        {
            entity.ToTable("question_banks");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.CourseId, item.Name }).IsUnique();
            entity.Property(item => item.Name).HasMaxLength(200).IsRequired();
            entity.HasOne<Course>().WithMany().HasForeignKey(item => item.CourseId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<Question>(entity =>
        {
            entity.ToTable("questions");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.QuestionBankId });
            entity.Property(item => item.Type).HasConversion<string>().HasMaxLength(40);
            entity.Property(item => item.Prompt).HasMaxLength(10000).IsRequired();
            entity.Property(item => item.OptionsJson).HasMaxLength(20000).IsRequired();
            entity.Property(item => item.CorrectAnswerJson).HasMaxLength(20000).IsRequired();
            entity.HasOne<Rubric>().WithMany().HasForeignKey(item => item.RubricId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<QuestionBank>().WithMany().HasForeignKey(item => item.QuestionBankId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<Assessment>(entity =>
        {
            entity.ToTable("assessments");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.CourseId, item.Status });
            entity.Property(item => item.Title).HasMaxLength(250).IsRequired();
            entity.Property(item => item.Instructions).HasMaxLength(10000);
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.CurrentVersion).HasDefaultValue(1);
            entity.Property(item => item.ShuffleQuestions).HasDefaultValue(false);
            entity.Property(item => item.ShuffleOptions).HasDefaultValue(false);
            entity.HasOne<Course>().WithMany().HasForeignKey(item => item.CourseId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<CourseVersion>().WithMany().HasForeignKey(item => item.CourseVersionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<QuestionBank>().WithMany().HasForeignKey(item => item.QuestionBankId).OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<AssessmentPool>(entity =>
        {
            entity.ToTable("assessment_pools");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.AssessmentId, item.Version, item.Name }).IsUnique();
            entity.Property(item => item.Name).HasMaxLength(100).IsRequired();
            entity.HasOne<Assessment>().WithMany().HasForeignKey(item => item.AssessmentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<Rubric>(entity =>
        {
            entity.ToTable("rubrics");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.CourseId, item.Name }).IsUnique();
            entity.Property(item => item.Name).HasMaxLength(200).IsRequired();
            entity.Property(item => item.CriteriaJson).HasMaxLength(30000).IsRequired();
            entity.HasOne<Course>().WithMany().HasForeignKey(item => item.CourseId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<LearnerAccommodation>(entity =>
        {
            entity.ToTable("learner_accommodations");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.CourseId, item.LearnerUserId }).IsUnique();
            entity.Property(item => item.Note).HasMaxLength(1000);
            entity.HasOne<Course>().WithMany().HasForeignKey(item => item.CourseId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<AssessmentQuestion>(entity =>
        {
            entity.ToTable("assessment_questions");
            entity.HasKey(item => new { item.AssessmentId, item.QuestionId });
            entity.HasIndex(item => new { item.AssessmentId, item.Version, item.DisplayOrder });
            entity.Property(item => item.Version).HasDefaultValue(1);
            entity.Property(item => item.PoolName).HasMaxLength(100);
            entity.HasOne<Assessment>().WithMany().HasForeignKey(item => item.AssessmentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Question>().WithMany().HasForeignKey(item => item.QuestionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssessmentAttempt>(entity =>
        {
            entity.ToTable("assessment_attempts");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.AssessmentId, item.LearnerUserId, item.AttemptNumber }).IsUnique();
            entity.HasIndex(item => new { item.TenantId, item.LearnerUserId, item.Status });
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.Percentage).HasPrecision(5, 2);
            entity.Property(item => item.SubmittedAfterTimeLimit).HasDefaultValue(false);
            entity.Property(item => item.Version).HasDefaultValue(1);
            entity.Property(item => item.PlanJson).HasMaxLength(60000);
            entity.Property(item => item.TeacherFeedback).HasMaxLength(10000);
            entity.HasOne<Assessment>().WithMany().HasForeignKey(item => item.AssessmentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Course>().WithMany().HasForeignKey(item => item.CourseId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<AssessmentAnswer>(entity =>
        {
            entity.ToTable("assessment_answers");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.AttemptId, item.QuestionId }).IsUnique();
            entity.Property(item => item.AnswerJson).HasMaxLength(30000).IsRequired();
            entity.Property(item => item.Feedback).HasMaxLength(4000);
            entity.Property(item => item.RubricScoresJson).HasMaxLength(30000);
            entity.HasOne<AssessmentAttempt>().WithMany().HasForeignKey(item => item.AttemptId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Question>().WithMany().HasForeignKey(item => item.QuestionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<NotificationTemplate>(entity =>
        {
            entity.ToTable("notification_templates");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.Code, item.Channel }).IsUnique();
            entity.Property(item => item.Code).HasMaxLength(100).IsRequired();
            entity.Property(item => item.Name).HasMaxLength(200).IsRequired();
            entity.Property(item => item.Channel).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.SubjectTemplate).HasMaxLength(500).IsRequired();
            entity.Property(item => item.BodyTemplate).HasMaxLength(10000).IsRequired();
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<NotificationPreference>(entity =>
        {
            entity.ToTable("notification_preferences");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.UserId, item.TemplateCode, item.Channel }).IsUnique();
            entity.Property(item => item.TemplateCode).HasMaxLength(100).IsRequired();
            entity.Property(item => item.Channel).HasConversion<string>().HasMaxLength(32);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<NotificationMessage>(entity =>
        {
            entity.ToTable("notification_messages");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.RecipientUserId, item.Status, item.CreatedAtUtc });
            entity.HasIndex(item => new { item.Status, item.NextAttemptAtUtc });
            entity.HasIndex(item => new { item.TenantId, item.RecipientUserId, item.DedupKey }).IsUnique();
            entity.Property(item => item.DedupKey).HasMaxLength(200);
            entity.Property(item => item.TemplateCode).HasMaxLength(100).IsRequired();
            entity.Property(item => item.Channel).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.Subject).HasMaxLength(500).IsRequired();
            entity.Property(item => item.Body).HasMaxLength(20000).IsRequired();
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.LastError).HasMaxLength(4000);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<CertificateTemplate>(entity =>
        {
            entity.ToTable("certificate_templates");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.Name }).IsUnique();
            entity.Property(item => item.Name).HasMaxLength(200).IsRequired();
            entity.Property(item => item.Description).HasMaxLength(2000);
            entity.Property(item => item.BodyTemplate).HasMaxLength(20000).IsRequired();
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<Certificate>(entity =>
        {
            entity.ToTable("certificates");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.EnrollmentId }).IsUnique();
            entity.HasIndex(item => new { item.TenantId, item.CertificateNumber }).IsUnique();
            entity.HasIndex(item => item.VerificationCode).IsUnique();
            entity.Property(item => item.CertificateNumber).HasMaxLength(80).IsRequired();
            entity.Property(item => item.VerificationCode).HasMaxLength(120).IsRequired();
            entity.Property(item => item.CourseTitle).HasMaxLength(250).IsRequired();
            entity.Property(item => item.LearnerName).HasMaxLength(200).IsRequired();
            entity.Property(item => item.ScorePercentage).HasPrecision(5, 2);
            entity.Property(item => item.RevocationReason).HasMaxLength(2000);
            entity.HasOne<CertificateTemplate>().WithMany().HasForeignKey(item => item.TemplateId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Enrollment>().WithMany().HasForeignKey(item => item.EnrollmentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Course>().WithMany().HasForeignKey(item => item.CourseId).OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<TranscriptEntry>(entity =>
        {
            entity.ToTable("transcript_entries");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.LearnerUserId, item.CourseId }).IsUnique();
            entity.Property(item => item.CourseTitle).HasMaxLength(250).IsRequired();
            entity.Property(item => item.EntryType).HasMaxLength(80).IsRequired();
            entity.Property(item => item.ScorePercentage).HasPrecision(5, 2);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<AiJob>(entity =>
        {
            entity.ToTable("ai_jobs");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.Status, item.NextAttemptAtUtc });
            entity.HasIndex(item => new { item.TenantId, item.RequestedByUserId, item.CreatedAtUtc });
            entity.Property(item => item.Feature).HasConversion<string>().HasMaxLength(60);
            entity.Property(item => item.Instruction).HasMaxLength(4000).IsRequired();
            entity.Property(item => item.OutputLanguage).HasMaxLength(20).IsRequired();
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.Provider).HasMaxLength(100);
            entity.Property(item => item.Model).HasMaxLength(150);
            entity.Property(item => item.InputHash).HasMaxLength(128).IsRequired();
            entity.Property(item => item.LastError).HasMaxLength(4000);
            entity.HasOne<Course>().WithMany().HasForeignKey(item => item.CourseId).OnDelete(DeleteBehavior.SetNull);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<AiOutput>(entity =>
        {
            entity.ToTable("ai_outputs");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.JobId, item.CreatedAtUtc });
            entity.Property(item => item.Feature).HasConversion<string>().HasMaxLength(60);
            entity.Property(item => item.Title).HasMaxLength(250).IsRequired();
            entity.Property(item => item.Content).HasMaxLength(20000).IsRequired();
            entity.Property(item => item.StructuredJson).HasMaxLength(50000);
            entity.Property(item => item.Provider).HasMaxLength(100).IsRequired();
            entity.Property(item => item.Model).HasMaxLength(150).IsRequired();
            entity.Property(item => item.InputHash).HasMaxLength(128).IsRequired();
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
            entity.HasOne<AiJob>().WithMany().HasForeignKey(item => item.JobId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Course>().WithMany().HasForeignKey(item => item.CourseId).OnDelete(DeleteBehavior.SetNull);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<AiCitation>(entity =>
        {
            entity.ToTable("ai_citations");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.AiOutputId });
            entity.Property(item => item.SourceType).HasMaxLength(60).IsRequired();
            entity.Property(item => item.SourceTitle).HasMaxLength(250).IsRequired();
            entity.Property(item => item.Locator).HasMaxLength(250);
            entity.Property(item => item.Excerpt).HasMaxLength(1000);
            entity.HasOne<AiOutput>().WithMany().HasForeignKey(item => item.AiOutputId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<AiReview>(entity =>
        {
            entity.ToTable("ai_reviews");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.AiOutputId, item.CreatedAtUtc });
            entity.Property(item => item.Decision).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.Notes).HasMaxLength(2000);
            entity.HasOne<AiOutput>().WithMany().HasForeignKey(item => item.AiOutputId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<LiveClassSession>(entity =>
        {
            entity.ToTable("live_class_sessions"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.StartAtUtc, item.Status });
            entity.HasIndex(item => new { item.TenantId, item.CourseId });
            entity.Property(item => item.Title).HasMaxLength(250).IsRequired();
            entity.Property(item => item.Description).HasMaxLength(4000);
            entity.Property(item => item.Provider).HasMaxLength(80).IsRequired();
            entity.Property(item => item.ProviderMeetingId).HasMaxLength(250).IsRequired();
            entity.Property(item => item.JoinUrl).HasMaxLength(1000).IsRequired();
            entity.Property(item => item.HostUrl).HasMaxLength(1000).IsRequired();
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
            entity.HasOne<Course>().WithMany().HasForeignKey(item => item.CourseId).OnDelete(DeleteBehavior.SetNull);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<SessionAttendance>(entity =>
        {
            entity.ToTable("session_attendance"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.SessionId, item.UserId }).IsUnique();
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
            entity.HasOne<LiveClassSession>().WithMany().HasForeignKey(item => item.SessionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<SessionAnnouncement>(entity =>
        {
            entity.ToTable("session_announcements"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.SessionId, item.CreatedAtUtc });
            entity.Property(item => item.Body).HasMaxLength(10000).IsRequired();
            entity.HasOne<LiveClassSession>().WithMany().HasForeignKey(item => item.SessionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<Assignment>(entity =>
        {
            entity.ToTable("assignments"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.CourseId, item.DueAtUtc });
            entity.Property(item => item.Title).HasMaxLength(250).IsRequired();
            entity.Property(item => item.Instructions).HasMaxLength(20000);
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
            entity.HasOne<Rubric>().WithMany().HasForeignKey(item => item.RubricId).OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<CourseRating>(entity =>
        {
            entity.ToTable("course_ratings"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.CourseId, item.LearnerUserId }).IsUnique();
            entity.HasIndex(item => new { item.TenantId, item.CourseId, item.IsHidden });
            entity.Property(item => item.Review).HasMaxLength(1000);
            entity.HasOne<Course>().WithMany().HasForeignKey(item => item.CourseId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<LandingImage>(entity =>
        {
            entity.ToTable("landing_images"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.TenantId);
            entity.Property(item => item.FileName).HasMaxLength(260).IsRequired();
            entity.Property(item => item.ContentType).HasMaxLength(100).IsRequired();
            entity.Property(item => item.StorageKey).HasMaxLength(500).IsRequired();
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<AssignmentGroup>(entity =>
        {
            entity.ToTable("assignment_groups"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.AssignmentId, item.Name }).IsUnique();
            entity.Property(item => item.Name).HasMaxLength(100).IsRequired();
            entity.HasOne<Assignment>().WithMany().HasForeignKey(item => item.AssignmentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<AssignmentGroupMember>(entity =>
        {
            entity.ToTable("assignment_group_members"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.AssignmentId, item.LearnerUserId }).IsUnique();
            entity.HasIndex(item => item.GroupId);
            entity.HasOne<AssignmentGroup>().WithMany().HasForeignKey(item => item.GroupId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<AssignmentSubmission>(entity =>
        {
            entity.ToTable("assignment_submissions"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.AssignmentId, item.LearnerUserId }).IsUnique();
            entity.Property(item => item.TextResponse).HasMaxLength(20000);
            entity.Property(item => item.FileStorageKey).HasMaxLength(500);
            entity.Property(item => item.FileName).HasMaxLength(260);
            entity.Property(item => item.FileContentType).HasMaxLength(150);
            entity.Property(item => item.Feedback).HasMaxLength(20000);
            entity.Property(item => item.RubricScoresJson).HasMaxLength(30000);
            entity.HasIndex(item => new { item.TenantId, item.GroupId });
            entity.Property(item => item.ScorePoints).HasPrecision(8, 2);
            entity.Property(item => item.FinalPoints).HasPrecision(8, 2);
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
            entity.HasOne<Assignment>().WithMany().HasForeignKey(item => item.AssignmentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<LessonBlock>(entity =>
        {
            entity.ToTable("lesson_blocks"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.CourseLessonId, item.DisplayOrder });
            entity.Property(item => item.Type).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.Title).HasMaxLength(200);
            entity.Property(item => item.Text).HasMaxLength(20000);
            entity.Property(item => item.Language).HasMaxLength(30);
            entity.Property(item => item.Url).HasMaxLength(2000);
            entity.Property(item => item.Caption).HasMaxLength(500);
            entity.HasOne<CourseLesson>().WithMany().HasForeignKey(item => item.CourseLessonId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<CoursePrerequisite>(entity =>
        {
            entity.ToTable("course_prerequisites"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.CourseId, item.RequiredCourseId }).IsUnique();
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<ModuleAccessRule>(entity =>
        {
            entity.ToTable("module_access_rules"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.ModuleId }).IsUnique();
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<Cohort>(entity =>
        {
            entity.ToTable("cohorts"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.Name }).IsUnique();
            entity.Property(item => item.Name).HasMaxLength(120).IsRequired();
            entity.Property(item => item.Description).HasMaxLength(1000);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<CohortMember>(entity =>
        {
            entity.ToTable("cohort_members"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.CohortId, item.UserId }).IsUnique();
            entity.HasIndex(item => new { item.TenantId, item.UserId });
            entity.HasOne<Cohort>().WithMany().HasForeignKey(item => item.CohortId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<CourseInvitation>(entity =>
        {
            entity.ToTable("course_invitations"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.TokenHash }).IsUnique();
            entity.HasIndex(item => new { item.TenantId, item.CourseId, item.Email });
            entity.HasIndex(item => new { item.TenantId, item.Email, item.Status });
            entity.Property(item => item.Email).HasMaxLength(320).IsRequired();
            entity.Property(item => item.TokenHash).HasMaxLength(100).IsRequired();
            entity.Property(item => item.Message).HasMaxLength(1000);
            entity.Property(item => item.EmailError).HasMaxLength(300);
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(20);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<GradeScale>(entity =>
        {
            entity.ToTable("grade_scales"); entity.HasKey(item => item.Id);
            entity.Ignore(item => item.Bands);
            entity.HasIndex(item => new { item.TenantId, item.Name }).IsUnique();
            entity.Property(item => item.Name).HasMaxLength(100).IsRequired();
            entity.Property(item => item.BandsJson).HasMaxLength(4000).IsRequired();
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<CourseGradingSettings>(entity =>
        {
            entity.ToTable("course_grading_settings"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.CourseId }).IsUnique();
            entity.Property(item => item.PassPercent).HasPrecision(5, 2);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<GradeCategory>(entity =>
        {
            entity.ToTable("grade_categories"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.CourseId, item.DisplayOrder });
            entity.Property(item => item.Name).HasMaxLength(60).IsRequired();
            entity.Property(item => item.WeightPercent).HasPrecision(5, 2);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<GradeItemCategory>(entity =>
        {
            entity.ToTable("grade_item_categories"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.ItemKind, item.ItemId }).IsUnique();
            entity.HasIndex(item => new { item.TenantId, item.CourseId });
            entity.Property(item => item.ItemKind).HasMaxLength(20).IsRequired();
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<EmailSettings>(entity =>
        {
            entity.ToTable("email_settings"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.TenantId).IsUnique();
            entity.Property(item => item.Provider).HasMaxLength(32).IsRequired();
            entity.Property(item => item.FromAddress).HasMaxLength(320).IsRequired();
            entity.Property(item => item.FromName).HasMaxLength(150);
            entity.Property(item => item.SmtpHost).HasMaxLength(255);
            entity.Property(item => item.SmtpUsername).HasMaxLength(255);
            entity.Property(item => item.SmtpPasswordProtected).HasMaxLength(4000);
            entity.Property(item => item.SmtpPasswordReference).HasMaxLength(200);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<Conversation>(entity =>
        {
            entity.ToTable("conversations"); entity.HasKey(item => item.Id);
            entity.Property(item => item.Kind).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.DirectKey).HasMaxLength(80);
            entity.HasIndex(item => new { item.TenantId, item.DirectKey }).IsUnique();
            entity.HasIndex(item => new { item.TenantId, item.CourseId });
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<ConversationParticipant>(entity =>
        {
            entity.ToTable("conversation_participants"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.ConversationId, item.UserId }).IsUnique();
            entity.HasIndex(item => new { item.TenantId, item.UserId });
            entity.HasOne<Conversation>().WithMany().HasForeignKey(item => item.ConversationId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<ConversationMessage>(entity =>
        {
            entity.ToTable("conversation_messages"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.ConversationId, item.CreatedAtUtc });
            entity.Property(item => item.Body).HasMaxLength(5000).IsRequired();
            entity.Property(item => item.AttachmentKey).HasMaxLength(500);
            entity.Property(item => item.AttachmentName).HasMaxLength(260);
            entity.Property(item => item.AttachmentContentType).HasMaxLength(200);
            entity.HasOne<Conversation>().WithMany().HasForeignKey(item => item.ConversationId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<ForumThread>(entity =>
        {
            entity.ToTable("forum_threads"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.CourseId, item.LastActivityAtUtc });
            entity.Property(item => item.Title).HasMaxLength(200).IsRequired();
            entity.Property(item => item.Body).HasMaxLength(10000).IsRequired();
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<ForumReply>(entity =>
        {
            entity.ToTable("forum_replies"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.ThreadId, item.CreatedAtUtc });
            entity.Property(item => item.Body).HasMaxLength(10000).IsRequired();
            entity.HasOne<ForumThread>().WithMany().HasForeignKey(item => item.ThreadId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<ForumEdit>(entity =>
        {
            entity.ToTable("forum_edits"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.ThreadId, item.ReplyId, item.EditedAtUtc });
            entity.Property(item => item.PreviousTitle).HasMaxLength(200);
            entity.Property(item => item.PreviousBody).HasMaxLength(10000).IsRequired();
            entity.HasOne<ForumThread>().WithMany().HasForeignKey(item => item.ThreadId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<ForumAttachment>(entity =>
        {
            entity.ToTable("forum_attachments"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.ThreadId, item.ReplyId });
            entity.Property(item => item.StorageKey).HasMaxLength(500).IsRequired();
            entity.Property(item => item.FileName).HasMaxLength(260).IsRequired();
            entity.Property(item => item.ContentType).HasMaxLength(200).IsRequired();
            entity.HasOne<ForumThread>().WithMany().HasForeignKey(item => item.ThreadId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<Announcement>(entity =>
        {
            entity.ToTable("announcements"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.CreatedAtUtc });
            entity.Property(item => item.Title).HasMaxLength(200).IsRequired();
            entity.Property(item => item.Body).HasMaxLength(10000).IsRequired();
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<LivePoll>(entity =>
        {
            entity.ToTable("live_polls"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.SessionId, item.CreatedAtUtc });
            entity.Property(item => item.Question).HasMaxLength(1000).IsRequired();
            entity.Property(item => item.OptionsJson).HasMaxLength(10000).IsRequired();
            entity.HasOne<LiveClassSession>().WithMany().HasForeignKey(item => item.SessionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<LivePollResponse>(entity =>
        {
            entity.ToTable("live_poll_responses"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.PollId, item.UserId }).IsUnique();
            entity.HasOne<LivePoll>().WithMany().HasForeignKey(item => item.PollId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<LiveClassSession>().WithMany().HasForeignKey(item => item.SessionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<SessionJoinRequest>(entity =>
        {
            entity.ToTable("session_join_requests"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.SessionId, item.UserId }).IsUnique();
            entity.HasIndex(item => new { item.SessionId, item.Status });
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(20);
            entity.HasOne<LiveClassSession>().WithMany().HasForeignKey(item => item.SessionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<SessionHandRaise>(entity =>
        {
            entity.ToTable("session_hand_raises"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.SessionId, item.UserId }).IsUnique();
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
            entity.HasOne<LiveClassSession>().WithMany().HasForeignKey(item => item.SessionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<SessionChatMessage>(entity =>
        {
            entity.ToTable("session_chat_messages"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.SessionId, item.CreatedAtUtc });
            entity.Property(item => item.Message).HasMaxLength(4000).IsRequired();
            entity.HasOne<LiveClassSession>().WithMany().HasForeignKey(item => item.SessionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<SessionRecording>(entity =>
        {
            entity.ToTable("session_recordings"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.SessionId }).IsUnique();
            entity.HasIndex(item => new { item.Status, item.NextAttemptAtUtc });
            entity.Property(item => item.Provider).HasMaxLength(80).IsRequired();
            entity.Property(item => item.ProviderRecordingId).HasMaxLength(250).IsRequired();
            entity.Property(item => item.RecordingUrl).HasMaxLength(1000);
            entity.Property(item => item.OutputKey).HasMaxLength(500);
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.LastError).HasMaxLength(4000);
            entity.HasOne<LiveClassSession>().WithMany().HasForeignKey(item => item.SessionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<SessionTrackRecording>(entity =>
        {
            entity.ToTable("session_track_recordings"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.SessionId, item.UserId });
            entity.HasIndex(item => item.Status);
            entity.Property(item => item.ProviderRecordingId).HasMaxLength(250).IsRequired();
            entity.Property(item => item.OutputKey).HasMaxLength(500);
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.LastError).HasMaxLength(4000);
            entity.HasOne<LiveClassSession>().WithMany().HasForeignKey(item => item.SessionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<SessionConsent>(entity =>
        {
            entity.ToTable("session_consents"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.SessionId, item.UserId, item.ConsentType }).IsUnique();
            entity.Property(item => item.ConsentType).HasMaxLength(80).IsRequired();
            entity.Property(item => item.IpAddress).HasMaxLength(64);
            entity.HasOne<LiveClassSession>().WithMany().HasForeignKey(item => item.SessionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<AppUser>().WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<SecurityAuditEvent>(entity =>
        {
            entity.ToTable("security_audit_events"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TenantId, item.CreatedAtUtc });
            entity.Property(item => item.Action).HasMaxLength(100).IsRequired();
            entity.Property(item => item.ResourceType).HasMaxLength(100).IsRequired();
            entity.Property(item => item.DetailsJson).HasMaxLength(10000).IsRequired();
            entity.Property(item => item.IpAddress).HasMaxLength(64);
            entity.Property(item => item.UserAgent).HasMaxLength(500);
            entity.HasQueryFilter(item => tenantContext.TenantId != null && item.TenantId == tenantContext.TenantId);
        });
    }
}
