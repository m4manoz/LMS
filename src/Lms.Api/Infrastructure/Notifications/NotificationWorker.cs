using Lms.Api.Domain.Notifications;
using Lms.Api.Infrastructure.Email;
using Lms.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Infrastructure.Notifications;

/// <summary>Delivers queued messages: in-app ones are marked sent; email ones go through the tenant's email provider.</summary>
public sealed class NotificationDispatcher(EmailService email)
{
    public const int MaxAttempts = 5;

    /// <summary>Processes up to <paramref name="batchSize"/> due messages across all tenants. Returns how many were handled.</summary>
    public async Task<int> DispatchBatchAsync(LmsDbContext db, CancellationToken cancellationToken, int batchSize = 25)
    {
        var now = DateTimeOffset.UtcNow;
        var messages = await db.NotificationMessages.IgnoreQueryFilters()
            .Where(item => (item.Status == NotificationStatus.Pending || item.Status == NotificationStatus.Failed)
                && item.AttemptCount < MaxAttempts && (item.NextAttemptAtUtc == null || item.NextAttemptAtUtc <= now))
            .OrderBy(item => item.CreatedAtUtc).Take(batchSize).ToListAsync(cancellationToken);
        foreach (var message in messages)
        {
            message.Status = NotificationStatus.Processing;
            message.AttemptCount++;
            await db.SaveChangesAsync(cancellationToken);
            try
            {
                // In-app delivery is local and durable; email goes out through the organization's provider.
                if (message.Channel == NotificationChannel.Email) await SendEmailAsync(db, message, cancellationToken);
                message.Status = NotificationStatus.Sent;
                message.SentAtUtc = DateTimeOffset.UtcNow;
                message.NextAttemptAtUtc = null;
                message.LastError = null;
            }
            catch (EmailNotConfiguredException exception)
            {
                // Retrying cannot fix missing configuration, so stop immediately.
                message.Status = NotificationStatus.DeadLetter;
                message.LastError = exception.Message;
                message.NextAttemptAtUtc = null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                message.Status = message.AttemptCount >= MaxAttempts ? NotificationStatus.DeadLetter : NotificationStatus.Failed;
                message.LastError = exception.Message.Length > 500 ? exception.Message[..500] : exception.Message;
                message.NextAttemptAtUtc = DateTimeOffset.UtcNow.AddMinutes(Math.Min(30, message.AttemptCount * 2));
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        return messages.Count;
    }

    private async Task SendEmailAsync(LmsDbContext db, NotificationMessage message, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == message.RecipientUserId, cancellationToken);
        var settings = await db.EmailSettings.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(item => item.TenantId == message.TenantId, cancellationToken);
        if (user is null) throw new EmailNotConfiguredException("The recipient no longer exists.");
        if (settings is null) throw new EmailNotConfiguredException("Email is not configured for this organization.");
        await email.SendAsync(settings, new OutgoingEmail(user.Email, message.Subject, message.Body), cancellationToken);
    }
}

public sealed class NotificationWorker(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<NotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Notifications:DispatcherEnabled", true)) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<NotificationDispatcher>()
                    .DispatchBatchAsync(scope.ServiceProvider.GetRequiredService<LmsDbContext>(), stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException) { logger.LogError(exception, "Notification worker failed while dispatching a batch."); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }
}
