using System.Collections.Concurrent;
using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using Lms.Api.Domain.Integrations;
using Lms.Api.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;

namespace Lms.Api.Infrastructure.Email;

public sealed record OutgoingEmail(string To, string Subject, string Body);

/// <summary>Raised when a message cannot be sent because email is not (or no longer) configured. Retrying will not help.</summary>
public sealed class EmailNotConfiguredException(string message) : Exception(message);

/// <summary>Everything the sender needs, with the password already resolved.</summary>
public sealed record EmailConnection(string Provider, string FromAddress, string FromName, string? Host, int Port, bool UseSsl, string? Username, string? Password);

public interface IEmailSender
{
    Task SendAsync(EmailConnection connection, OutgoingEmail email, CancellationToken cancellationToken);
}

/// <summary>Keeps the most recent messages sent by the Log provider so they can be inspected in development and tests.</summary>
public sealed class EmailOutbox
{
    private const int Capacity = 200;
    private readonly ConcurrentQueue<OutgoingEmail> messages = new();

    public void Add(OutgoingEmail email)
    {
        messages.Enqueue(email);
        while (messages.Count > Capacity && messages.TryDequeue(out _)) { }
    }

    public IReadOnlyList<OutgoingEmail> Snapshot() => messages.ToArray();
}

public sealed class LogEmailSender(EmailOutbox outbox, ILogger<LogEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailConnection connection, OutgoingEmail email, CancellationToken cancellationToken)
    {
        outbox.Add(email);
        logger.LogInformation("Email (log provider) to {To}: {Subject}", email.To, email.Subject);
        return Task.CompletedTask;
    }
}

public sealed class SmtpEmailSender : IEmailSender
{
    public async Task SendAsync(EmailConnection connection, OutgoingEmail email, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connection.Host)) throw new EmailNotConfiguredException("The SMTP host is not set.");
        using var message = new MailMessage
        {
            From = new MailAddress(connection.FromAddress, string.IsNullOrWhiteSpace(connection.FromName) ? null : connection.FromName),
            Subject = EmailText.SingleLine(email.Subject),
            Body = email.Body,
            IsBodyHtml = false
        };
        message.To.Add(new MailAddress(email.To));

        using var client = new SmtpClient(connection.Host, connection.Port) { EnableSsl = connection.UseSsl, Timeout = 15000, DeliveryMethod = SmtpDeliveryMethod.Network };
        if (!string.IsNullOrWhiteSpace(connection.Username)) client.Credentials = new NetworkCredential(connection.Username, connection.Password ?? string.Empty);
        await client.SendMailAsync(message, cancellationToken);
    }
}

public static class EmailText
{
    /// <summary>Header values must not contain line breaks, which would allow header injection.</summary>
    public static string SingleLine(string value) => value.Replace("\r", " ").Replace("\n", " ").Trim();
}

/// <summary>Stops an administrator pointing SMTP at internal infrastructure (SSRF) unless explicitly allowed.</summary>
public static class SmtpHostPolicy
{
    private static readonly int[] AllowedPorts = [25, 465, 587, 2525];

    public static bool IsPortAllowed(int port, bool allowAnyPort) => allowAnyPort ? port is > 0 and <= 65535 : AllowedPorts.Contains(port);

    public static async Task<bool> IsHostAllowedAsync(string host, bool allowPrivate, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        if (allowPrivate) return true;
        var trimmed = host.Trim();
        if (trimmed.Equals("localhost", StringComparison.OrdinalIgnoreCase) || trimmed.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || trimmed.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var addresses = IPAddress.TryParse(trimmed, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(trimmed, cancellationToken);
            return addresses.Length > 0 && addresses.All(address => !IsPrivate(address));
        }
        catch (SocketException) { return false; }
    }

    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6) return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254)
            || bytes[0] == 0;
    }
}

/// <summary>Resolves a tenant's settings into a connection and sends through the right provider.</summary>
public sealed class EmailService(LogEmailSender logSender, SmtpEmailSender smtpSender, IDataProtectionProvider protection, IManagedSecretStore secrets, IConfiguration configuration)
{
    public const string PasswordPurpose = "lms.email.smtp-password.v1";

    public static string? Protect(IDataProtectionProvider provider, string? password)
        => string.IsNullOrEmpty(password) ? null : provider.CreateProtector(PasswordPurpose).Protect(password);

    public EmailConnection Resolve(EmailSettings settings)
    {
        string? password = null;
        if (!string.IsNullOrWhiteSpace(settings.SmtpPasswordReference)) password = secrets.Get(settings.SmtpPasswordReference);
        else if (!string.IsNullOrWhiteSpace(settings.SmtpPasswordProtected)) password = protection.CreateProtector(PasswordPurpose).Unprotect(settings.SmtpPasswordProtected);
        return new EmailConnection(settings.Provider, settings.FromAddress, settings.FromName, settings.SmtpHost, settings.SmtpPort, settings.SmtpUseSsl, settings.SmtpUsername, password);
    }

    public async Task SendAsync(EmailSettings settings, OutgoingEmail email, CancellationToken cancellationToken)
    {
        if (!settings.Enabled) throw new EmailNotConfiguredException("Email delivery is turned off for this organization.");
        var connection = Resolve(settings);
        if (connection.Provider == EmailProviders.Smtp)
        {
            var allowPrivate = configuration.GetValue("Integrations:AllowPrivateSmtpHosts", false);
            if (!await SmtpHostPolicy.IsHostAllowedAsync(connection.Host ?? string.Empty, allowPrivate, cancellationToken))
                throw new EmailNotConfiguredException("The SMTP host is not allowed.");
            await smtpSender.SendAsync(connection, email, cancellationToken);
        }
        else await logSender.SendAsync(connection, email, cancellationToken);
    }
}
