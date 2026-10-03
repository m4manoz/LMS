using Lms.Api.Domain.AI;
using Lms.Api.Domain.Courses;
using Lms.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Infrastructure.AI;

public sealed class AiWorker(
    IServiceScopeFactory scopeFactory,
    IAiProvider provider,
    ILogger<AiWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessBatchAsync(stoppingToken); }
            catch (Exception exception) { logger.LogError(exception, "AI worker failed while processing a batch."); }
            try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }

    private async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LmsDbContext>();
        var now = DateTimeOffset.UtcNow;
        var jobs = await db.AiJobs.IgnoreQueryFilters()
            .Where(item => (item.Status == AiJobStatus.Pending || item.Status == AiJobStatus.Failed)
                && item.AttemptCount < item.MaxAttempts
                && (item.NextAttemptAtUtc == null || item.NextAttemptAtUtc <= now))
            .OrderBy(item => item.CreatedAtUtc)
            .Take(10)
            .ToListAsync(cancellationToken);

        foreach (var job in jobs)
        {
            job.Status = AiJobStatus.Processing;
            job.AttemptCount++;
            job.StartedAtUtc = DateTimeOffset.UtcNow;
            job.LastError = null;
            await db.SaveChangesAsync(cancellationToken);

            try
            {
                var request = await BuildRequestAsync(db, job, cancellationToken);
                var result = await provider.GenerateAsync(request, cancellationToken);
                var output = new AiOutput
                {
                    Id = Guid.NewGuid(), TenantId = job.TenantId, JobId = job.Id, CourseId = job.CourseId,
                    Feature = job.Feature, Title = AiTextSafety.RedactAndLimit(result.Title, 250),
                    Content = AiTextSafety.RedactAndLimit(result.Content, 20000), StructuredJson = AiTextSafety.RedactAndLimit(result.StructuredJson, 50000),
                    Provider = result.Provider, Model = result.Model, InputHash = job.InputHash,
                    Status = AiOutputStatus.Draft, CreatedAtUtc = DateTimeOffset.UtcNow
                };
                db.AiOutputs.Add(output);
                db.AiCitations.AddRange(result.Citations.Select(citation => new AiCitation
                {
                    Id = Guid.NewGuid(), TenantId = job.TenantId, AiOutputId = output.Id,
                    SourceType = "course-lesson", SourceId = citation.SourceId,
                    SourceTitle = AiTextSafety.RedactAndLimit(citation.SourceTitle, 250),
                    Locator = AiTextSafety.RedactAndLimit(citation.Locator, 250),
                    Excerpt = AiTextSafety.RedactAndLimit(citation.Excerpt, 1000)
                }));
                job.Provider = result.Provider;
                job.Model = result.Model;
                job.Status = AiJobStatus.Completed;
                job.CompletedAtUtc = DateTimeOffset.UtcNow;
                job.NextAttemptAtUtc = null;
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                job.Status = job.AttemptCount >= job.MaxAttempts ? AiJobStatus.DeadLetter : AiJobStatus.Failed;
                job.LastError = AiTextSafety.RedactAndLimit(exception.Message, 4000);
                job.NextAttemptAtUtc = job.Status == AiJobStatus.Failed
                    ? DateTimeOffset.UtcNow.AddSeconds(Math.Min(60, job.AttemptCount * 10))
                    : null;
                await db.SaveChangesAsync(cancellationToken);
                logger.LogWarning(exception, "AI job {JobId} failed on attempt {AttemptCount}.", job.Id, job.AttemptCount);
            }
        }
    }

    private static async Task<AiGenerationRequest> BuildRequestAsync(LmsDbContext db, AiJob job, CancellationToken cancellationToken)
    {
        if (job.CourseId is not Guid courseId)
            throw new InvalidOperationException("AI jobs must be grounded in a published course.");

        var course = await db.Courses.IgnoreQueryFilters()
            .SingleOrDefaultAsync(item => item.Id == courseId && item.TenantId == job.TenantId && item.Status == CourseStatus.Published, cancellationToken);
        if (course is null || course.CurrentVersionId is not Guid versionId)
            throw new InvalidOperationException("The selected course is not currently published.");

        var lessons = await db.CourseLessons.IgnoreQueryFilters()
            .Where(item => item.TenantId == job.TenantId && item.CourseModuleId != Guid.Empty
                && db.CourseModules.IgnoreQueryFilters().Any(module => module.Id == item.CourseModuleId
                    && module.TenantId == job.TenantId && module.CourseVersionId == versionId))
            .OrderBy(item => item.DisplayOrder)
            .Take(20)
            .Select(item => new AiContextItem(item.Id, item.Title,
                AiTextSafety.RedactAndLimit($"{item.Summary}\n{item.ContentHtml}", 1200)))
            .ToListAsync(cancellationToken);

        return new AiGenerationRequest(
            job.Feature,
            AiTextSafety.RedactAndLimit(job.Instruction, 4000),
            AiTextSafety.RedactAndLimit(job.OutputLanguage, 20),
            AiTextSafety.RedactAndLimit(course.Title, 250),
            lessons.Where(item => !string.IsNullOrWhiteSpace(item.Content)).ToArray());
    }
}
