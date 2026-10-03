using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lms.Api.Domain.Courses;
using Lms.Api.Domain.Identity;
using Lms.Api.Domain.Learning;
using Lms.Api.Domain.Offline;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Offline;
using Lms.Api.Infrastructure.Storage;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Offline;

public static class OfflineEndpoints
{
    private const int MaximumDevicesPerUser = 3;
    private const int PackageLifetimeHours = 24;

    public static void MapOfflineEndpoints(this WebApplication app)
    {
        var tenant = app.MapGroup("/api/v1/tenant/offline").RequireAuthorization("tenant.enrollment.read");
        tenant.AddEndpointFilter(async (context, next) =>
        {
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            return tenantContext.IsResolved ? await next(context) : Results.BadRequest(new { message = "A tenant is required." });
        });
        tenant.MapPost("/devices", RegisterDeviceAsync);
        tenant.MapGet("/devices", ListDevicesAsync);
        tenant.MapPost("/devices/{deviceId:guid}/revoke", RevokeDeviceAsync);
        tenant.MapGet("/devices/{deviceId:guid}/conflicts", ListConflictsAsync);
        tenant.MapPost("/devices/{deviceId:guid}/sync", SyncAsync);
        tenant.MapGet("/courses/{courseId:guid}/package", GetPackageAsync);
        tenant.MapGet("/courses/{courseId:guid}/assets/{assetId:guid}", DownloadEncryptedAssetAsync);
    }

    private static async Task<IResult> RegisterDeviceAsync(HttpContext httpContext, LmsDbContext db, ITenantContext tenantContext, RegisterOfflineDeviceRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 120) return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Name)] = ["Device name is required and must be at most 120 characters."] });
        if (string.IsNullOrWhiteSpace(request.Fingerprint) || request.Fingerprint.Trim().Length > 200) return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Fingerprint)] = ["A device fingerprint is required and must be at most 200 characters."] });
        var fingerprintHash = Hash(request.Fingerprint.Trim());
        if (await db.OfflineDevices.AnyAsync(item => item.UserId == userId && item.FingerprintHash == fingerprintHash && item.Status == OfflineDeviceStatus.Active, cancellationToken))
            return Results.Conflict(new { message = "This device is already registered. Reuse its existing offline credentials." });
        var activeCount = await db.OfflineDevices.CountAsync(item => item.UserId == userId && item.Status == OfflineDeviceStatus.Active, cancellationToken);
        if (activeCount >= MaximumDevicesPerUser) return Results.Conflict(new { message = $"A learner can register at most {MaximumDevicesPerUser} active offline devices." });

        var now = DateTimeOffset.UtcNow;
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        var device = new OfflineDevice
        {
            Id = Guid.NewGuid(), TenantId = tenantId, UserId = userId, Name = request.Name.Trim(),
            FingerprintHash = fingerprintHash, SecretHash = Hash(secret), Status = OfflineDeviceStatus.Active,
            MaxActivePackages = 5, CreatedAtUtc = now, LastSeenAtUtc = now
        };
        db.OfflineDevices.Add(device);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/offline/devices/{device.Id:D}", new OfflineDeviceRegistrationResponse(device.Id, device.Name, secret, device.MaxActivePackages, device.CreatedAtUtc));
    }

    private static async Task<IResult> ListDevicesAsync(HttpContext httpContext, LmsDbContext db, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var devices = await db.OfflineDevices.AsNoTracking().Where(item => item.UserId == userId).OrderByDescending(item => item.LastSeenAtUtc).ToArrayAsync(cancellationToken);
        return Results.Ok(devices.Select(ToDeviceResponse).ToArray());
    }

    private static async Task<IResult> RevokeDeviceAsync(HttpContext httpContext, LmsDbContext db, Guid deviceId, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var device = await db.OfflineDevices.SingleOrDefaultAsync(item => item.Id == deviceId && item.UserId == userId, cancellationToken);
        if (device is null) return Results.NotFound();
        device.Status = OfflineDeviceStatus.Revoked;
        device.RevokedAtUtc = DateTimeOffset.UtcNow;
        var licenses = await db.OfflinePackageLicenses.Where(item => item.OfflineDeviceId == deviceId && item.RevokedAtUtc == null).ToListAsync(cancellationToken);
        foreach (var license in licenses) license.RevokedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ListConflictsAsync(HttpContext httpContext, LmsDbContext db, Guid deviceId, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        if (!await db.OfflineDevices.AnyAsync(item => item.Id == deviceId && item.UserId == userId, cancellationToken)) return Results.NotFound();
        var conflicts = await db.OfflineSyncConflicts.AsNoTracking().Where(item => item.OfflineDeviceId == deviceId).OrderByDescending(item => item.CreatedAtUtc).Take(100).ToArrayAsync(cancellationToken);
        return Results.Ok(conflicts.Select(item => new OfflineConflictResponse(item.Id, item.CourseId, item.LessonId, item.ClientOccurredAtUtc, item.ServerUpdatedAtUtc, item.ClientPositionSeconds, item.ClientStatus, item.ServerPositionSeconds, item.ServerStatus, item.Status)).ToArray());
    }

    private static async Task<IResult> SyncAsync(HttpContext httpContext, LmsDbContext db, Guid deviceId, OfflineSyncRequest request, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var device = await FindDeviceAsync(httpContext, db, deviceId, userId, cancellationToken);
        if (device is null) return Results.Unauthorized();
        if (request.Events is null || request.Events.Length == 0 || request.Events.Length > 100) return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Events)] = ["Submit between 1 and 100 offline progress events."] });
        var applied = 0;
        var conflicts = new List<OfflineConflictResponse>();
        foreach (var item in request.Events)
        {
            if (item.CourseId == Guid.Empty || item.LessonId == Guid.Empty || string.IsNullOrWhiteSpace(item.IdempotencyKey) || item.IdempotencyKey.Trim().Length > 120) continue;
            var eventKey = $"OFFLINE:{device.Id:D}:{item.IdempotencyKey.Trim()}";
            if (await db.LearningProgressEvents.AnyAsync(progressEvent => progressEvent.EnrollmentId != Guid.Empty && progressEvent.IdempotencyKey == eventKey, cancellationToken)) continue;
            var enrollment = await db.Enrollments.SingleOrDefaultAsync(enrollment => enrollment.CourseId == item.CourseId && enrollment.LearnerUserId == userId && (enrollment.Status == EnrollmentStatus.Active || enrollment.Status == EnrollmentStatus.Completed), cancellationToken);
            var course = await db.Courses.SingleOrDefaultAsync(course => course.Id == item.CourseId && course.Status == CourseStatus.Published, cancellationToken);
            if (enrollment is null || course?.CurrentVersionId is not Guid versionId || !await db.CourseLessons.AnyAsync(lesson => lesson.Id == item.LessonId && db.CourseModules.Any(module => module.Id == lesson.CourseModuleId && module.CourseVersionId == versionId), cancellationToken)) continue;
            var progress = await db.LessonProgress.SingleOrDefaultAsync(progress => progress.EnrollmentId == enrollment.Id && progress.LessonId == item.LessonId, cancellationToken);
            if (progress is not null && progress.UpdatedAtUtc > item.OccurredAtUtc)
            {
                var conflict = new OfflineSyncConflict
                {
                    Id = Guid.NewGuid(), TenantId = device.TenantId, OfflineDeviceId = device.Id, UserId = userId,
                    CourseId = item.CourseId, LessonId = item.LessonId, ClientOccurredAtUtc = item.OccurredAtUtc,
                    ServerUpdatedAtUtc = progress.UpdatedAtUtc, ClientPositionSeconds = Math.Max(0, item.PositionSeconds), ClientStatus = item.Status,
                    ServerPositionSeconds = progress.PositionSeconds, ServerStatus = progress.Status.ToString(), Status = "Open", CreatedAtUtc = DateTimeOffset.UtcNow
                };
                db.OfflineSyncConflicts.Add(conflict);
                conflicts.Add(new OfflineConflictResponse(conflict.Id, conflict.CourseId, conflict.LessonId, conflict.ClientOccurredAtUtc, conflict.ServerUpdatedAtUtc, conflict.ClientPositionSeconds, conflict.ClientStatus, conflict.ServerPositionSeconds, conflict.ServerStatus, conflict.Status));
                continue;
            }
            var status = ParseStatus(item.Status);
            if (status is null) continue;
            var now = DateTimeOffset.UtcNow;
            var isNewProgress = progress is null;
            progress ??= new LessonProgress { Id = Guid.NewGuid(), TenantId = device.TenantId, EnrollmentId = enrollment.Id, CourseId = item.CourseId, LearnerUserId = userId, LessonId = item.LessonId, Status = LessonProgressStatus.NotStarted, UpdatedAtUtc = now };
            if (isNewProgress) db.LessonProgress.Add(progress);
            progress.Status = status.Value;
            progress.PositionSeconds = Math.Max(0, item.PositionSeconds);
            progress.LastViewedAtUtc = now;
            progress.CompletedAtUtc = status == LessonProgressStatus.Completed ? progress.CompletedAtUtc ?? now : progress.CompletedAtUtc;
            progress.UpdatedAtUtc = now;
            enrollment.LastAccessedAtUtc = now;
            enrollment.UpdatedAtUtc = now;
            db.LearningProgressEvents.Add(new LearningProgressEvent { Id = Guid.NewGuid(), TenantId = device.TenantId, EnrollmentId = enrollment.Id, CourseId = item.CourseId, LearnerUserId = userId, LessonId = item.LessonId, EventType = status == LessonProgressStatus.Completed ? LearningProgressEventType.LessonCompleted : LearningProgressEventType.LessonResumed, PositionSeconds = progress.PositionSeconds, IdempotencyKey = eventKey, OccurredAtUtc = item.OccurredAtUtc });
            applied++;
        }
        device.LastSeenAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new OfflineSyncResponse(applied, conflicts.ToArray()));
    }

    private static async Task<IResult> GetPackageAsync(HttpContext httpContext, LmsDbContext db, Guid courseId, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var device = await FindDeviceFromHeadersAsync(httpContext, db, userId, cancellationToken);
        if (device is null) return Results.Unauthorized();
        var course = await db.Courses.AsNoTracking().SingleOrDefaultAsync(item => item.Id == courseId && item.Status == CourseStatus.Published, cancellationToken);
        if (course is null || course.CurrentVersionId is not Guid versionId) return Results.NotFound();
        if (!await db.Enrollments.AnyAsync(item => item.CourseId == courseId && item.LearnerUserId == userId && (item.Status == EnrollmentStatus.Active || item.Status == EnrollmentStatus.Completed), cancellationToken)) return Results.NotFound(new { message = "An active enrollment is required for an offline package." });
        var license = await db.OfflinePackageLicenses.SingleOrDefaultAsync(item => item.OfflineDeviceId == device.Id && item.CourseId == courseId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (license is null)
        {
            var activePackages = await db.OfflinePackageLicenses.CountAsync(item => item.OfflineDeviceId == device.Id && item.RevokedAtUtc == null && item.ExpiresAtUtc > now, cancellationToken);
            if (activePackages >= device.MaxActivePackages) return Results.Conflict(new { message = "This device has reached its active offline package limit." });
            license = new OfflinePackageLicense { Id = Guid.NewGuid(), TenantId = device.TenantId, OfflineDeviceId = device.Id, UserId = userId, CourseId = courseId, IssuedAtUtc = now };
            db.OfflinePackageLicenses.Add(license);
        }
        license.IssuedAtUtc = now;
        license.ExpiresAtUtc = now.AddHours(PackageLifetimeHours);
        license.RevokedAtUtc = null;
        license.DownloadCount++;
        license.LastDownloadedAtUtc = now;
        var payload = await BuildPackageAsync(db, course, versionId, device, license, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(OfflinePackageCipher.Encrypt(JsonSerializer.SerializeToUtf8Bytes(payload), ReadSecret(httpContext), device.Id, courseId, license.ExpiresAtUtc));
    }

    private static async Task<IResult> DownloadEncryptedAssetAsync(HttpContext httpContext, LmsDbContext db, IContentAssetStorage storage, Guid courseId, Guid assetId, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var device = await FindDeviceFromHeadersAsync(httpContext, db, userId, cancellationToken);
        if (device is null) return Results.Unauthorized();
        var license = await db.OfflinePackageLicenses.SingleOrDefaultAsync(item => item.OfflineDeviceId == device.Id && item.CourseId == courseId && item.RevokedAtUtc == null && item.ExpiresAtUtc > DateTimeOffset.UtcNow, cancellationToken);
        var asset = await db.ContentAssets.AsNoTracking().SingleOrDefaultAsync(item => item.Id == assetId && item.CourseId == courseId, cancellationToken);
        if (license is null || asset is null) return Results.NotFound();
        await using var stream = await storage.OpenReadAsync(asset.StorageKey, cancellationToken);
        if (stream is null) return Results.NotFound();
        await using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return Results.Ok(OfflinePackageCipher.Encrypt(buffer.ToArray(), ReadSecret(httpContext), device.Id, assetId, license.ExpiresAtUtc));
    }

    private static async Task<OfflinePackagePayload> BuildPackageAsync(LmsDbContext db, Course course, Guid versionId, OfflineDevice device, OfflinePackageLicense license, CancellationToken cancellationToken)
    {
        var modules = await db.CourseModules.AsNoTracking().Where(item => item.CourseVersionId == versionId).OrderBy(item => item.DisplayOrder).ToListAsync(cancellationToken);
        var moduleIds = modules.Select(item => item.Id).ToArray();
        var lessons = await db.CourseLessons.AsNoTracking().Where(item => moduleIds.Contains(item.CourseModuleId)).OrderBy(item => item.DisplayOrder).ToListAsync(cancellationToken);
        var assets = await db.ContentAssets.AsNoTracking().Where(item => item.CourseId == course.Id && (item.CourseVersionId == versionId || item.CourseVersionId == null)).OrderBy(item => item.CreatedAtUtc).ToListAsync(cancellationToken);
        return new OfflinePackagePayload(course.Id, course.Code, course.Title, license.ExpiresAtUtc, modules.Select(module => new OfflinePackageModule(module.Id, module.Title, module.Description, lessons.Where(lesson => lesson.CourseModuleId == module.Id).Select(lesson => new OfflinePackageLesson(lesson.Id, lesson.Title, lesson.Summary, lesson.ContentHtml, lesson.DisplayOrder)).ToArray())).ToArray(), assets.Select(asset => new OfflinePackageAsset(asset.Id, asset.OriginalFileName, asset.ContentType, asset.SizeBytes, asset.Sha256, $"/api/v1/tenant/offline/courses/{course.Id:D}/assets/{asset.Id:D}")).ToArray());
    }

    private static async Task<OfflineDevice?> FindDeviceFromHeadersAsync(HttpContext httpContext, LmsDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        return Guid.TryParse(httpContext.Request.Headers["X-Offline-Device-Id"].FirstOrDefault(), out var deviceId)
            ? await FindDeviceAsync(httpContext, db, deviceId, userId, cancellationToken)
            : null;
    }

    private static async Task<OfflineDevice?> FindDeviceAsync(HttpContext httpContext, LmsDbContext db, Guid deviceId, Guid userId, CancellationToken cancellationToken)
    {
        var device = await db.OfflineDevices.SingleOrDefaultAsync(item => item.Id == deviceId && item.UserId == userId && item.Status == OfflineDeviceStatus.Active, cancellationToken);
        var secret = ReadSecret(httpContext);
        if (device is null || string.IsNullOrWhiteSpace(secret) || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(device.SecretHash), Convert.FromHexString(Hash(secret)))) return null;
        device.LastSeenAtUtc = DateTimeOffset.UtcNow;
        return device;
    }

    private static string ReadSecret(HttpContext httpContext) => httpContext.Request.Headers["X-Offline-Device-Secret"].FirstOrDefault() ?? string.Empty;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static OfflineDeviceResponse ToDeviceResponse(OfflineDevice item) => new(item.Id, item.Name, item.Status.ToString(), item.MaxActivePackages, item.CreatedAtUtc, item.LastSeenAtUtc, item.RevokedAtUtc);
    private static Guid? GetUserId(HttpContext httpContext) => Guid.TryParse(httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    private static LessonProgressStatus? ParseStatus(string? value) => value?.Trim().ToLowerInvariant() switch { "inprogress" or "in_progress" or "started" => LessonProgressStatus.InProgress, "completed" or "complete" => LessonProgressStatus.Completed, _ => null };

}

public sealed record RegisterOfflineDeviceRequest(string Name, string Fingerprint);
public sealed record OfflineDeviceRegistrationResponse(Guid Id, string Name, string DeviceSecret, int MaxActivePackages, DateTimeOffset CreatedAtUtc);
public sealed record OfflineDeviceResponse(Guid Id, string Name, string Status, int MaxActivePackages, DateTimeOffset CreatedAtUtc, DateTimeOffset LastSeenAtUtc, DateTimeOffset? RevokedAtUtc);
public sealed record OfflineSyncRequest(OfflineSyncEventRequest[] Events);
public sealed record OfflineSyncEventRequest(Guid CourseId, Guid LessonId, string Status, int PositionSeconds, DateTimeOffset OccurredAtUtc, string IdempotencyKey);
public sealed record OfflineSyncResponse(int AppliedCount, OfflineConflictResponse[] Conflicts);
public sealed record OfflineConflictResponse(Guid Id, Guid CourseId, Guid LessonId, DateTimeOffset ClientOccurredAtUtc, DateTimeOffset ServerUpdatedAtUtc, int ClientPositionSeconds, string ClientStatus, int ServerPositionSeconds, string ServerStatus, string Status);
public sealed record OfflinePackagePayload(Guid CourseId, string CourseCode, string CourseTitle, DateTimeOffset ExpiresAtUtc, OfflinePackageModule[] Modules, OfflinePackageAsset[] Assets);
public sealed record OfflinePackageModule(Guid Id, string Title, string? Description, OfflinePackageLesson[] Lessons);
public sealed record OfflinePackageLesson(Guid Id, string Title, string? Summary, string? ContentHtml, int DisplayOrder);
public sealed record OfflinePackageAsset(Guid Id, string OriginalFileName, string ContentType, long SizeBytes, string Sha256, string DownloadUrl);
