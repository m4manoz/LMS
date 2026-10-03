using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Lms.Api.Domain.AI;
using Lms.Api.Domain.Courses;
using Lms.Api.Domain.Identity;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.AI;

public static class AiEndpoints
{
    public static void MapAiEndpoints(this WebApplication app)
    {
        var tenant = app.MapGroup("/api/v1/tenant/ai").RequireAuthorization("tenant.authenticated");
        tenant.AddEndpointFilter(async (context, next) =>
        {
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (!tenantContext.IsResolved)
                return Results.BadRequest(new { message = "A tenant is required. Use the X-Tenant-Slug header or a tenant subdomain." });
            return await next(context);
        });

        tenant.MapGet("/jobs", ListJobsAsync).RequireAuthorization("tenant.ai.use");
        tenant.MapGet("/jobs/{jobId:guid}", GetJobAsync).RequireAuthorization("tenant.ai.use");
        tenant.MapPost("/jobs", CreateJobAsync).RequireAuthorization("tenant.ai.use");
        tenant.MapPost("/outputs/{outputId:guid}/reviews", ReviewOutputAsync).RequireAuthorization("tenant.ai.manage");
        tenant.MapPost("/outputs/{outputId:guid}/regenerate", RegenerateAsync).RequireAuthorization("tenant.ai.manage");
    }

    private static async Task<IResult> ListJobsAsync(HttpContext httpContext, LmsDbContext db, CancellationToken cancellationToken)
    {
        var userId = GetUserId(httpContext);
        if (userId is not Guid requesterId) return Results.Unauthorized();
        var canManage = HasPermission(httpContext, LmsPermissions.AiManage);
        var query = db.AiJobs.AsNoTracking().AsQueryable();
        if (!canManage) query = query.Where(item => item.RequestedByUserId == requesterId);
        var jobs = await query.OrderByDescending(item => item.CreatedAtUtc)
            .Take(100)
            .Select(item => new AiJobSummaryResponse(
                item.Id, item.Feature.ToString(), item.Status.ToString(), item.CourseId,
                item.Provider, item.Model, item.AttemptCount, item.LastError,
                item.CreatedAtUtc, item.CompletedAtUtc,
                db.AiOutputs.Count(output => output.JobId == item.Id)))
            .ToListAsync(cancellationToken);
        return Results.Ok(jobs);
    }

    private static async Task<IResult> GetJobAsync(HttpContext httpContext, LmsDbContext db, Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.AiJobs.AsNoTracking().SingleOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        if (job is null) return Results.NotFound();
        if (!HasPermission(httpContext, LmsPermissions.AiManage) && job.RequestedByUserId != GetUserId(httpContext))
            return Results.Forbid();
        return Results.Ok(await BuildJobResponseAsync(db, job, cancellationToken));
    }

    private static async Task<IResult> CreateJobAsync(
        HttpContext httpContext,
        LmsDbContext db,
        ITenantContext tenantContext,
        CreateAiJobRequest request,
        CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || GetUserId(httpContext) is not Guid userId)
            return Results.BadRequest(new { message = "A tenant and authenticated user are required." });
        if (!Enum.TryParse<AiFeatureType>(request.Feature, true, out var feature))
            return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Feature)] = ["Choose a supported AI feature."] });
        if (string.IsNullOrWhiteSpace(request.Instruction) || request.Instruction.Length > 4000)
            return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Instruction)] = ["Instruction is required and must be at most 4,000 characters."] });
        if (request.CourseId is not Guid courseId)
            return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.CourseId)] = ["AI drafts must be grounded in a published course."] });

        var course = await db.Courses.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == courseId && item.Status == CourseStatus.Published, cancellationToken);
        if (course is null) return Results.NotFound();

        var activeCount = await db.AiJobs.CountAsync(item =>
            (item.Status == AiJobStatus.Pending || item.Status == AiJobStatus.Processing)
            || (item.Status == AiJobStatus.Failed && item.NextAttemptAtUtc > DateTimeOffset.UtcNow.AddMinutes(-1)), cancellationToken);
        if (activeCount >= 25)
            return Results.Problem("The AI queue is busy. Try again shortly.", statusCode: StatusCodes.Status429TooManyRequests);

        var language = string.IsNullOrWhiteSpace(request.OutputLanguage) ? "en" : request.OutputLanguage.Trim().ToLowerInvariant();
        if (language.Length > 20) return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.OutputLanguage)] = ["Output language must be at most 20 characters."] });
        var now = DateTimeOffset.UtcNow;
        var job = new AiJob
        {
            Id = Guid.NewGuid(), TenantId = tenantId, RequestedByUserId = userId, CourseId = courseId,
            Feature = feature, Instruction = request.Instruction.Trim(), OutputLanguage = language,
            Status = AiJobStatus.Pending, InputHash = CreateInputHash(course, feature, request.Instruction, language),
            AttemptCount = 0, MaxAttempts = 3, CreatedAtUtc = now, NextAttemptAtUtc = now
        };
        db.AiJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/ai/jobs/{job.Id:D}", ToSummary(job, 0));
    }

    private static async Task<IResult> ReviewOutputAsync(
        HttpContext httpContext,
        LmsDbContext db,
        Guid outputId,
        ReviewAiOutputRequest request,
        CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid reviewerId) return Results.Unauthorized();
        if (!Enum.TryParse<AiReviewDecision>(request.Decision, true, out var decision))
            return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Decision)] = ["Decision must be Approved or Rejected."] });
        if (request.Notes?.Length > 2000)
            return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Notes)] = ["Review notes must be at most 2,000 characters."] });

        var output = await db.AiOutputs.SingleOrDefaultAsync(item => item.Id == outputId, cancellationToken);
        if (output is null) return Results.NotFound();
        output.Status = decision == AiReviewDecision.Approved ? AiOutputStatus.Approved : AiOutputStatus.Rejected;
        output.ApprovedAtUtc = decision == AiReviewDecision.Approved ? DateTimeOffset.UtcNow : null;
        var review = new AiReview
        {
            Id = Guid.NewGuid(), TenantId = output.TenantId, AiOutputId = output.Id,
            ReviewerUserId = reviewerId, Decision = decision, Notes = request.Notes?.Trim(), CreatedAtUtc = DateTimeOffset.UtcNow
        };
        db.AiReviews.Add(review);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new AiReviewResponse(review.Id, review.Decision.ToString(), review.Notes, review.ReviewerUserId, review.CreatedAtUtc, output.Status.ToString()));
    }

    private static async Task<IResult> RegenerateAsync(HttpContext httpContext, LmsDbContext db, Guid outputId, CancellationToken cancellationToken)
    {
        var output = await db.AiOutputs.AsNoTracking().SingleOrDefaultAsync(item => item.Id == outputId, cancellationToken);
        if (output is null) return Results.NotFound();
        var original = await db.AiJobs.AsNoTracking().SingleOrDefaultAsync(item => item.Id == output.JobId, cancellationToken);
        if (original is null || original.CourseId is not Guid courseId) return Results.Conflict(new { message = "The original AI job cannot be regenerated." });
        var course = await db.Courses.AsNoTracking().SingleOrDefaultAsync(item => item.Id == courseId && item.Status == CourseStatus.Published, cancellationToken);
        if (course is null) return Results.Conflict(new { message = "The source course is no longer published." });
        var now = DateTimeOffset.UtcNow;
        var job = new AiJob
        {
            Id = Guid.NewGuid(), TenantId = original.TenantId, RequestedByUserId = GetUserId(httpContext)!.Value,
            CourseId = original.CourseId, Feature = original.Feature, Instruction = original.Instruction,
            OutputLanguage = original.OutputLanguage, Status = AiJobStatus.Pending, InputHash = CreateInputHash(course, original.Feature, original.Instruction, original.OutputLanguage),
            MaxAttempts = 3, CreatedAtUtc = now, NextAttemptAtUtc = now
        };
        db.AiJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Accepted($"/api/v1/tenant/ai/jobs/{job.Id:D}", ToSummary(job, 0));
    }

    private static async Task<AiJobResponse> BuildJobResponseAsync(LmsDbContext db, AiJob job, CancellationToken cancellationToken)
    {
        var outputs = await db.AiOutputs.AsNoTracking().Where(item => item.JobId == job.Id).OrderByDescending(item => item.CreatedAtUtc).ToListAsync(cancellationToken);
        var outputIds = outputs.Select(item => item.Id).ToArray();
        var citations = await db.AiCitations.AsNoTracking().Where(item => outputIds.Contains(item.AiOutputId)).ToListAsync(cancellationToken);
        var reviews = await db.AiReviews.AsNoTracking().Where(item => outputIds.Contains(item.AiOutputId)).OrderByDescending(item => item.CreatedAtUtc).ToListAsync(cancellationToken);
        return new AiJobResponse(
            job.Id, job.Feature.ToString(), job.Status.ToString(), job.CourseId, job.RequestedByUserId,
            job.Instruction, job.OutputLanguage, job.Provider, job.Model, job.AttemptCount, job.LastError,
            job.CreatedAtUtc, job.CompletedAtUtc,
            outputs.Select(output => new AiOutputResponse(
                output.Id, output.Feature.ToString(), output.Title, output.Content, output.Provider, output.Model,
                output.Status.ToString(), output.CreatedAtUtc, output.ApprovedAtUtc,
                citations.Where(item => item.AiOutputId == output.Id).Select(item => new AiCitationResponse(item.Id, item.SourceType, item.SourceId, item.SourceTitle, item.Locator, item.Excerpt)).ToArray(),
                reviews.Where(item => item.AiOutputId == output.Id).Select(item => new AiReviewResponse(item.Id, item.Decision.ToString(), item.Notes, item.ReviewerUserId, item.CreatedAtUtc, output.Status.ToString())).ToArray())).ToArray());
    }

    private static AiJobSummaryResponse ToSummary(AiJob job, int outputCount)
        => new(job.Id, job.Feature.ToString(), job.Status.ToString(), job.CourseId, job.Provider, job.Model, job.AttemptCount, job.LastError, job.CreatedAtUtc, job.CompletedAtUtc, outputCount);

    private static string CreateInputHash(Course course, AiFeatureType feature, string instruction, string language)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{course.Id}|{course.CurrentVersionId}|{feature}|{language}|{instruction.Trim()}")));

    private static Guid? GetUserId(HttpContext context)
        => Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub"), out var id) ? id : null;

    private static bool HasPermission(HttpContext context, string permission)
        => context.User.Claims.Any(claim => claim.Type == "permission" && claim.Value.Equals(permission, StringComparison.OrdinalIgnoreCase));
}

public sealed record CreateAiJobRequest(string Feature, Guid? CourseId, string Instruction, string? OutputLanguage);
public sealed record ReviewAiOutputRequest(string Decision, string? Notes);
public sealed record AiJobSummaryResponse(Guid Id, string Feature, string Status, Guid? CourseId, string? Provider, string? Model, int AttemptCount, string? LastError, DateTimeOffset CreatedAtUtc, DateTimeOffset? CompletedAtUtc, int OutputCount);
public sealed record AiJobResponse(Guid Id, string Feature, string Status, Guid? CourseId, Guid RequestedByUserId, string Instruction, string OutputLanguage, string? Provider, string? Model, int AttemptCount, string? LastError, DateTimeOffset CreatedAtUtc, DateTimeOffset? CompletedAtUtc, AiOutputResponse[] Outputs);
public sealed record AiOutputResponse(Guid Id, string Feature, string Title, string Content, string Provider, string Model, string Status, DateTimeOffset CreatedAtUtc, DateTimeOffset? ApprovedAtUtc, AiCitationResponse[] Citations, AiReviewResponse[] Reviews);
public sealed record AiCitationResponse(Guid Id, string SourceType, Guid SourceId, string SourceTitle, string? Locator, string? Excerpt);
public sealed record AiReviewResponse(Guid Id, string Decision, string? Notes, Guid ReviewerUserId, DateTimeOffset CreatedAtUtc, string OutputStatus);
