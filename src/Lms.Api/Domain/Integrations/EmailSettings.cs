namespace Lms.Api.Domain.Integrations;

public static class EmailProviders
{
    /// <summary>Real delivery through an SMTP server.</summary>
    public const string Smtp = "Smtp";
    /// <summary>Writes the message to the application log and an in-memory outbox; for development and demos.</summary>
    public const string Log = "Log";

    public static readonly string[] All = [Smtp, Log];
}

/// <summary>One row per tenant. The SMTP password is never stored in plain text.</summary>
public sealed class EmailSettings
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Provider { get; set; } = EmailProviders.Log;
    public bool Enabled { get; set; }
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseSsl { get; set; } = true;
    public string? SmtpUsername { get; set; }
    /// <summary>Data Protection payload (development and small deployments).</summary>
    public string? SmtpPasswordProtected { get; set; }
    /// <summary>Name of a secret in the managed secret store (preferred in production); wins over the protected value.</summary>
    public string? SmtpPasswordReference { get; set; }
    public Guid? UpdatedByUserId { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
