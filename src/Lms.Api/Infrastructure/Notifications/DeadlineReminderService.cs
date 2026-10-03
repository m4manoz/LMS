using Lms.Api.Domain.Assignments;
using Lms.Api.Domain.Learning;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Infrastructure.Notifications;

/// <summary>
/// Reminds enrolled learners who have not submitted an assignment that is due soon.
/// Safe to run repeatedly: each learner is reminded once per assignment (see the dedup key).
/// </summary>
public sealed class DeadlineReminderService(NotificationService notifications)
{
    public static string DedupKey(Guid assignmentId) => $"deadline:{assignmentId}";

    /// <summary>The scoped <paramref name="db"/> must already have its tenant context set.</summary>
    public async Task<int> RunAsync(LmsDbContext db, Guid tenantId, DateTimeOffset now, TimeSpan lookAhead, CancellationToken cancellationToken)
    {
        var horizon = now.Add(lookAhead);
        var assignments = await db.Assignments.AsNoTracking()
            .Where(item => item.Status == AssignmentStatus.Published && item.DueAtUtc != null && item.DueAtUtc > now && item.DueAtUtc <= horizon)
            .ToListAsync(cancellationToken);
        if (assignments.Count == 0) return 0;

        var courseTitles = await db.Courses.AsNoTracking().ToDictionaryAsync(item => item.Id, item => item.Title, cancellationToken);
        var total = 0;
        foreach (var assignment in assignments)
        {
            var submitted = await db.AssignmentSubmissions.AsNoTracking().Where(item => item.AssignmentId == assignment.Id).Select(item => item.LearnerUserId).ToListAsync(cancellationToken);
            var learners = await db.Enrollments.AsNoTracking()
                .Where(item => item.CourseId == assignment.CourseId && item.Status == EnrollmentStatus.Active && !submitted.Contains(item.LearnerUserId))
                .Select(item => item.LearnerUserId).ToListAsync(cancellationToken);
            total += await notifications.QueueManyAsync(db, tenantId, learners, "DEADLINE_REMINDER",
                new Dictionary<string, string> { ["Title"] = assignment.Title, ["DueDate"] = assignment.DueAtUtc!.Value.ToString("u") },
                DedupKey(assignment.Id), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
        return total;
    }
}

public sealed class DeadlineReminderWorker(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<DeadlineReminderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Clamp(configuration.GetValue("Notifications:ReminderScanMinutes", 15), 1, 1440));
        var lookAhead = TimeSpan.FromHours(Math.Clamp(configuration.GetValue("Notifications:DeadlineReminderHours", 24), 1, 168));
        if (!configuration.GetValue("Notifications:RemindersEnabled", true)) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ScanAsync(lookAhead, stoppingToken); }
            catch (Exception exception) when (exception is not OperationCanceledException) { logger.LogError(exception, "Deadline reminder scan failed."); }
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task ScanAsync(TimeSpan lookAhead, CancellationToken cancellationToken)
    {
        List<(Guid Id, string Slug)> tenants;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LmsDbContext>();
            tenants = (await db.Tenants.IgnoreQueryFilters().Select(item => new { item.Id, item.Slug }).ToListAsync(cancellationToken)).Select(item => (item.Id, item.Slug)).ToList();
        }
        foreach (var (id, slug) in tenants)
        {
            // One scope per tenant so the query filters apply to exactly that tenant.
            using var scope = scopeFactory.CreateScope();
            ((TenantContext)scope.ServiceProvider.GetRequiredService<ITenantContext>()).Set(id, slug);
            var db = scope.ServiceProvider.GetRequiredService<LmsDbContext>();
            var queued = await scope.ServiceProvider.GetRequiredService<DeadlineReminderService>().RunAsync(db, id, DateTimeOffset.UtcNow, lookAhead, cancellationToken);
            if (queued > 0) logger.LogInformation("Queued {Count} deadline reminders for tenant {Tenant}.", queued, slug);
        }
    }
}
