namespace Lms.Api.Domain.Certificates;

public sealed class CertificateTemplate
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string BodyTemplate { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class Certificate
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid TemplateId { get; set; }
    public Guid EnrollmentId { get; set; }
    public Guid CourseId { get; set; }
    public Guid LearnerUserId { get; set; }
    public string CertificateNumber { get; set; } = string.Empty;
    public string VerificationCode { get; set; } = string.Empty;
    public string CourseTitle { get; set; } = string.Empty;
    public string LearnerName { get; set; } = string.Empty;
    public decimal? ScorePercentage { get; set; }
    public DateTimeOffset IssuedAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public string? RevocationReason { get; set; }
}

public sealed class TranscriptEntry
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid LearnerUserId { get; set; }
    public Guid CourseId { get; set; }
    public Guid? CertificateId { get; set; }
    public string CourseTitle { get; set; } = string.Empty;
    public string EntryType { get; set; } = "CourseCompletion";
    public decimal? ScorePercentage { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; }
}
