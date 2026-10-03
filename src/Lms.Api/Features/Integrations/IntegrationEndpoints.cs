using System.Net.Mail;
using System.Security.Claims;
using Lms.Api.Domain.Integrations;
using Lms.Api.Domain.LiveClasses;
using Lms.Api.Infrastructure.LiveClasses;
using Lms.Api.Domain.Notifications;
using Lms.Api.Infrastructure.Email;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Integrations;

/// <summary>Per-organization integration settings. Email today; payments, video and AI providers follow the same shape.</summary>
public static class IntegrationEndpoints
{
    public static void MapIntegrationEndpoints(this WebApplication app)
    {
        var tenant = app.MapGroup("/api/v1/tenant/integrations").RequireAuthorization("tenant.authenticated");
        tenant.AddEndpointFilter(async (context, next) =>
        {
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (!tenantContext.IsResolved)
                return Results.BadRequest(new { message = "A tenant is required. Use the X-Tenant-Slug header or a tenant subdomain." });
            return await next(context);
        });

        // Only organization administrators (tenant.manage) may read or change integration settings.
        tenant.MapGet("/live-classes", GetLiveClassSettingsAsync).RequireAuthorization("tenant.tenant.manage");
        tenant.MapPut("/live-classes", SaveLiveClassSettingsAsync).RequireAuthorization("tenant.tenant.manage");
        tenant.MapGet("/email", GetEmailAsync).RequireAuthorization("tenant.tenant.manage");
        tenant.MapPut("/email", SaveEmailAsync).RequireAuthorization("tenant.tenant.manage");
        tenant.MapPost("/email/test", SendTestAsync).RequireAuthorization("tenant.tenant.manage");
        tenant.MapGet("/email/outbox", OutboxAsync).RequireAuthorization("tenant.tenant.manage");
    }

    private static async Task<IResult> GetLiveClassSettingsAsync(LmsDbContext db, CancellationToken cancellationToken)
    {
        var settings = await db.LiveClassSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return Results.Ok(ToLiveResponse(settings));
    }

    /// <summary>Never includes the LiveKit secret, only whether one is set.</summary>
    private static LiveClassSettingsResponse ToLiveResponse(LiveClassSettings? settings)
        => new(settings?.Provider ?? LiveClassProviders.Local, settings?.JitsiBaseUrl ?? LiveClassProviders.DefaultJitsiBaseUrl, settings?.UpdatedAtUtc,
            settings?.LiveKitUrl, settings?.LiveKitApiKey, !string.IsNullOrEmpty(settings?.LiveKitSecretProtected) || !string.IsNullOrEmpty(settings?.LiveKitSecretReference));

    private static async Task<IResult> SaveLiveClassSettingsAsync(HttpContext httpContext, ITenantContext tenantContext, LmsDbContext db, IConfiguration configuration, IHostEnvironment environment, LiveKitCredentialStore credentials, SaveLiveClassSettingsRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId) return Results.Unauthorized();
        var provider = LiveClassProviders.All.FirstOrDefault(item => item.Equals(request.Provider, StringComparison.OrdinalIgnoreCase));
        if (provider is null) return Problem($"Provider must be one of: {string.Join(", ", LiveClassProviders.All)}.");
        string? jitsi = null;
        if (provider == LiveClassProviders.Jitsi)
        {
            jitsi = LiveClassUrls.NormalizeHttps(string.IsNullOrWhiteSpace(request.JitsiBaseUrl) ? LiveClassProviders.DefaultJitsiBaseUrl : request.JitsiBaseUrl, configuration.GetValue("Integrations:AllowInsecureLiveClassHosts", false));
            if (jitsi is null) return Problem("Enter the Jitsi server address as an https link, for example https://meet.jit.si.");
            if (jitsi.Contains('?') || jitsi.Contains('#')) return Problem("The Jitsi server address must not contain a query or fragment.");
        }
        var settings = await db.LiveClassSettings.SingleOrDefaultAsync(cancellationToken);
        var allowInsecure = configuration.GetValue("Integrations:AllowInsecureLiveClassHosts", false);
        string? liveKitUrl = null;
        if (provider == LiveClassProviders.LiveKit)
        {
            liveKitUrl = NormalizeLiveKitUrl(request.LiveKitUrl, allowInsecure, allowLoopback: environment.IsDevelopment());
            if (liveKitUrl is null) return Problem("Enter the LiveKit server address, for example wss://your-project.livekit.cloud.");
            if (string.IsNullOrWhiteSpace(request.LiveKitApiKey) || request.LiveKitApiKey.Trim().Length > 200) return Problem("Enter the LiveKit API key.");
            if (request.LiveKitSecretReference is { Length: > 200 }) return Problem("The secret reference is too long.");
            var hasSecret = !string.IsNullOrEmpty(request.LiveKitApiSecret) || !string.IsNullOrWhiteSpace(request.LiveKitSecretReference)
                || !string.IsNullOrEmpty(settings?.LiveKitSecretProtected) || !string.IsNullOrEmpty(settings?.LiveKitSecretReference);
            if (!hasSecret) return Problem("Enter the LiveKit API secret.");
            if (request.LiveKitApiSecret is { Length: > 500 }) return Problem("The API secret is too long.");
        }
        if (settings is null)
        {
            settings = new LiveClassSettings { Id = Guid.NewGuid(), TenantId = tenantId };
            db.LiveClassSettings.Add(settings);
        }
        settings.Provider = provider;
        settings.JitsiBaseUrl = jitsi;
        if (provider == LiveClassProviders.LiveKit)
        {
            settings.LiveKitUrl = liveKitUrl;
            settings.LiveKitApiKey = request.LiveKitApiKey!.Trim();
            // A blank secret means "keep the one already saved", so the form never has to show it.
            if (!string.IsNullOrEmpty(request.LiveKitApiSecret)) { settings.LiveKitSecretProtected = credentials.Protect(request.LiveKitApiSecret); settings.LiveKitSecretReference = null; }
            if (!string.IsNullOrWhiteSpace(request.LiveKitSecretReference)) { settings.LiveKitSecretReference = request.LiveKitSecretReference.Trim(); settings.LiveKitSecretProtected = null; }
        }
        settings.UpdatedByUserId = Guid.TryParse(httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ? userId : null;
        settings.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToLiveResponse(settings));
    }

    /// <summary>A clean wss:// address (ws:// only when told to, or for this machine itself while developing), or null.</summary>
    private static string? NormalizeLiveKitUrl(string? value, bool allowInsecure, bool allowLoopback = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 500) return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return null;
        var secure = uri.Scheme is "wss" or "https";
        if (!secure && !((allowInsecure || (allowLoopback && uri.IsLoopback)) && uri.Scheme is "ws" or "http")) return null;
        if (!string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return null;
        return uri.ToString().TrimEnd('/');
    }

    private static async Task<IResult> GetEmailAsync(LmsDbContext db, CancellationToken cancellationToken)
    {
        var settings = await db.EmailSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return Results.Ok(ToResponse(settings));
    }

    private static async Task<IResult> SaveEmailAsync(HttpContext httpContext, ITenantContext tenantContext, LmsDbContext db, IConfiguration configuration, IDataProtectionProvider protection, SaveEmailSettingsRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId) return Results.Unauthorized();
        var provider = EmailProviders.All.FirstOrDefault(item => item.Equals(request.Provider, StringComparison.OrdinalIgnoreCase));
        if (provider is null) return Problem($"Provider must be one of: {string.Join(", ", EmailProviders.All)}.");
        if (!IsEmail(request.FromAddress)) return Problem("Enter a valid sender email address.");
        if (request.FromName is { Length: > 150 }) return Problem("The sender name is too long.");

        var host = request.SmtpHost?.Trim();
        if (provider == EmailProviders.Smtp)
        {
            if (string.IsNullOrWhiteSpace(host)) return Problem("The SMTP host is required.");
            if (!SmtpHostPolicy.IsPortAllowed(request.SmtpPort, configuration.GetValue("Integrations:AllowPrivateSmtpHosts", false)))
                return Problem("Use a standard mail port: 25, 465, 587 or 2525.");
            if (!await SmtpHostPolicy.IsHostAllowedAsync(host, configuration.GetValue("Integrations:AllowPrivateSmtpHosts", false), cancellationToken))
                return Problem("That SMTP host cannot be used. Internal and unresolvable addresses are not allowed.");
        }
        if (request.SmtpPasswordReference is { Length: > 200 }) return Problem("The secret reference is too long.");

        var settings = await db.EmailSettings.SingleOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            settings = new EmailSettings { Id = Guid.NewGuid(), TenantId = tenantId };
            db.EmailSettings.Add(settings);
        }
        settings.Provider = provider;
        settings.Enabled = request.Enabled;
        settings.FromAddress = request.FromAddress!.Trim();
        settings.FromName = request.FromName?.Trim() ?? string.Empty;
        settings.SmtpHost = provider == EmailProviders.Smtp ? host : null;
        settings.SmtpPort = request.SmtpPort;
        settings.SmtpUseSsl = request.SmtpUseSsl;
        settings.SmtpUsername = string.IsNullOrWhiteSpace(request.SmtpUsername) ? null : request.SmtpUsername.Trim();
        // null = keep the stored password; "" = clear it; anything else = replace it.
        if (request.SmtpPassword is not null) settings.SmtpPasswordProtected = EmailService.Protect(protection, request.SmtpPassword);
        settings.SmtpPasswordReference = string.IsNullOrWhiteSpace(request.SmtpPasswordReference) ? null : request.SmtpPasswordReference.Trim();
        settings.UpdatedByUserId = GetUserId(httpContext);
        settings.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToResponse(settings));
    }

    private static async Task<IResult> SendTestAsync(HttpContext httpContext, LmsDbContext db, EmailService email, CancellationToken cancellationToken)
    {
        var settings = await db.EmailSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (settings is null) return Results.Ok(new TestEmailResponse(false, "Save your email settings first."));
        var address = httpContext.User.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrWhiteSpace(address)) return Results.Ok(new TestEmailResponse(false, "Your account has no email address."));
        try
        {
            // A test works even while delivery is switched off, so settings can be checked before going live.
            await email.SendAsync(new EmailSettings
            {
                Provider = settings.Provider, Enabled = true, FromAddress = settings.FromAddress, FromName = settings.FromName, SmtpHost = settings.SmtpHost, SmtpPort = settings.SmtpPort,
                SmtpUseSsl = settings.SmtpUseSsl, SmtpUsername = settings.SmtpUsername, SmtpPasswordProtected = settings.SmtpPasswordProtected, SmtpPasswordReference = settings.SmtpPasswordReference
            }, new OutgoingEmail(address, "Test email from your learning platform", "This is a test message. If you can read it, email delivery is working."), cancellationToken);
            return Results.Ok(new TestEmailResponse(true, $"A test email was sent to {address}."));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Results.Ok(new TestEmailResponse(false, $"Sending failed: {(exception.Message.Length > 300 ? exception.Message[..300] : exception.Message)}"));
        }
    }

    private static async Task<IResult> OutboxAsync(LmsDbContext db, CancellationToken cancellationToken)
    {
        var messages = await db.NotificationMessages.AsNoTracking().Where(item => item.Channel == NotificationChannel.Email)
            .OrderByDescending(item => item.CreatedAtUtc).Take(50).ToListAsync(cancellationToken);
        var ids = messages.Select(item => item.RecipientUserId).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(item => ids.Contains(item.Id)).ToDictionaryAsync(item => item.Id, item => item.Email, cancellationToken);
        return Results.Ok(messages.Select(item => new OutboxResponse(item.Id, users.GetValueOrDefault(item.RecipientUserId, "Unknown"), item.Subject, item.Status.ToString(), item.AttemptCount, item.LastError, item.CreatedAtUtc, item.SentAtUtc)));
    }

    private static EmailSettingsResponse ToResponse(EmailSettings? settings) => settings is null
        ? new EmailSettingsResponse(EmailProviders.Log, false, string.Empty, string.Empty, null, 587, true, null, false, null, null)
        : new EmailSettingsResponse(settings.Provider, settings.Enabled, settings.FromAddress, settings.FromName, settings.SmtpHost, settings.SmtpPort, settings.SmtpUseSsl,
            settings.SmtpUsername, !string.IsNullOrEmpty(settings.SmtpPasswordProtected), settings.SmtpPasswordReference, settings.UpdatedAtUtc);

    private static IResult Problem(string message) => Results.BadRequest(new { message });

    private static bool IsEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { return new MailAddress(value.Trim()).Address.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase); }
        catch (FormatException) { return false; }
    }

    private static Guid? GetUserId(HttpContext context)
        => Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub"), out var id) ? id : null;
}

/// <summary>SmtpPassword: null keeps the stored one, an empty string clears it, any other value replaces it.</summary>
public sealed record SaveEmailSettingsRequest(string? Provider, bool Enabled, string? FromAddress, string? FromName, string? SmtpHost, int SmtpPort, bool SmtpUseSsl, string? SmtpUsername, string? SmtpPassword, string? SmtpPasswordReference);
/// <summary>The password is never returned; HasPassword only says whether one is stored.</summary>
public sealed record EmailSettingsResponse(string Provider, bool Enabled, string FromAddress, string FromName, string? SmtpHost, int SmtpPort, bool SmtpUseSsl, string? SmtpUsername, bool HasPassword, string? SmtpPasswordReference, DateTimeOffset? UpdatedAtUtc);
public sealed record TestEmailResponse(bool Success, string Message);
public sealed record OutboxResponse(Guid Id, string Recipient, string Subject, string Status, int AttemptCount, string? LastError, DateTimeOffset CreatedAtUtc, DateTimeOffset? SentAtUtc);

public sealed record SaveLiveClassSettingsRequest(string? Provider, string? JitsiBaseUrl, string? LiveKitUrl = null, string? LiveKitApiKey = null, string? LiveKitApiSecret = null, string? LiveKitSecretReference = null);
public sealed record LiveClassSettingsResponse(string Provider, string JitsiBaseUrl, DateTimeOffset? UpdatedAtUtc, string? LiveKitUrl = null, string? LiveKitApiKey = null, bool LiveKitSecretSet = false);
