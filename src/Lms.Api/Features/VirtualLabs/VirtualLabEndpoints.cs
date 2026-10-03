using System.Security.Claims;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using Lms.Api.Domain.Tenants;
using Lms.Api.Domain.Identity;
using Lms.Api.Domain.VirtualLabs;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Tokens;
using Lms.Api.Infrastructure.VirtualLabs;
using Lms.Api.Infrastructure.Security;

namespace Lms.Api.Features.VirtualLabs;

public static class VirtualLabEndpoints
{
    public static void MapVirtualLabEndpoints(this WebApplication app)
    {
        var tenant = app.MapGroup("/api/v1/tenant/virtual-labs").RequireAuthorization("tenant.virtuallab.read");
        tenant.AddEndpointFilter(async (context, next) =>
        {
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            return tenantContext.IsResolved ? await next(context) : Results.BadRequest(new { message = "A tenant is required." });
        });
        tenant.MapGet("", ListAsync);
        tenant.MapPost("", CreateAsync).RequireAuthorization("tenant.virtuallab.manage");
        tenant.MapPut("/{labId:guid}", UpdateAsync).RequireAuthorization("tenant.virtuallab.manage");
        tenant.MapGet("/{labId:guid}/launch-token", CreateLaunchTokenAsync);
        tenant.MapPost("/{labId:guid}/health", CheckHealthAsync).RequireAuthorization("tenant.virtuallab.manage");
        tenant.MapPost("/{labId:guid}/results", ReceiveResultAsync);
        tenant.MapGet("/{labId:guid}/health-history", HealthHistoryAsync).RequireAuthorization("tenant.virtuallab.manage");
        tenant.MapGet("/{labId:guid}/results", ListResultsAsync).RequireAuthorization("tenant.virtuallab.manage");
        tenant.MapGet("/{labId:guid}/webhooks", ListWebhooksAsync).RequireAuthorization("tenant.virtuallab.manage");
        tenant.MapPost("/{labId:guid}/webhooks/{eventId:guid}/retry", RetryWebhookAsync).RequireAuthorization("tenant.virtuallab.manage");
        app.MapPost("/api/v1/integrations/virtual-labs/{labCode}/webhook", ReceiveWebhookAsync);
    }

    private static async Task<IResult> ListAsync(HttpContext httpContext, LmsDbContext db, CancellationToken cancellationToken)
    {
        var canManage = httpContext.User.HasClaim("permission", LmsPermissions.VirtualLabManage);
        var query = db.VirtualLabs.AsNoTracking();
        if (!canManage) query = query.Where(item => item.Status == VirtualLabStatus.Active);
        var labs = (await query.OrderBy(item => item.Name).ToArrayAsync(cancellationToken)).Select(ToResponse).ToArray();
        return Results.Ok(labs);
    }

    private static async Task<IResult> CreateAsync(HttpContext httpContext, LmsDbContext db, ITenantContext tenantContext, IDataProtectionProvider protectionProvider, CreateVirtualLabRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var validation = Validate(request.Code, request.Name, request.ProviderType, request.LaunchUrl);
        if (validation is not null) return validation;
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.VirtualLabs.AnyAsync(item => item.Code == code, cancellationToken)) return Results.Conflict(new { message = "A virtual lab with this code already exists." });
        var now = DateTimeOffset.UtcNow;
        var secretReference = string.IsNullOrWhiteSpace(request.WebhookSecretReference) ? null : request.WebhookSecretReference.Trim();
        var lab = new VirtualLab { Id = Guid.NewGuid(), TenantId = tenantId, Code = code, Name = request.Name.Trim(), Description = request.Description?.Trim(), ProviderType = request.ProviderType.Trim(), LaunchUrl = request.LaunchUrl?.Trim(), Status = VirtualLabStatus.Draft, CreatedByUserId = userId, HealthStatus = "Unknown", WebhookSecretProtected = secretReference is null ? ProtectWebhookSecret(protectionProvider, request.WebhookSecret) : null, WebhookSecretReference = secretReference, CreatedAtUtc = now, UpdatedAtUtc = now };
        db.VirtualLabs.Add(lab);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/virtual-labs/{lab.Id:D}", ToResponse(lab));
    }

    private static async Task<IResult> UpdateAsync(LmsDbContext db, IDataProtectionProvider protectionProvider, Guid labId, UpdateVirtualLabRequest request, CancellationToken cancellationToken)
    {
        var lab = await db.VirtualLabs.SingleOrDefaultAsync(item => item.Id == labId, cancellationToken);
        if (lab is null) return Results.NotFound();
        var validation = Validate(request.Name, request.Name, request.ProviderType, request.LaunchUrl, codeRequired: false);
        if (validation is not null) return validation;
        if (!Enum.TryParse<VirtualLabStatus>(request.Status, true, out var status)) return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Status)] = ["Status must be Draft, Active, or Archived."] });
        lab.Name = request.Name.Trim(); lab.Description = request.Description?.Trim(); lab.ProviderType = request.ProviderType.Trim(); lab.LaunchUrl = request.LaunchUrl?.Trim(); lab.Status = status; lab.UpdatedAtUtc = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(request.WebhookSecretReference))
        {
            lab.WebhookSecretReference = request.WebhookSecretReference.Trim();
            lab.WebhookSecretProtected = null;
        }
        else if (request.WebhookSecret is not null)
        {
            lab.WebhookSecretProtected = ProtectWebhookSecret(protectionProvider, request.WebhookSecret);
            lab.WebhookSecretReference = null;
        }
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToResponse(lab));
    }

    private static async Task<IResult> CreateLaunchTokenAsync(HttpContext httpContext, IConfiguration configuration, LmsDbContext db, Guid labId, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var lab = await db.VirtualLabs.AsNoTracking().SingleOrDefaultAsync(item => item.Id == labId && item.Status == VirtualLabStatus.Active, cancellationToken);
        if (lab is null) return Results.NotFound();
        var signingKey = configuration["Auth:SigningKey"];
        if (string.IsNullOrWhiteSpace(signingKey)) return Results.Problem("Virtual lab signing is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddMinutes(10);
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken("lms-virtual-lab", "lms-virtual-lab", [
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString("D")),
            new Claim("lab_id", lab.Id.ToString("D")),
            new Claim("tenant_id", lab.TenantId.ToString("D"))
        ], now.UtcDateTime, expiresAt.UtcDateTime, credentials);
        return Results.Ok(new { labId = lab.Id, launchUrl = lab.LaunchUrl, token = new JwtSecurityTokenHandler().WriteToken(token), expiresAtUtc = expiresAt });
    }

    private static async Task<IResult> CheckHealthAsync(IHttpClientFactory clients, LmsDbContext db, Guid labId, CancellationToken cancellationToken)
    {
        var lab = await db.VirtualLabs.SingleOrDefaultAsync(item => item.Id == labId, cancellationToken);
        if (lab is null) return Results.NotFound();
        var stopwatch = Stopwatch.StartNew();
        lab.LastHealthCheckUtc = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(lab.LaunchUrl))
        {
            lab.HealthStatus = "NotConfigured"; lab.LastHealthError = "No launch URL is configured.";
        }
        else
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var response = await clients.CreateClient().GetAsync(lab.LaunchUrl, timeout.Token);
                lab.HealthStatus = response.IsSuccessStatusCode ? "Healthy" : "Unhealthy";
                lab.LastHealthError = response.IsSuccessStatusCode ? null : $"Provider returned {(int)response.StatusCode}.";
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                lab.HealthStatus = "Unhealthy"; lab.LastHealthError = exception.Message[..Math.Min(exception.Message.Length, 1000)];
            }
        }
        lab.UpdatedAtUtc = DateTimeOffset.UtcNow;
        db.VirtualLabHealthChecks.Add(new VirtualLabHealthCheck { Id = Guid.NewGuid(), TenantId = lab.TenantId, VirtualLabId = lab.Id, Status = lab.HealthStatus, LatencyMilliseconds = (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds), Error = lab.LastHealthError, CheckedAtUtc = lab.LastHealthCheckUtc.Value });
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToResponse(lab));
    }

    private static async Task<IResult> ReceiveResultAsync(HttpContext httpContext, IConfiguration configuration, LmsDbContext db, Guid labId, ReceiveVirtualLabResultRequest request, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid currentUserId) return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(request.ExternalAttemptId) || request.ExternalAttemptId.Trim().Length > 160) return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.ExternalAttemptId)] = ["An external attempt ID is required and must be at most 160 characters."] });
        if (request.ScorePercent is < 0 or > 100) return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.ScorePercent)] = ["Score must be between 0 and 100."] });
        var signingKey = configuration["Auth:SigningKey"];
        if (string.IsNullOrWhiteSpace(signingKey)) return Results.Problem("Virtual lab signing is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
        Guid tokenTenantId;
        try
        {
            var principal = new JwtSecurityTokenHandler().ValidateToken(request.LaunchToken, new TokenValidationParameters { ValidateIssuer = true, ValidIssuer = "lms-virtual-lab", ValidateAudience = true, ValidAudience = "lms-virtual-lab", ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)), ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(15) }, out _);
            if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var tokenUserId) || tokenUserId != currentUserId || !Guid.TryParse(principal.FindFirstValue("lab_id"), out var tokenLabId) || tokenLabId != labId || !Guid.TryParse(principal.FindFirstValue("tenant_id"), out tokenTenantId)) return Results.Forbid();
        }
        catch (SecurityTokenException) { return Results.Unauthorized(); }
        var lab = await db.VirtualLabs.SingleOrDefaultAsync(item => item.Id == labId && item.Status == VirtualLabStatus.Active, cancellationToken);
        if (lab is null) return Results.NotFound();
        if (lab.TenantId != tokenTenantId) return Results.Forbid();
        var existing = await db.VirtualLabResults.SingleOrDefaultAsync(item => item.VirtualLabId == labId && item.UserId == currentUserId && item.ExternalAttemptId == request.ExternalAttemptId.Trim(), cancellationToken);
        if (existing is not null) return Results.Ok(new VirtualLabResultResponse(existing.Id, existing.ScorePercent, existing.Completed, existing.ReceivedAtUtc));
        var result = new VirtualLabResult { Id = Guid.NewGuid(), TenantId = lab.TenantId, VirtualLabId = labId, UserId = currentUserId, ExternalAttemptId = request.ExternalAttemptId.Trim(), ScorePercent = request.ScorePercent, Completed = request.Completed, PayloadJson = request.PayloadJson, ReceivedAtUtc = DateTimeOffset.UtcNow };
        db.VirtualLabResults.Add(result);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/virtual-labs/{labId:D}/results/{result.Id:D}", new VirtualLabResultResponse(result.Id, result.ScorePercent, result.Completed, result.ReceivedAtUtc));
    }

    private static async Task<IResult> HealthHistoryAsync(LmsDbContext db, Guid labId, CancellationToken cancellationToken)
    {
        if (!await db.VirtualLabs.AnyAsync(item => item.Id == labId, cancellationToken)) return Results.NotFound();
        var history = await db.VirtualLabHealthChecks.AsNoTracking().Where(item => item.VirtualLabId == labId).OrderByDescending(item => item.CheckedAtUtc).Take(100).Select(item => new VirtualLabHealthCheckResponse(item.Id, item.Status, item.LatencyMilliseconds, item.Error, item.CheckedAtUtc)).ToArrayAsync(cancellationToken);
        return Results.Ok(history);
    }

    private static async Task<IResult> ListResultsAsync(LmsDbContext db, Guid labId, CancellationToken cancellationToken)
    {
        if (!await db.VirtualLabs.AnyAsync(item => item.Id == labId, cancellationToken)) return Results.NotFound();
        var results = await db.VirtualLabResults.AsNoTracking().Where(item => item.VirtualLabId == labId).OrderByDescending(item => item.ReceivedAtUtc).Take(100).Select(item => new VirtualLabResultAuditResponse(item.Id, item.UserId, item.ExternalAttemptId, item.ScorePercent, item.Completed, item.ReceivedAtUtc)).ToArrayAsync(cancellationToken);
        return Results.Ok(results);
    }

    private static async Task<IResult> ListWebhooksAsync(LmsDbContext db, Guid labId, CancellationToken cancellationToken)
    {
        if (!await db.VirtualLabs.AnyAsync(item => item.Id == labId, cancellationToken)) return Results.NotFound();
        var events = await db.VirtualLabWebhookEvents.AsNoTracking().Where(item => item.VirtualLabId == labId).OrderByDescending(item => item.ReceivedAtUtc).Take(100).Select(item => new VirtualLabWebhookResponse(item.Id, item.ExternalEventId, item.Status, item.AttemptCount, item.MaxAttempts, item.NextAttemptAtUtc, item.LastError, item.ReceivedAtUtc, item.ProcessedAtUtc)).ToArrayAsync(cancellationToken);
        return Results.Ok(events);
    }

    private static async Task<IResult> RetryWebhookAsync(LmsDbContext db, VirtualLabWebhookProcessor processor, Guid labId, Guid eventId, CancellationToken cancellationToken)
    {
        var webhook = await db.VirtualLabWebhookEvents.SingleOrDefaultAsync(item => item.Id == eventId && item.VirtualLabId == labId, cancellationToken);
        if (webhook is null) return Results.NotFound();
        if (webhook.Status == "Processed") return Results.Ok(new { webhook.Id, webhook.Status });
        webhook.Status = "Failed";
        webhook.NextAttemptAtUtc = DateTimeOffset.UtcNow;
        await processor.ProcessAsync(db, webhook, cancellationToken);
        return Results.Ok(new VirtualLabWebhookResponse(webhook.Id, webhook.ExternalEventId, webhook.Status, webhook.AttemptCount, webhook.MaxAttempts, webhook.NextAttemptAtUtc, webhook.LastError, webhook.ReceivedAtUtc, webhook.ProcessedAtUtc));
    }

    private static async Task<IResult> ReceiveWebhookAsync(HttpContext httpContext, LmsDbContext db, ITenantContext tenantContext, IDataProtectionProvider protectionProvider, IManagedSecretStore secretStore, VirtualLabWebhookProcessor processor, string labCode, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId) return Results.BadRequest(new { message = "X-Tenant-Slug is required for provider webhooks." });
        if (httpContext.Request.ContentLength is > 200000) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        var signature = httpContext.Request.Headers["X-Virtual-Lab-Signature"].FirstOrDefault();
        var externalEventId = httpContext.Request.Headers["X-Virtual-Lab-Event-Id"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(externalEventId) || externalEventId.Length > 160) return Results.BadRequest(new { message = "X-Virtual-Lab-Signature and X-Virtual-Lab-Event-Id are required." });
        var lab = await db.VirtualLabs.SingleOrDefaultAsync(item => item.TenantId == tenantId && item.Code == labCode.Trim().ToUpperInvariant(), cancellationToken);
        if (lab is null) return Results.NotFound();
        var secret = ResolveWebhookSecret(lab, protectionProvider, secretStore);
        if (string.IsNullOrWhiteSpace(secret)) return Results.Problem("Webhook signing is not configured for this lab.", statusCode: StatusCodes.Status503ServiceUnavailable);
        var body = await new StreamReader(httpContext.Request.Body).ReadToEndAsync(cancellationToken);
        var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));
        var supplied = signature.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase) ? signature[7..] : signature;
        if (!string.Equals(expected, supplied, StringComparison.OrdinalIgnoreCase)) return Results.Unauthorized();
        var existing = await db.VirtualLabWebhookEvents.SingleOrDefaultAsync(item => item.VirtualLabId == lab.Id && item.ExternalEventId == externalEventId, cancellationToken);
        if (existing is not null) return Results.Accepted(value: new { existing.Id, existing.Status });
        var webhook = new VirtualLabWebhookEvent { Id = Guid.NewGuid(), TenantId = tenantId, VirtualLabId = lab.Id, ExternalEventId = externalEventId, PayloadJson = body, Signature = signature, Status = "Received", ReceivedAtUtc = DateTimeOffset.UtcNow };
        db.VirtualLabWebhookEvents.Add(webhook);
        await db.SaveChangesAsync(cancellationToken);
        await processor.ProcessAsync(db, webhook, cancellationToken);
        return Results.Accepted(value: new { webhook.Id, webhook.Status });
    }

    private static IResult? Validate(string code, string name, string providerType, string? launchUrl, bool codeRequired = true)
    {
        var errors = new Dictionary<string, string[]>();
        if (codeRequired && (string.IsNullOrWhiteSpace(code) || code.Trim().Length > 80)) errors[nameof(code)] = ["Code is required and must be at most 80 characters."];
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200) errors[nameof(name)] = ["Name is required and must be at most 200 characters."];
        if (string.IsNullOrWhiteSpace(providerType) || providerType.Trim().Length > 80) errors[nameof(providerType)] = ["Provider type is required and must be at most 80 characters."];
        if (!string.IsNullOrWhiteSpace(launchUrl) && (!Uri.TryCreate(launchUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))) errors[nameof(launchUrl)] = ["Launch URL must be an absolute HTTP or HTTPS URL."];
        return errors.Count == 0 ? null : Results.ValidationProblem(errors);
    }

    private static string? ProtectWebhookSecret(IDataProtectionProvider provider, string? secret) => string.IsNullOrWhiteSpace(secret) ? null : provider.CreateProtector("lms.virtual-lab.webhook-secret.v1").Protect(secret.Trim());
    private static string? ResolveWebhookSecret(VirtualLab lab, IDataProtectionProvider protectionProvider, IManagedSecretStore secretStore)
    {
        if (!string.IsNullOrWhiteSpace(lab.WebhookSecretReference)) return secretStore.Get(lab.WebhookSecretReference);
        return string.IsNullOrWhiteSpace(lab.WebhookSecretProtected) ? null : protectionProvider.CreateProtector("lms.virtual-lab.webhook-secret.v1").Unprotect(lab.WebhookSecretProtected);
    }
    private static VirtualLabResponse ToResponse(VirtualLab lab) => new(lab.Id, lab.Code, lab.Name, lab.Description, lab.ProviderType, lab.LaunchUrl, lab.Status.ToString(), string.IsNullOrWhiteSpace(lab.HealthStatus) ? "Unknown" : lab.HealthStatus, lab.LastHealthCheckUtc, lab.LastHealthError, !string.IsNullOrWhiteSpace(lab.WebhookSecretProtected) || !string.IsNullOrWhiteSpace(lab.WebhookSecretReference), lab.UpdatedAtUtc);
    private static Guid? GetUserId(HttpContext httpContext) => Guid.TryParse(httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}

public sealed record CreateVirtualLabRequest(string Code, string Name, string ProviderType, string? Description = null, string? LaunchUrl = null, string? WebhookSecret = null, string? WebhookSecretReference = null);
public sealed record UpdateVirtualLabRequest(string Name, string ProviderType, string Status, string? Description = null, string? LaunchUrl = null, string? WebhookSecret = null, string? WebhookSecretReference = null);
public sealed record ReceiveVirtualLabResultRequest(string LaunchToken, string ExternalAttemptId, bool Completed, decimal? ScorePercent = null, string? PayloadJson = null);
public sealed record VirtualLabResponse(Guid Id, string Code, string Name, string? Description, string ProviderType, string? LaunchUrl, string Status, string HealthStatus, DateTimeOffset? LastHealthCheckUtc, string? LastHealthError, bool WebhookConfigured, DateTimeOffset UpdatedAtUtc);
public sealed record VirtualLabResultResponse(Guid Id, decimal? ScorePercent, bool Completed, DateTimeOffset ReceivedAtUtc);
public sealed record VirtualLabHealthCheckResponse(Guid Id, string Status, int? LatencyMilliseconds, string? Error, DateTimeOffset CheckedAtUtc);
public sealed record VirtualLabResultAuditResponse(Guid Id, Guid UserId, string ExternalAttemptId, decimal? ScorePercent, bool Completed, DateTimeOffset ReceivedAtUtc);
public sealed record VirtualLabWebhookResponse(Guid Id, string ExternalEventId, string Status, int AttemptCount, int MaxAttempts, DateTimeOffset? NextAttemptAtUtc, string? LastError, DateTimeOffset ReceivedAtUtc, DateTimeOffset? ProcessedAtUtc);
