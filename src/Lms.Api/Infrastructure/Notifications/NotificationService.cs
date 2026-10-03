using Lms.Api.Domain.Notifications;
using Lms.Api.Infrastructure.Email;
using Lms.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Infrastructure.Notifications;

public sealed class NotificationService
{
    public static readonly NotificationTemplateDefinition[] DefaultTemplates =
    [
        new("ENROLLMENT_CREATED", "Enrollment created", "You are enrolled in {{CourseTitle}}", "Your enrollment in {{CourseTitle}} is active."),
        new("COURSE_COMPLETED", "Course completed", "Course completed: {{CourseTitle}}", "Congratulations! You completed {{CourseTitle}}."),
        new("ASSESSMENT_GRADED", "Assessment graded", "Assessment graded: {{AssessmentTitle}}", "Your assessment {{AssessmentTitle}} has been graded: {{Percentage}}%."),
        new("CERTIFICATE_ISSUED", "Certificate issued", "Certificate issued for {{CourseTitle}}", "Your certificate for {{CourseTitle}} is ready. Verification code: {{VerificationCode}}."),
        new("DEADLINE_REMINDER", "Deadline reminder", "Upcoming deadline: {{Title}}", "{{Title}} is due on {{DueDate}}."),
        new("ANNOUNCEMENT", "Announcement", "{{Title}}", "{{Body}}"),
        new("ASSIGNMENT_PUBLISHED", "Assignment published", "New assignment: {{Title}}", "{{CourseTitle}}: {{Title}} is due {{DueDate}}."),
        new("ENROLLMENT_WAITLISTED", "Added to a waitlist", "You are on the waitlist for {{CourseTitle}}", "{{CourseTitle}} is full. You will be enrolled automatically when a place opens up."),
        new("ENROLLMENT_PROMOTED", "Moved off a waitlist", "A place opened up in {{CourseTitle}}", "Good news: you now have a place in {{CourseTitle}} and can start learning."),
        new("COURSE_INVITATION", "Course invitation", "You are invited to {{CourseTitle}}", "{{InviterName}} invited you to {{CourseTitle}}. Open Invitations to accept or decline."),
        new("COURSE_UPDATED", "Course updated", "{{CourseTitle}} has been updated", "{{CourseTitle}} has a new version. {{ChangeSummary}}"),
        new("ASSIGNMENT_GRADED", "Assignment graded", "Assignment graded: {{Title}}", "Your submission for {{Title}} scored {{Score}} out of {{MaxPoints}}.")
    ];

    public static async Task EnsureDefaultTemplatesForAllTenantsAsync(LmsDbContext db, CancellationToken cancellationToken)
    {
        var tenantIds = await db.Tenants.Select(item => item.Id).ToListAsync(cancellationToken);
        foreach (var tenantId in tenantIds) await EnsureDefaultTemplatesAsync(db, tenantId, cancellationToken);
    }

    public static async Task EnsureDefaultTemplatesAsync(LmsDbContext db, Guid tenantId, CancellationToken cancellationToken)
    {
        var existing = await db.NotificationTemplates.IgnoreQueryFilters().Where(item => item.TenantId == tenantId).Select(item => item.Code).ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        db.NotificationTemplates.AddRange(DefaultTemplates.Where(item => !existing.Contains(item.Code, StringComparer.OrdinalIgnoreCase)).Select(item => new NotificationTemplate
        {
            Id = Guid.NewGuid(), TenantId = tenantId, Code = item.Code, Name = item.Name, Channel = NotificationChannel.InApp,
            SubjectTemplate = item.Subject, BodyTemplate = item.Body, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now
        }));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task QueueAsync(LmsDbContext db, Guid tenantId, Guid recipientUserId, string templateCode, IReadOnlyDictionary<string, string>? tokens = null, CancellationToken cancellationToken = default)
    {
        var template = await db.NotificationTemplates.SingleOrDefaultAsync(item => item.Code == templateCode && item.Channel == NotificationChannel.InApp && item.IsActive, cancellationToken);
        if (template is null) return;
        var subject = Render(template.SubjectTemplate, tokens);
        var body = Render(template.BodyTemplate, tokens);

        // In-app and email preferences are independent: opting out of one does not affect the other.
        var preference = await db.NotificationPreferences.SingleOrDefaultAsync(item => item.UserId == recipientUserId && item.TemplateCode == templateCode && item.Channel == template.Channel, cancellationToken);
        if (preference is not { Enabled: false })
        {
            db.NotificationMessages.Add(new NotificationMessage
            {
                Id = Guid.NewGuid(), TenantId = tenantId, RecipientUserId = recipientUserId, TemplateCode = template.Code,
                Channel = template.Channel, Subject = subject, Body = body,
                Status = NotificationStatus.Pending, AttemptCount = 0, NextAttemptAtUtc = DateTimeOffset.UtcNow, CreatedAtUtc = DateTimeOffset.UtcNow
            });
        }
        await QueueEmailCopiesAsync(db, tenantId, [recipientUserId], template.Code, subject, body, null, cancellationToken);
    }

    /// <summary>
    /// Queues one message per recipient. When a dedup prefix is given, each recipient gets at most one message
    /// for that key, so the call is safe to repeat (for example from a periodic scan). Returns how many were queued.
    /// </summary>
    public async Task<int> QueueManyAsync(LmsDbContext db, Guid tenantId, IEnumerable<Guid> recipientUserIds, string templateCode, IReadOnlyDictionary<string, string>? tokens = null, string? dedupKey = null, CancellationToken cancellationToken = default)
    {
        var recipients = recipientUserIds.Distinct().ToList();
        if (recipients.Count == 0) return 0;
        var template = await db.NotificationTemplates.SingleOrDefaultAsync(item => item.Code == templateCode && item.Channel == NotificationChannel.InApp && item.IsActive, cancellationToken);
        if (template is null) return 0;

        var optedOut = await db.NotificationPreferences.Where(item => recipients.Contains(item.UserId) && item.TemplateCode == templateCode && item.Channel == template.Channel && !item.Enabled)
            .Select(item => item.UserId).ToListAsync(cancellationToken);
        var alreadySent = dedupKey is null
            ? []
            : await db.NotificationMessages.Where(item => recipients.Contains(item.RecipientUserId) && item.DedupKey == dedupKey).Select(item => item.RecipientUserId).ToListAsync(cancellationToken);

        var subject = Render(template.SubjectTemplate, tokens);
        var body = Render(template.BodyTemplate, tokens);
        var now = DateTimeOffset.UtcNow;
        var queued = 0;
        foreach (var recipient in recipients.Except(optedOut).Except(alreadySent))
        {
            db.NotificationMessages.Add(new NotificationMessage
            {
                Id = Guid.NewGuid(), TenantId = tenantId, RecipientUserId = recipient, TemplateCode = template.Code, Channel = template.Channel,
                Subject = subject, Body = body, Status = NotificationStatus.Pending, AttemptCount = 0, NextAttemptAtUtc = now, CreatedAtUtc = now, DedupKey = dedupKey
            });
            queued++;
        }
        await QueueEmailCopiesAsync(db, tenantId, recipients, template.Code, subject, body, dedupKey, cancellationToken);
        return queued;
    }

    /// <summary>
    /// When the organization has email turned on, every notification also gets an Email-channel copy for each recipient
    /// who has not opted out of email for that notification type. The copy shares the dedup key (plus ":email").
    /// </summary>
    private static async Task QueueEmailCopiesAsync(LmsDbContext db, Guid tenantId, List<Guid> recipients, string templateCode, string subject, string body, string? dedupKey, CancellationToken cancellationToken)
    {
        var settings = await db.EmailSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (settings is not { Enabled: true }) return;
        var optedOut = await db.NotificationPreferences.Where(item => recipients.Contains(item.UserId) && item.TemplateCode == templateCode && item.Channel == NotificationChannel.Email && !item.Enabled)
            .Select(item => item.UserId).ToListAsync(cancellationToken);
        var emailKey = dedupKey is null ? null : dedupKey + ":email";
        var already = emailKey is null
            ? []
            : await db.NotificationMessages.Where(item => recipients.Contains(item.RecipientUserId) && item.DedupKey == emailKey).Select(item => item.RecipientUserId).ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        foreach (var recipient in recipients.Except(optedOut).Except(already))
        {
            db.NotificationMessages.Add(new NotificationMessage
            {
                Id = Guid.NewGuid(), TenantId = tenantId, RecipientUserId = recipient, TemplateCode = templateCode, Channel = NotificationChannel.Email,
                Subject = EmailText.SingleLine(subject), Body = body, Status = NotificationStatus.Pending, AttemptCount = 0, NextAttemptAtUtc = now, CreatedAtUtc = now, DedupKey = emailKey
            });
        }
    }

    private static string Render(string template, IReadOnlyDictionary<string, string>? tokens)
    {
        if (tokens is null) return template;
        foreach (var token in tokens) template = template.Replace($"{{{{{token.Key}}}}}", token.Value, StringComparison.OrdinalIgnoreCase);
        return template;
    }
}

public sealed record NotificationTemplateDefinition(string Code, string Name, string Subject, string Body);
