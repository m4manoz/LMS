using System.Text;
using Lms.Api.Domain.Identity;
using Lms.Api.Domain.Tenants;
using Lms.Api.Features.Identity;
using Lms.Api.Features.Videos;
using Lms.Api.Features.Landing;
using Lms.Api.Features.Courses;
using Lms.Api.Features.Learning;
using Lms.Api.Features.Assessments;
using Lms.Api.Features.Notifications;
using Lms.Api.Features.Certificates;
using Lms.Api.Features.Catalog;
using Lms.Api.Features.Assignments;
using Lms.Api.Features.Community;
using Lms.Api.Features.Content;
using Lms.Api.Features.Cohorts;
using Lms.Api.Features.Enrollments;
using Lms.Api.Features.Invitations;
using Lms.Api.Features.Dashboard;
using Lms.Api.Features.Gradebook;
using Lms.Api.Features.Integrations;
using Lms.Api.Features.Messaging;
using Lms.Api.Features.Reports;
using Lms.Api.Features.Tasks;
using Lms.Api.Infrastructure.Observability;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Security;
using Lms.Api.Infrastructure.Tenancy;
using Lms.Api.Infrastructure.Storage;
using Lms.Api.Infrastructure.Email;
using Lms.Api.Infrastructure.CourseAccess;
using Lms.Api.Infrastructure.Notifications;
using Lms.Api.Infrastructure.AI;
using Lms.Api.Features.AI;
using Lms.Api.Features.Security;
using Lms.Api.Features.LiveClasses;
using Lms.Api.Features.Recommendations;
using Lms.Api.Features.Gamification;
using Lms.Api.Features.VirtualLabs;
using Lms.Api.Features.Offline;
using Lms.Api.Features.Telemetry;
using Lms.Api.Features.Operations;
using Lms.Api.Infrastructure.LiveClasses;
using Lms.Api.Infrastructure.Gamification;
using Lms.Api.Infrastructure.VirtualLabs;
using Lms.Api.Infrastructure.Retention;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Services.AddProblemDetails();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        var client = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var tenant = httpContext.Request.Headers["X-Tenant-Slug"].FirstOrDefault() ?? "public";
        // Applying for a course is open to anyone, so it gets the same strict limit as signing in.
        var isApplication = HttpMethods.IsPost(httpContext.Request.Method) && httpContext.Request.Path.StartsWithSegments("/api/v1/public");
        var isLogin = isApplication || httpContext.Request.Path.StartsWithSegments("/api/v1/auth/login") || httpContext.Request.Path.StartsWithSegments("/api/v1/auth/password-reset") || httpContext.Request.Path.StartsWithSegments("/api/v1/tenant/invitations/public");
        var partition = $"{(isLogin ? "login" : "api")}:{tenant}:{client}";
        return RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = isLogin
                ? builder.Configuration.GetValue("RateLimiting:LoginPermitsPerMinute", 10)
                : builder.Configuration.GetValue("RateLimiting:ApiPermitsPerMinute", 300),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });
});
builder.Services.AddCors(options =>
{
    var origins = builder.Configuration.GetSection("Frontend:AllowedOrigins").Get<string[]>()
        ?? ["http://localhost:5173", "http://127.0.0.1:5173"];

    options.AddPolicy("frontend", policy => policy
        .WithOrigins(origins)
        .AllowAnyHeader()
        .AllowAnyMethod());
});

builder.Services.AddScoped<ITenantContext, TenantContext>();
builder.Services.AddSingleton<RequestMetrics>();
builder.Services.AddScoped<PasswordService>();
builder.Services.AddScoped<JwtTokenService>();
// Storage is chosen when it is first needed (and resolved at startup below), so the final configuration is always the one used.
static bool UsesObjectStorage(IConfiguration configuration) => string.Equals(configuration["Storage:Provider"], "S3", StringComparison.OrdinalIgnoreCase);
builder.Services.AddSingleton<LocalContentAssetStorage>();
builder.Services.AddSingleton(sp => S3StorageOptions.From(sp.GetRequiredService<IConfiguration>()));
// null (no object store) when the provider is Local; GetService returns null and the readiness check skips it.
builder.Services.AddSingleton<IObjectStore>(sp => UsesObjectStorage(sp.GetRequiredService<IConfiguration>())
    ? new S3ObjectStore(sp.GetRequiredService<S3StorageOptions>(), sp.GetRequiredService<IManagedSecretStore>())
    : null!);
builder.Services.AddSingleton<IContentAssetStorage>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var provider = configuration["Storage:Provider"] ?? "Local";
    if (provider.Equals("Local", StringComparison.OrdinalIgnoreCase)) return sp.GetRequiredService<LocalContentAssetStorage>();
    if (!UsesObjectStorage(configuration)) throw new InvalidOperationException("Storage:Provider must be Local or S3.");

    // AWS S3 or any S3-compatible server such as MinIO. Fail at startup rather than on the first upload.
    var options = sp.GetRequiredService<S3StorageOptions>();
    var problems = options.Validate();
    if (problems.Count > 0) throw new InvalidOperationException("Storage:Provider is S3 but its configuration is invalid. " + string.Join(" ", problems));
    var primary = new ObjectStorageContentAssetStorage(sp.GetRequiredService<IObjectStore>(), options);
    // Files uploaded before the switch still live on local disk; keep serving them.
    return configuration.GetValue("Storage:LocalFallback", true)
        ? new FallbackContentAssetStorage(primary, sp.GetRequiredService<LocalContentAssetStorage>())
        : primary;
});
builder.Services.AddHttpClient();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddSingleton<EmailOutbox>();
builder.Services.AddScoped<LogEmailSender>(sp => new LogEmailSender(sp.GetRequiredService<EmailOutbox>(), sp.GetRequiredService<ILogger<LogEmailSender>>()));
builder.Services.AddScoped<SmtpEmailSender>();
builder.Services.AddScoped<EmailService>();
builder.Services.AddScoped<NotificationDispatcher>();
builder.Services.AddScoped<EnrollmentService>();
builder.Services.AddScoped<ModuleAccessService>();
builder.Services.AddScoped<CourseVersioningService>();
builder.Services.AddScoped<InvitationService>();
builder.Services.AddScoped<DeadlineReminderService>();
builder.Services.AddHostedService<NotificationWorker>();
builder.Services.AddHostedService<DeadlineReminderWorker>();
builder.Services.AddSingleton<IAiProvider, LocalAiProvider>();
builder.Services.AddHostedService<AiWorker>();
builder.Services.AddSingleton<LocalLiveClassProvider>();
builder.Services.AddSingleton<ManualLinkLiveClassProvider>();
builder.Services.AddSingleton<JitsiLiveClassProvider>();
builder.Services.AddSingleton<LiveKitLiveClassProvider>();
builder.Services.AddScoped<LiveKitCredentialStore>();
builder.Services.AddSingleton<ILiveKitEgressClient, LiveKitEgressClient>();
builder.Services.AddScoped<LiveKitRecordings>();
builder.Services.AddSingleton<LiveKitRecordingSync>();
builder.Services.AddHostedService<LiveKitRecordingWorker>();
builder.Services.AddSingleton<ILiveClassProvider>(services => services.GetRequiredService<LocalLiveClassProvider>());
builder.Services.AddScoped<LiveClassProviderResolver>();
builder.Services.AddSingleton<Lms.Api.Infrastructure.Videos.VideoPlaybackTokens>();
builder.Services.AddHostedService<LiveClassWorker>();
builder.Services.AddSingleton<Lms.Api.Infrastructure.Videos.IVideoTranscoder, Lms.Api.Infrastructure.Videos.FfmpegVideoTranscoder>();
builder.Services.AddSingleton<Lms.Api.Infrastructure.Videos.VideoProcessingService>();
builder.Services.AddHostedService<Lms.Api.Infrastructure.Videos.VideoProcessingWorker>();
builder.Services.AddSingleton<Lms.Api.Infrastructure.Videos.VideoTranscriptionService>();
builder.Services.AddHostedService<Lms.Api.Infrastructure.Videos.VideoTranscriptionWorker>();
builder.Services.AddScoped<Lms.Api.Infrastructure.Videos.VideoAiCredentialStore>();
builder.Services.AddSingleton<Lms.Api.Infrastructure.Videos.LocalVideoAiClient>();
builder.Services.AddScoped<Lms.Api.Infrastructure.Videos.VideoAiResolver>();
builder.Services.AddScoped<SecurityAuditService>();
builder.Services.AddScoped<GamificationService>();
builder.Services.AddScoped<VirtualLabWebhookProcessor>();
builder.Services.AddHostedService<VirtualLabWebhookWorker>();
builder.Services.AddHostedService<RetentionWorker>();
builder.Services.AddSingleton<IManagedSecretStore, ConfigurationSecretStore>();
builder.Services.AddDbContext<LmsDbContext>(options =>
{
    var provider = builder.Configuration["Database:Provider"] ?? "InMemory";
    if (provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
    {
        options.UseNpgsql(
            builder.Configuration.GetConnectionString("Default"),
            npgsql => npgsql.CommandTimeout(builder.Configuration.GetValue("Database:CommandTimeoutSeconds", 30)));
    }
    else
    {
        options.UseInMemoryDatabase(builder.Configuration["Database:InMemoryName"] ?? "lms-development");
    }
});

var dataProtectionDirectory = builder.Configuration["DataProtection:KeyStoragePath"]?.Trim();
if (string.IsNullOrWhiteSpace(dataProtectionDirectory) && builder.Environment.IsProduction())
{
    throw new InvalidOperationException("DataProtection:KeyStoragePath must point to a durable shared key-ring location in production.");
}
dataProtectionDirectory = string.IsNullOrWhiteSpace(dataProtectionDirectory)
    ? Path.Combine(AppContext.BaseDirectory, "data-protection-keys")
    : dataProtectionDirectory;
Directory.CreateDirectory(dataProtectionDirectory);
builder.Services.AddDataProtection()
    .SetApplicationName(builder.Configuration["DataProtection:ApplicationName"] ?? "Lms.Api")
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionDirectory));

var issuer = builder.Configuration["Auth:Issuer"]
    ?? throw new InvalidOperationException("Auth:Issuer is required.");
var audience = builder.Configuration["Auth:Audience"]
    ?? throw new InvalidOperationException("Auth:Audience is required.");
var signingKey = builder.Configuration["Auth:SigningKey"]
    ?? throw new InvalidOperationException("Auth:SigningKey is required.");
if (Encoding.UTF8.GetByteCount(signingKey) < 32)
{
    throw new InvalidOperationException("Auth:SigningKey must be at least 32 bytes.");
}
if (builder.Environment.IsProduction())
{
    static bool IsPlaceholder(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Contains("local-development", StringComparison.OrdinalIgnoreCase);
    var connectionString = builder.Configuration.GetConnectionString("Default");
    if (IsPlaceholder(signingKey)
        || IsPlaceholder(builder.Configuration["Platform:ProvisioningKey"])
        || (connectionString?.Contains("Password=postgre;", StringComparison.OrdinalIgnoreCase) ?? false))
    {
        throw new InvalidOperationException("Production requires non-default Auth:SigningKey, Platform:ProvisioningKey and database password; supply them through environment variables or the secret manager.");
    }
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = issuer,
        ValidateAudience = true,
        ValidAudience = audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30)
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("tenant.authenticated", policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim("tenant_id"));
    foreach (var permission in LmsPermissions.All)
    {
        options.AddPolicy($"tenant.{permission}", policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim("tenant_id")
            .RequireClaim("permission", permission));
    }
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LmsDbContext>();
    if (db.Database.IsInMemory())
    {
        await db.Database.EnsureCreatedAsync();
    }
    else if (builder.Configuration.GetValue("Database:ApplyMigrations", false))
    {
        await db.Database.MigrateAsync();
    }
    _ = scope.ServiceProvider.GetRequiredService<IContentAssetStorage>(); // validates the storage configuration
    if (scope.ServiceProvider.GetService<IObjectStore>() is { } objectStore && app.Configuration.GetValue("Storage:S3:CreateBucket", false))
        await objectStore.EnsureBucketAsync(CancellationToken.None); // development convenience (MinIO)
    await DevelopmentSeed.SeedAsync(db, scope.ServiceProvider.GetRequiredService<PasswordService>(), builder.Configuration, app.Environment, CancellationToken.None);
    await IdentityEndpoints.EnsureDefaultRolesForAllTenantsAsync(db, CancellationToken.None);
    await NotificationService.EnsureDefaultTemplatesForAllTenantsAsync(db, CancellationToken.None);
    await CertificateEndpoints.EnsureDefaultTemplateForAllTenantsAsync(db, CancellationToken.None);
    await GamificationService.EnsureDefaultBadgesForAllTenantsAsync(db, CancellationToken.None);
    await GamificationService.EnsureDefaultSettingsForAllTenantsAsync(db, CancellationToken.None);
}

app.UseExceptionHandler();
app.UseRouting();
app.UseRateLimiter();
app.UseCors("frontend");
app.UseMiddleware<RequestCorrelationMiddleware>();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthentication();
app.UseMiddleware<TenantClaimValidationMiddleware>();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "lms-api",
    utc = DateTimeOffset.UtcNow
}));

app.MapGet("/health/live", () => Results.Ok(new { status = "alive", service = "lms-api" }));

app.MapGet("/health/ready", async (LmsDbContext db, IServiceProvider services, CancellationToken cancellationToken) =>
{
    try
    {
        if (!await db.Database.CanConnectAsync(cancellationToken)) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    catch
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    // With object storage configured, the API is not ready if it cannot reach the bucket.
    var objectStore = services.GetService<IObjectStore>();
    if (objectStore is not null)
    {
        try { await objectStore.PingAsync(cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Results.Json(new { status = "not-ready", database = "connected", storage = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
    return Results.Ok(new { status = "ready", database = "connected", storage = objectStore is null ? "local" : "connected" });
});

var platform = app.MapGroup("/api/v1/platform");
platform.MapGet("/metrics", (HttpContext httpContext, IConfiguration configuration, RequestMetrics metrics) =>
{
    var configuredKey = configuration["Platform:ProvisioningKey"];
    var suppliedKey = httpContext.Request.Headers["X-Platform-Key"].FirstOrDefault();
    return string.IsNullOrWhiteSpace(configuredKey) || !string.Equals(configuredKey, suppliedKey, StringComparison.Ordinal)
        ? Results.Unauthorized()
        : Results.Ok(metrics.Snapshot());
});
platform.MapPost("/tenants", async (
    HttpContext httpContext,
    IConfiguration configuration,
    LmsDbContext db,
    ProvisionTenantRequest request,
    CancellationToken cancellationToken) =>
{
    var configuredKey = configuration["Platform:ProvisioningKey"];
    var suppliedKey = httpContext.Request.Headers["X-Platform-Key"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(configuredKey)
        || !string.Equals(configuredKey, suppliedKey, StringComparison.Ordinal))
    {
        return Results.Unauthorized();
    }

    var slug = TenantSlug.Normalize(request.Slug);
    if (!TenantSlug.IsValid(slug) || string.IsNullOrWhiteSpace(request.Name))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [nameof(request.Slug)] = ["Use 3-63 lowercase characters, numbers, and hyphens."],
            [nameof(request.Name)] = ["Tenant name is required."]
        });
    }

    if (await db.Tenants.AnyAsync(item => item.Slug == slug, cancellationToken))
    {
        return Results.Conflict(new { message = "A tenant with this slug already exists." });
    }

    var tenant = new Tenant
    {
        Id = Guid.NewGuid(),
        Slug = slug,
        Name = request.Name.Trim(),
        Status = TenantStatus.Active,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        UpdatedAtUtc = DateTimeOffset.UtcNow
    };

    db.Tenants.Add(tenant);
    await db.SaveChangesAsync(cancellationToken);
    await IdentityEndpoints.EnsureDefaultRolesAsync(db, tenant.Id, cancellationToken);
    await NotificationService.EnsureDefaultTemplatesAsync(db, tenant.Id, cancellationToken);
    await CertificateEndpoints.EnsureDefaultTemplateAsync(db, tenant.Id, cancellationToken);
    await GamificationService.EnsureDefaultBadgesAsync(db, tenant.Id, cancellationToken);
    await GamificationService.EnsureDefaultSettingsAsync(db, tenant.Id, cancellationToken);
    return Results.Created($"/api/v1/platform/tenants/{tenant.Slug}", new
    {
        id = tenant.Id,
        tenant.Slug,
        tenant.Name,
        status = tenant.Status.ToString(),
        tenant.CreatedAtUtc
    });
});

app.MapIdentityEndpoints();
app.MapCourseEndpoints();
app.MapLearningEndpoints();
app.MapAssessmentEndpoints();
app.MapNotificationEndpoints();
app.MapCertificateEndpoints();
app.MapReportEndpoints();
app.MapDashboardEndpoints();
app.MapCatalogEndpoints();
app.MapCommunityEndpoints();
app.MapAssignmentEndpoints();
app.MapTaskEndpoints();
app.MapGradebookEndpoints();
app.MapMessagingEndpoints();
app.MapIntegrationEndpoints();
app.MapContentBlockEndpoints();
app.MapEnrollmentManagementEndpoints();
app.MapInvitationEndpoints();
app.MapVideoEndpoints();
app.MapVideoAiEndpoints();
app.MapLandingEndpoints();
app.MapPasswordResetEndpoints();
app.MapCohortEndpoints();
app.MapAiEndpoints();
app.MapSecurityEndpoints();
app.MapLiveClassEndpoints();
app.MapRecommendationEndpoints();
app.MapGamificationEndpoints();
app.MapVirtualLabEndpoints();
app.MapOfflineEndpoints();
app.MapTelemetryEndpoints();
app.MapOperationsEndpoints();

var tenantApi = app.MapGroup("/api/v1/tenant")
    .RequireAuthorization("tenant.authenticated");

tenantApi.AddEndpointFilter(async (context, next) =>
{
    var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
    if (!tenantContext.IsResolved)
    {
        return Results.BadRequest(new
        {
            message = "A tenant is required. Use the X-Tenant-Slug header or a tenant subdomain."
        });
    }

    return await next(context);
});

tenantApi.MapGet("/context", (ITenantContext tenantContext) => Results.Ok(new
{
    tenantId = tenantContext.TenantId,
    tenantSlug = tenantContext.TenantSlug
}));

await app.RunAsync();

public sealed record ProvisionTenantRequest(string Name, string Slug);

public partial class Program { }
