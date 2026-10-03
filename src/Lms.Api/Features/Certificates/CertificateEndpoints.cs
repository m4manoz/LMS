using System.Security.Claims;
using Lms.Api.Domain.Certificates;
using Lms.Api.Domain.Learning;
using Lms.Api.Infrastructure.Notifications;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Certificates;

public static class CertificateEndpoints
{
    private const string DefaultTemplateName = "Standard course completion";

    public static void MapCertificateEndpoints(this WebApplication app)
    {
        var publicApi = app.MapGroup("/api/v1/public");
        publicApi.MapGet("/certificates/{verificationCode}", VerifyPublicAsync);

        var tenant = app.MapGroup("/api/v1/tenant").RequireAuthorization("tenant.authenticated");
        tenant.AddEndpointFilter(async (context, next) =>
        {
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (!tenantContext.IsResolved) return Results.BadRequest(new { message = "A tenant is required. Use the X-Tenant-Slug header or a tenant subdomain." });
            return await next(context);
        });
        tenant.MapGet("/certificate-templates", ListTemplatesAsync).RequireAuthorization("tenant.certificate.read");
        tenant.MapPost("/certificate-templates", CreateTemplateAsync).RequireAuthorization("tenant.certificate.manage");
        tenant.MapGet("/certificates", ListCertificatesAsync).RequireAuthorization("tenant.certificate.read");
        tenant.MapPost("/enrollments/{enrollmentId:guid}/certificate", IssueCertificateAsync).RequireAuthorization("tenant.certificate.manage");
        tenant.MapPost("/certificates/{certificateId:guid}/revoke", RevokeCertificateAsync).RequireAuthorization("tenant.certificate.manage");
        tenant.MapGet("/transcript", ListTranscriptAsync).RequireAuthorization("tenant.certificate.read");
    }

    public static async Task EnsureDefaultTemplateForAllTenantsAsync(LmsDbContext db, CancellationToken cancellationToken)
    {
        var tenantIds = await db.Tenants.Select(item => item.Id).ToListAsync(cancellationToken);
        foreach (var tenantId in tenantIds) await EnsureDefaultTemplateAsync(db, tenantId, cancellationToken);
    }

    public static async Task EnsureDefaultTemplateAsync(LmsDbContext db, Guid tenantId, CancellationToken cancellationToken)
    {
        if (await db.CertificateTemplates.IgnoreQueryFilters().AnyAsync(item => item.TenantId == tenantId && item.Name == DefaultTemplateName, cancellationToken)) return;
        var now = DateTimeOffset.UtcNow;
        db.CertificateTemplates.Add(new CertificateTemplate { Id = Guid.NewGuid(), TenantId = tenantId, Name = DefaultTemplateName, Description = "Default course completion certificate.", BodyTemplate = "This certifies that {{LearnerName}} completed {{CourseTitle}}.", IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<IResult> ListTemplatesAsync(LmsDbContext db, CancellationToken cancellationToken)
    {
        var templates = await db.CertificateTemplates.AsNoTracking().OrderBy(item => item.Name).Select(item => new CertificateTemplateResponse(item.Id, item.Name, item.Description, item.BodyTemplate, item.IsActive)).ToListAsync(cancellationToken);
        return Results.Ok(templates);
    }

    private static async Task<IResult> CreateTemplateAsync(ITenantContext tenantContext, LmsDbContext db, CreateCertificateTemplateRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId) return Results.BadRequest(new { message = "A tenant is required." });
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.BodyTemplate)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["template"] = ["Name and body template are required."] });
        if (await db.CertificateTemplates.AnyAsync(item => item.Name == request.Name.Trim(), cancellationToken)) return Results.Conflict(new { message = "A certificate template with this name already exists." });
        var now = DateTimeOffset.UtcNow;
        var template = new CertificateTemplate { Id = Guid.NewGuid(), TenantId = tenantId, Name = request.Name.Trim(), Description = request.Description?.Trim(), BodyTemplate = request.BodyTemplate.Trim(), IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
        db.CertificateTemplates.Add(template);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/certificate-templates/{template.Id:D}", new CertificateTemplateResponse(template.Id, template.Name, template.Description, template.BodyTemplate, template.IsActive));
    }

    private static async Task<IResult> ListCertificatesAsync(HttpContext httpContext, LmsDbContext db, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var query = db.Certificates.AsNoTracking();
        if (!HasManage(httpContext)) query = query.Where(item => item.LearnerUserId == userId);
        var certificates = await query.OrderByDescending(item => item.IssuedAtUtc).ToListAsync(cancellationToken);
        return Results.Ok(certificates.Select(ToResponse).ToArray());
    }

    private static async Task<IResult> IssueCertificateAsync(HttpContext httpContext, LmsDbContext db, NotificationService notifications, Guid enrollmentId, CancellationToken cancellationToken)
    {
        var enrollment = await db.Enrollments.SingleOrDefaultAsync(item => item.Id == enrollmentId, cancellationToken);
        if (enrollment is null) return Results.NotFound();
        if (enrollment.Status != EnrollmentStatus.Completed) return Results.Conflict(new { message = "A certificate can only be issued for a completed enrollment." });
        var existing = await db.Certificates.SingleOrDefaultAsync(item => item.EnrollmentId == enrollmentId, cancellationToken);
        if (existing is not null) return Results.Ok(ToResponse(existing));
        var course = await db.Courses.SingleOrDefaultAsync(item => item.Id == enrollment.CourseId, cancellationToken);
        var learner = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == enrollment.LearnerUserId, cancellationToken);
        if (course is null || learner is null) return Results.NotFound();
        var template = await db.CertificateTemplates.SingleOrDefaultAsync(item => item.IsActive, cancellationToken);
        if (template is null)
        {
            var now = DateTimeOffset.UtcNow;
            template = new CertificateTemplate { Id = Guid.NewGuid(), TenantId = enrollment.TenantId, Name = DefaultTemplateName, BodyTemplate = "This certifies that {{LearnerName}} completed {{CourseTitle}}.", IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
            db.CertificateTemplates.Add(template);
        }
        var bestScore = await db.AssessmentAttempts.AsNoTracking().Where(item => item.CourseId == enrollment.CourseId && item.LearnerUserId == enrollment.LearnerUserId && item.Status == Domain.Assessments.AttemptStatus.Graded && item.Percentage != null).OrderByDescending(item => item.Percentage).Select(item => item.Percentage).FirstOrDefaultAsync(cancellationToken);
        var issuedAt = DateTimeOffset.UtcNow;
        var certificate = new Certificate { Id = Guid.NewGuid(), TenantId = enrollment.TenantId, TemplateId = template.Id, EnrollmentId = enrollment.Id, CourseId = enrollment.CourseId, LearnerUserId = enrollment.LearnerUserId, CertificateNumber = $"LMS-{issuedAt:yyyy}-{Guid.NewGuid():N}"[..18].ToUpperInvariant(), VerificationCode = Guid.NewGuid().ToString("N"), CourseTitle = course.Title, LearnerName = learner.DisplayName, ScorePercentage = bestScore, IssuedAtUtc = issuedAt };
        db.Certificates.Add(certificate);
        db.TranscriptEntries.Add(new TranscriptEntry { Id = Guid.NewGuid(), TenantId = enrollment.TenantId, LearnerUserId = enrollment.LearnerUserId, CourseId = enrollment.CourseId, CertificateId = certificate.Id, CourseTitle = course.Title, ScorePercentage = bestScore, CompletedAtUtc = enrollment.CompletedAtUtc ?? issuedAt });
        await notifications.QueueAsync(db, enrollment.TenantId, enrollment.LearnerUserId, "CERTIFICATE_ISSUED", new Dictionary<string, string> { ["CourseTitle"] = course.Title, ["VerificationCode"] = certificate.VerificationCode }, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/certificates/{certificate.Id:D}", ToResponse(certificate));
    }

    private static async Task<IResult> RevokeCertificateAsync(LmsDbContext db, Guid certificateId, RevokeCertificateRequest request, CancellationToken cancellationToken)
    {
        var certificate = await db.Certificates.SingleOrDefaultAsync(item => item.Id == certificateId, cancellationToken);
        if (certificate is null) return Results.NotFound();
        if (certificate.RevokedAtUtc is null)
        {
            certificate.RevokedAtUtc = DateTimeOffset.UtcNow;
            certificate.RevocationReason = request.Reason?.Trim();
            await db.SaveChangesAsync(cancellationToken);
        }
        return Results.Ok(ToResponse(certificate));
    }

    private static async Task<IResult> ListTranscriptAsync(HttpContext httpContext, LmsDbContext db, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var query = db.TranscriptEntries.AsNoTracking();
        if (!HasManage(httpContext)) query = query.Where(item => item.LearnerUserId == userId);
        var transcript = await query.OrderByDescending(item => item.CompletedAtUtc).Select(item => new TranscriptResponse(item.Id, item.CourseId, item.CourseTitle, item.EntryType, item.ScorePercentage, item.CertificateId, item.CompletedAtUtc)).ToListAsync(cancellationToken);
        return Results.Ok(transcript);
    }

    private static async Task<IResult> VerifyPublicAsync(LmsDbContext db, string verificationCode, CancellationToken cancellationToken)
    {
        var certificate = await db.Certificates.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(item => item.VerificationCode == verificationCode, cancellationToken);
        return certificate is null ? Results.NotFound(new { message = "Certificate not found." }) : Results.Ok(new PublicCertificateResponse(certificate.CertificateNumber, certificate.CourseTitle, certificate.LearnerName, certificate.IssuedAtUtc, certificate.RevokedAtUtc is null, certificate.RevokedAtUtc, certificate.RevocationReason));
    }

    private static CertificateResponse ToResponse(Certificate item) => new(item.Id, item.CertificateNumber, item.VerificationCode, item.CourseId, item.CourseTitle, item.LearnerUserId, item.LearnerName, item.ScorePercentage, item.IssuedAtUtc, item.RevokedAtUtc, item.RevocationReason, $"/api/v1/public/certificates/{item.VerificationCode}");
    private static Guid? GetUserId(HttpContext httpContext) => Guid.TryParse(httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    private static bool HasManage(HttpContext httpContext) => httpContext.User.HasClaim("permission", Domain.Identity.LmsPermissions.CertificateManage);
}

public sealed record CreateCertificateTemplateRequest(string Name, string? Description, string BodyTemplate);
public sealed record RevokeCertificateRequest(string? Reason);
public sealed record CertificateTemplateResponse(Guid Id, string Name, string? Description, string BodyTemplate, bool IsActive);
public sealed record CertificateResponse(Guid Id, string CertificateNumber, string VerificationCode, Guid CourseId, string CourseTitle, Guid LearnerUserId, string LearnerName, decimal? ScorePercentage, DateTimeOffset IssuedAtUtc, DateTimeOffset? RevokedAtUtc, string? RevocationReason, string VerificationPath);
public sealed record TranscriptResponse(Guid Id, Guid CourseId, string CourseTitle, string EntryType, decimal? ScorePercentage, Guid? CertificateId, DateTimeOffset CompletedAtUtc);
public sealed record PublicCertificateResponse(string CertificateNumber, string CourseTitle, string LearnerName, DateTimeOffset IssuedAtUtc, bool IsValid, DateTimeOffset? RevokedAtUtc, string? RevocationReason);
