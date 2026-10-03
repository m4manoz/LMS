using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Text.Json;
using Lms.Api.Domain.Assessments;
using Lms.Api.Domain.Courses;
using Lms.Api.Domain.Videos;
using Lms.Api.Infrastructure.LiveClasses;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Tenancy;
using Lms.Api.Infrastructure.Videos;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Videos;

/// <summary>
/// What is said in a video, made searchable and useful: transcripts (pasted or made by a speech-to-text service), search that jumps to the moment,
/// and summaries and practice questions written from the transcript. Written text is a draft until staff publish it.
/// </summary>
public static class VideoAiEndpoints
{
    private const int MaxTranscriptCharacters = 2_000_000;
    private const int MaxInsightsPerKind = 10;
    private static readonly Regex ModelName = new(@"^[A-Za-z0-9][A-Za-z0-9._:/\-]{0,99}$", RegexOptions.Compiled);

    public static void MapVideoAiEndpoints(this WebApplication app)
    {
        var videos = app.MapGroup("/api/v1/tenant/videos").RequireAuthorization("tenant.authenticated");
        videos.AddEndpointFilter(RequireTenantAsync);
        videos.MapGet("/search", SearchAsync).RequireAuthorization("tenant.course.read");
        videos.MapGet("/{videoId:guid}/transcript", GetTranscriptAsync).RequireAuthorization("tenant.course.read");
        videos.MapPost("/{videoId:guid}/transcript", SaveTranscriptAsync).RequireAuthorization("tenant.course.manage");
        videos.MapPost("/{videoId:guid}/transcript/generate", GenerateTranscriptAsync).RequireAuthorization("tenant.course.manage");
        videos.MapDelete("/{videoId:guid}/transcript", DeleteTranscriptAsync).RequireAuthorization("tenant.course.manage");
        videos.MapGet("/{videoId:guid}/insights", ListInsightsAsync).RequireAuthorization("tenant.course.read");
        videos.MapPost("/{videoId:guid}/insights", GenerateInsightAsync).RequireAuthorization("tenant.course.manage");
        videos.MapPut("/{videoId:guid}/insights/{insightId:guid}", UpdateInsightAsync).RequireAuthorization("tenant.course.manage");
        videos.MapPost("/{videoId:guid}/insights/{insightId:guid}/publish", PublishInsightAsync).RequireAuthorization("tenant.course.manage");
        videos.MapDelete("/{videoId:guid}/insights/{insightId:guid}", DeleteInsightAsync).RequireAuthorization("tenant.course.manage");
        videos.MapPost("/{videoId:guid}/insights/{insightId:guid}/quiz", CreateQuizAsync).RequireAuthorization("tenant.assessment.manage");

        var settings = app.MapGroup("/api/v1/tenant/integrations/video-ai").RequireAuthorization("tenant.authenticated");
        settings.AddEndpointFilter(RequireTenantAsync);
        settings.MapGet("", GetSettingsAsync).RequireAuthorization("tenant.tenant.manage");
        settings.MapPut("", SaveSettingsAsync).RequireAuthorization("tenant.tenant.manage");
    }

    private static async ValueTask<object?> RequireTenantAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        => context.HttpContext.RequestServices.GetRequiredService<ITenantContext>().IsResolved
            ? await next(context)
            : Results.BadRequest(new { message = "A tenant is required. Use the X-Tenant-Slug header or a tenant subdomain." });

    private static async Task<Video?> FindVisibleAsync(HttpContext httpContext, LmsDbContext db, Guid userId, Guid videoId, CancellationToken cancellationToken)
        => await (await VideoEndpoints.VisibleAsync(httpContext, db, userId, cancellationToken)).SingleOrDefaultAsync(item => item.Id == videoId, cancellationToken);

    // ---------- search ----------
    private static async Task<IResult> SearchAsync(HttpContext httpContext, LmsDbContext db, string? q, Guid? courseId, CancellationToken cancellationToken)
    {
        if (VideoEndpoints.GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var words = Regex.Split((q ?? string.Empty).Trim().ToLowerInvariant(), @"\s+").Where(word => word.Length > 0).Take(6).ToList();
        if (string.Join(' ', words).Length is < 2 or > 100) return VideoEndpoints.Problem("Type between 2 and 100 characters to search for.");

        var visible = await VideoEndpoints.VisibleAsync(httpContext, db, userId, cancellationToken);
        if (courseId is Guid course) visible = visible.Where(item => item.CourseId == course);
        var videos = await visible.Select(item => new { item.Id, item.Title, item.CourseId }).ToListAsync(cancellationToken);
        var ids = videos.Select(item => item.Id).ToList();
        var ready = await db.VideoTranscripts.AsNoTracking().Where(item => item.Status == TranscriptStatus.Ready && ids.Contains(item.VideoId)).Select(item => item.VideoId).ToListAsync(cancellationToken);

        var query = db.VideoTranscriptSegments.AsNoTracking().Where(item => ready.Contains(item.VideoId));
        foreach (var word in words) { var needle = word; query = query.Where(item => item.Text.ToLower().Contains(needle)); }
        var hits = await query.OrderBy(item => item.VideoId).ThenBy(item => item.StartMs).Take(60).ToListAsync(cancellationToken);

        var courses = await db.Courses.AsNoTracking().Where(item => videos.Select(v => v.CourseId).Contains(item.Id)).ToDictionaryAsync(item => item.Id, item => item.Title, cancellationToken);
        var byId = videos.ToDictionary(item => item.Id);
        return Results.Ok(hits.Select(hit => new SearchHit(hit.VideoId, byId[hit.VideoId].Title, courses.GetValueOrDefault(byId[hit.VideoId].CourseId, "Course"), hit.StartMs / 1000.0, hit.Text)).ToList());
    }

    // ---------- transcripts ----------
    private static async Task<IResult> GetTranscriptAsync(HttpContext httpContext, LmsDbContext db, Guid videoId, CancellationToken cancellationToken)
    {
        if (VideoEndpoints.GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        if (await FindVisibleAsync(httpContext, db, userId, videoId, cancellationToken) is null) return Results.NotFound();
        return Results.Ok(await ReadTranscriptAsync(db, videoId, VideoEndpoints.CanManage(httpContext), cancellationToken));
    }

    private static async Task<TranscriptResponse> ReadTranscriptAsync(LmsDbContext db, Guid videoId, bool staff, CancellationToken cancellationToken)
    {
        var transcript = await db.VideoTranscripts.AsNoTracking().SingleOrDefaultAsync(item => item.VideoId == videoId, cancellationToken);
        if (transcript is null || (!staff && transcript.Status != TranscriptStatus.Ready)) return new TranscriptResponse("None", null, null, null, null, null);
        var segments = transcript.Status == TranscriptStatus.Ready
            ? (await db.VideoTranscriptSegments.AsNoTracking().Where(item => item.VideoId == videoId).OrderBy(item => item.Index).ToListAsync(cancellationToken))
                .Select(item => new SegmentResponse(item.Index, item.StartMs / 1000.0, item.EndMs / 1000.0, item.Text)).ToList()
            : null;
        return new TranscriptResponse(transcript.Status.ToString(), transcript.Source, transcript.Language, transcript.Provider, transcript.StatusMessage, segments);
    }

    private static async Task<IResult> SaveTranscriptAsync(HttpContext httpContext, LmsDbContext db, ITenantContext tenantContext, Guid videoId, SaveTranscriptRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || VideoEndpoints.GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        if (!await db.Videos.AnyAsync(item => item.Id == videoId, cancellationToken)) return Results.NotFound();
        if (request.Text is { Length: > MaxTranscriptCharacters }) return VideoEndpoints.Problem("The transcript is too large.");
        if (request.Language is { Length: > 40 }) return VideoEndpoints.Problem("The language must be 40 characters or fewer.");
        var cues = TranscriptParser.Parse(request.Text, out var error);
        if (cues is null) return VideoEndpoints.Problem(error!);
        await TranscriptStore.ReplaceAsync(db, tenantId, videoId, cues, "Manual", request.Language?.Trim(), null, userId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(await ReadTranscriptAsync(db, videoId, true, cancellationToken));
    }

    private static async Task<IResult> GenerateTranscriptAsync(HttpContext httpContext, LmsDbContext db, ITenantContext tenantContext, IVideoTranscoder transcoder, VideoAiResolver resolver, Guid videoId, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || VideoEndpoints.GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var video = await db.Videos.AsNoTracking().SingleOrDefaultAsync(item => item.Id == videoId, cancellationToken);
        if (video is null) return Results.NotFound();
        if (video.Type == VideoType.External) return VideoEndpoints.Problem("Only uploaded videos and class recordings can be transcribed automatically. Paste a transcript for a linked video.");
        var client = await resolver.ResolveAsync(db, cancellationToken);
        if (!client.CanTranscribe) return Results.Conflict(new { message = "No speech-to-text service is set up. Add one under Integrations → Video AI, or paste a transcript." });
        if (!transcoder.Enabled) return Results.Conflict(new { message = "Video conversion (FFmpeg) is not set up on this server, so the sound cannot be extracted." });
        var existing = await db.VideoTranscripts.AsNoTracking().SingleOrDefaultAsync(item => item.VideoId == videoId, cancellationToken);
        if (existing is { Status: TranscriptStatus.Queued or TranscriptStatus.Processing }) return Results.Conflict(new { message = "A transcript is already being made." });
        await TranscriptStore.QueueAsync(db, tenantId, videoId, client.Name, userId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Accepted($"/api/v1/tenant/videos/{videoId:D}/transcript", await ReadTranscriptAsync(db, videoId, true, cancellationToken));
    }

    private static async Task<IResult> DeleteTranscriptAsync(LmsDbContext db, Guid videoId, CancellationToken cancellationToken)
    {
        var transcript = await db.VideoTranscripts.SingleOrDefaultAsync(item => item.VideoId == videoId, cancellationToken);
        if (transcript is null) return Results.NotFound();
        db.VideoTranscriptSegments.RemoveRange(await db.VideoTranscriptSegments.Where(item => item.VideoId == videoId).ToListAsync(cancellationToken));
        db.VideoTranscripts.Remove(transcript);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    // ---------- summaries and questions ----------
    private static async Task<IResult> ListInsightsAsync(HttpContext httpContext, LmsDbContext db, Guid videoId, CancellationToken cancellationToken)
    {
        if (VideoEndpoints.GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        if (await FindVisibleAsync(httpContext, db, userId, videoId, cancellationToken) is null) return Results.NotFound();
        var query = db.VideoInsights.AsNoTracking().Where(item => item.VideoId == videoId);
        if (!VideoEndpoints.CanManage(httpContext)) query = query.Where(item => item.Published);
        return Results.Ok((await query.OrderBy(item => item.Kind).ThenByDescending(item => item.UpdatedAtUtc).ToListAsync(cancellationToken)).Select(ToResponse).ToList());
    }

    private static async Task<IResult> GenerateInsightAsync(HttpContext httpContext, LmsDbContext db, ITenantContext tenantContext, VideoAiResolver resolver, Guid videoId, GenerateInsightRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || VideoEndpoints.GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var video = await db.Videos.AsNoTracking().SingleOrDefaultAsync(item => item.Id == videoId, cancellationToken);
        if (video is null) return Results.NotFound();
        if (!Enum.TryParse<VideoInsightKind>(request.Kind, true, out var kind) || !Enum.IsDefined(kind)) return VideoEndpoints.Problem("Choose Summary or Questions.");
        if (await db.VideoInsights.CountAsync(item => item.VideoId == videoId && item.Kind == kind, cancellationToken) >= MaxInsightsPerKind)
            return Results.Conflict(new { message = $"There are already {MaxInsightsPerKind} of these. Delete one first." });
        if (!await db.VideoTranscripts.AnyAsync(item => item.VideoId == videoId && item.Status == TranscriptStatus.Ready, cancellationToken))
            return Results.Conflict(new { message = "Add a transcript first: this is written from what is said in the video." });
        var cues = (await db.VideoTranscriptSegments.AsNoTracking().Where(item => item.VideoId == videoId).OrderBy(item => item.Index).ToListAsync(cancellationToken))
            .Select(item => new TranscriptCue(item.StartMs, item.EndMs, item.Text)).ToList();

        var client = await resolver.ResolveAsync(db, cancellationToken);
        string content, model;
        try
        {
            if (kind == VideoInsightKind.Summary) (content, model) = await client.SummarizeAsync(video.Title, cues, cancellationToken);
            else
            {
                var (questions, usedModel) = await client.QuestionsAsync(video.Title, cues, Math.Clamp(request.Count ?? 5, 1, 15), cancellationToken);
                (content, model) = (PracticeQuestions.Serialize(questions), usedModel);
            }
        }
        catch (VideoAiException exception) { return Results.Json(new { message = exception.Message }, statusCode: client.Name == VideoAiProviders.Local ? StatusCodes.Status422UnprocessableEntity : StatusCodes.Status502BadGateway); }

        var now = DateTimeOffset.UtcNow;
        var insight = new VideoInsight { Id = Guid.NewGuid(), TenantId = tenantId, VideoId = videoId, Kind = kind, Content = content, Published = false, Provider = client.Name, Model = model, CreatedByUserId = userId, CreatedAtUtc = now, UpdatedAtUtc = now };
        db.VideoInsights.Add(insight);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/videos/{videoId:D}/insights/{insight.Id:D}", ToResponse(insight));
    }

    private static async Task<IResult> UpdateInsightAsync(LmsDbContext db, Guid videoId, Guid insightId, UpdateInsightRequest request, CancellationToken cancellationToken)
    {
        var insight = await db.VideoInsights.SingleOrDefaultAsync(item => item.Id == insightId && item.VideoId == videoId, cancellationToken);
        if (insight is null) return Results.NotFound();
        if (insight.Kind == VideoInsightKind.Summary)
        {
            if (string.IsNullOrWhiteSpace(request.Content) || request.Content.Length > 20_000) return VideoEndpoints.Problem("Write the summary in 20,000 characters or fewer.");
            insight.Content = request.Content.Trim();
        }
        else
        {
            var questions = request.Questions?.Select(item => item with { Question = item.Question?.Trim() ?? string.Empty, Options = item.Options?.Select(option => option?.Trim() ?? string.Empty).ToArray() ?? [] }).ToList();
            if (PracticeQuestions.Validate(questions) is { } problem) return VideoEndpoints.Problem(problem);
            insight.Content = PracticeQuestions.Serialize(questions!);
        }
        insight.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToResponse(insight));
    }

    private static async Task<IResult> PublishInsightAsync(LmsDbContext db, Guid videoId, Guid insightId, PublishRequest request, CancellationToken cancellationToken)
    {
        var insight = await db.VideoInsights.SingleOrDefaultAsync(item => item.Id == insightId && item.VideoId == videoId, cancellationToken);
        if (insight is null) return Results.NotFound();
        insight.Published = request.Published;
        insight.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToResponse(insight));
    }

    private static async Task<IResult> DeleteInsightAsync(LmsDbContext db, Guid videoId, Guid insightId, CancellationToken cancellationToken)
    {
        var insight = await db.VideoInsights.SingleOrDefaultAsync(item => item.Id == insightId && item.VideoId == videoId, cancellationToken);
        if (insight is null) return Results.NotFound();
        db.VideoInsights.Remove(insight);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>
    /// Turns a published set of practice questions into a real quiz in the course's assessments: multiple-choice questions that are
    /// graded automatically, with attempts, an optional time limit and results in the gradebook. It is a draft until published.
    /// </summary>
    private static async Task<IResult> CreateQuizAsync(HttpContext httpContext, LmsDbContext db, ITenantContext tenantContext, Guid videoId, Guid insightId, CreateQuizRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || VideoEndpoints.GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var insight = await db.VideoInsights.SingleOrDefaultAsync(item => item.Id == insightId && item.VideoId == videoId, cancellationToken);
        var video = await db.Videos.AsNoTracking().SingleOrDefaultAsync(item => item.Id == videoId, cancellationToken);
        if (insight is null || video is null) return Results.NotFound();
        if (insight.Kind != VideoInsightKind.Questions) return VideoEndpoints.Problem("Only practice questions can become a quiz.");
        if (!insight.Published) return Results.Conflict(new { message = "Publish the questions first: only questions you have checked can become a graded quiz." });
        if (insight.QuizAssessmentId is Guid existing && await db.Assessments.AnyAsync(item => item.Id == existing, cancellationToken))
            return Results.Conflict(new { message = "A quiz was already made from these questions. Find it under Assessments.", assessmentId = existing });

        var title = string.IsNullOrWhiteSpace(request.Title) ? $"Quiz: {video.Title}" : request.Title.Trim();
        if (title.Length > 250) return VideoEndpoints.Problem("The quiz title must be 250 characters or fewer.");
        if (request.TimeLimitMinutes is <= 0 or > 1440) return VideoEndpoints.Problem("The time limit must be between 1 and 1,440 minutes.");
        if (request.AttemptLimit is < 1 or > 20) return VideoEndpoints.Problem("The attempt limit must be between 1 and 20.");
        if (request.PointsPerQuestion is < 1 or > 100) return VideoEndpoints.Problem("Points per question must be between 1 and 100.");
        var questions = PracticeQuestions.Deserialize(insight.Content);
        if (PracticeQuestions.Validate(questions) is { } problem) return VideoEndpoints.Problem(problem);

        var course = await db.Courses.SingleOrDefaultAsync(item => item.Id == video.CourseId && item.Status != CourseStatus.Archived, cancellationToken);
        if (course is null || course.CurrentVersionId is not Guid versionId) return Results.NotFound(new { message = "The course or its current version was not found." });
        if (request.Publish == true && course.Status != CourseStatus.Published) return Results.Conflict(new { message = "The course must be published before its quiz can be." });

        var now = DateTimeOffset.UtcNow;
        var points = request.PointsPerQuestion ?? 1;
        var bank = new QuestionBank { Id = Guid.NewGuid(), TenantId = tenantId, CourseId = course.Id, Name = $"{title} question bank", CreatedAtUtc = now, CreatedByUserId = userId };
        var assessment = new Assessment
        {
            Id = Guid.NewGuid(), TenantId = tenantId, CourseId = course.Id, CourseVersionId = versionId, QuestionBankId = bank.Id, Title = title,
            Instructions = $"Questions from the video “{video.Title}”.", Status = request.Publish == true ? AssessmentStatus.Published : AssessmentStatus.Draft,
            TimeLimitMinutes = request.TimeLimitMinutes, AttemptLimit = request.AttemptLimit ?? 1, CreatedByUserId = userId, CreatedAtUtc = now, UpdatedAtUtc = now,
            PublishedAtUtc = request.Publish == true ? now : null
        };
        db.QuestionBanks.Add(bank);
        db.Assessments.Add(assessment);
        for (var index = 0; index < questions.Count; index++)
        {
            var item = questions[index];
            var question = new Question
            {
                Id = Guid.NewGuid(), TenantId = tenantId, QuestionBankId = bank.Id, Type = QuestionType.MultipleChoice, Prompt = item.Question.Trim(),
                OptionsJson = JsonSerializer.Serialize(item.Options), CorrectAnswerJson = JsonSerializer.Serialize(new[] { item.Options[item.AnswerIndex] }),
                Points = points, CreatedAtUtc = now, UpdatedAtUtc = now
            };
            db.Questions.Add(question);
            db.AssessmentQuestions.Add(new AssessmentQuestion { AssessmentId = assessment.Id, QuestionId = question.Id, DisplayOrder = index + 1, Points = points });
        }
        insight.QuizAssessmentId = assessment.Id;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/assessments/{assessment.Id:D}", new QuizCreated(assessment.Id, assessment.Title, assessment.Status.ToString(), questions.Count, questions.Count * points, course.Id));
    }

    private static InsightResponse ToResponse(VideoInsight item)
        => new(item.Id, item.Kind.ToString(), item.Kind == VideoInsightKind.Summary ? item.Content : null,
            item.Kind == VideoInsightKind.Questions ? PracticeQuestions.Deserialize(item.Content) : null, item.Published, item.Provider, item.Model, item.UpdatedAtUtc, item.QuizAssessmentId);

    // ---------- the organization's setting ----------
    private static async Task<IResult> GetSettingsAsync(LmsDbContext db, IVideoTranscoder transcoder, CancellationToken cancellationToken)
        => Results.Ok(ToResponse(await db.VideoAiSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken), transcoder.Enabled));

    /// <summary>Never includes the key, only whether one is saved.</summary>
    private static VideoAiSettingsResponse ToResponse(VideoAiSettings? settings, bool conversionAvailable)
        => new(settings?.Provider ?? VideoAiProviders.Local, settings?.BaseUrl ?? VideoAiProviders.DefaultBaseUrl, !string.IsNullOrEmpty(settings?.ApiKeyProtected),
            settings?.TranscriptionModel ?? VideoAiProviders.DefaultTranscriptionModel, settings?.ChatModel ?? VideoAiProviders.DefaultChatModel, settings?.AutoTranscribe ?? false, conversionAvailable);

    private static async Task<IResult> SaveSettingsAsync(HttpContext httpContext, ITenantContext tenantContext, LmsDbContext db, IConfiguration configuration, IVideoTranscoder transcoder, VideoAiCredentialStore credentials, SaveVideoAiSettingsRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId) return Results.Unauthorized();
        var provider = VideoAiProviders.All.FirstOrDefault(item => item.Equals(request.Provider, StringComparison.OrdinalIgnoreCase));
        if (provider is null) return VideoEndpoints.Problem($"Provider must be one of: {string.Join(", ", VideoAiProviders.All)}.");
        var settings = await db.VideoAiSettings.SingleOrDefaultAsync(cancellationToken);
        string? baseUrl = null, transcriptionModel = null, chatModel = null;
        if (provider == VideoAiProviders.OpenAiCompatible)
        {
            baseUrl = LiveClassUrls.NormalizeHttps(string.IsNullOrWhiteSpace(request.BaseUrl) ? VideoAiProviders.DefaultBaseUrl : request.BaseUrl, configuration.GetValue("Integrations:AllowInsecureLiveClassHosts", false));
            if (baseUrl is null || baseUrl.Contains('?') || baseUrl.Contains('#')) return VideoEndpoints.Problem("Enter the service address as an https link, for example https://api.openai.com/v1.");
            baseUrl = baseUrl.TrimEnd('/');
            transcriptionModel = string.IsNullOrWhiteSpace(request.TranscriptionModel) ? VideoAiProviders.DefaultTranscriptionModel : request.TranscriptionModel.Trim();
            chatModel = string.IsNullOrWhiteSpace(request.ChatModel) ? VideoAiProviders.DefaultChatModel : request.ChatModel.Trim();
            if (!ModelName.IsMatch(transcriptionModel) || !ModelName.IsMatch(chatModel)) return VideoEndpoints.Problem("A model name may use letters, digits and . _ : / - only, up to 100 characters.");
            if (request.ApiKey is { Length: > 500 }) return VideoEndpoints.Problem("The API key is too long.");
            if (string.IsNullOrEmpty(request.ApiKey) && string.IsNullOrEmpty(settings?.ApiKeyProtected)) return VideoEndpoints.Problem("Enter the API key.");
        }
        if (settings is null)
        {
            settings = new VideoAiSettings { Id = Guid.NewGuid(), TenantId = tenantId };
            db.VideoAiSettings.Add(settings);
        }
        settings.Provider = provider;
        if (provider == VideoAiProviders.OpenAiCompatible)
        {
            settings.BaseUrl = baseUrl; settings.TranscriptionModel = transcriptionModel; settings.ChatModel = chatModel;
            // A blank key means "keep the one already saved", so the form never has to show it.
            if (!string.IsNullOrEmpty(request.ApiKey)) settings.ApiKeyProtected = credentials.Protect(request.ApiKey);
        }
        settings.AutoTranscribe = provider == VideoAiProviders.OpenAiCompatible && request.AutoTranscribe;
        settings.UpdatedByUserId = Guid.TryParse(httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ? userId : null;
        settings.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToResponse(settings, transcoder.Enabled));
    }
}

public sealed record SaveTranscriptRequest(string? Text, string? Language = null);
public sealed record GenerateInsightRequest(string? Kind, int? Count = null);
public sealed record UpdateInsightRequest(string? Content = null, PracticeQuestion[]? Questions = null);
public sealed record PublishRequest(bool Published);
public sealed record SegmentResponse(int Index, double StartSeconds, double EndSeconds, string Text);
public sealed record TranscriptResponse(string Status, string? Source, string? Language, string? Provider, string? StatusMessage, List<SegmentResponse>? Segments);
public sealed record SearchHit(Guid VideoId, string VideoTitle, string CourseTitle, double StartSeconds, string Text);
public sealed record InsightResponse(Guid Id, string Kind, string? Content, List<PracticeQuestion>? Questions, bool Published, string Provider, string? Model, DateTimeOffset UpdatedAtUtc, Guid? QuizAssessmentId = null);
public sealed record CreateQuizRequest(string? Title = null, int? TimeLimitMinutes = null, int? AttemptLimit = null, int? PointsPerQuestion = null, bool? Publish = null);
public sealed record QuizCreated(Guid AssessmentId, string Title, string Status, int Questions, int TotalPoints, Guid CourseId);
public sealed record SaveVideoAiSettingsRequest(string? Provider, string? BaseUrl = null, string? ApiKey = null, string? TranscriptionModel = null, string? ChatModel = null, bool AutoTranscribe = false);
public sealed record VideoAiSettingsResponse(string Provider, string BaseUrl, bool ApiKeySet, string TranscriptionModel, string ChatModel, bool AutoTranscribe, bool ConversionAvailable);
