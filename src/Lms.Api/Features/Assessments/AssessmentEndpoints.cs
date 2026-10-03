using System.Security.Claims;
using System.Text.Json;
using Lms.Api.Domain.Assessments;
using Lms.Api.Domain.Courses;
using Lms.Api.Domain.Identity;
using Lms.Api.Domain.Learning;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Notifications;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Assessments;

public static class AssessmentEndpoints
{
    public static void MapAssessmentEndpoints(this WebApplication app)
    {
        var tenant = app.MapGroup("/api/v1/tenant").RequireAuthorization("tenant.authenticated");
        tenant.AddEndpointFilter(async (context, next) =>
        {
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (!tenantContext.IsResolved)
                return Results.BadRequest(new { message = "A tenant is required. Use the X-Tenant-Slug header or a tenant subdomain." });
            return await next(context);
        });

        tenant.MapGet("/courses/{courseId:guid}/assessments", ListAssessmentsAsync).RequireAuthorization("tenant.assessment.read");
        tenant.MapPost("/courses/{courseId:guid}/assessments", CreateAssessmentAsync).RequireAuthorization("tenant.assessment.manage");
        tenant.MapPost("/assessments/{assessmentId:guid}/questions", AddQuestionAsync).RequireAuthorization("tenant.assessment.manage");
        tenant.MapPost("/assessments/{assessmentId:guid}/publish", PublishAssessmentAsync).RequireAuthorization("tenant.assessment.manage");
        tenant.MapGet("/assessments/{assessmentId:guid}", GetAssessmentAsync).RequireAuthorization("tenant.assessment.read");
        tenant.MapPost("/assessments/{assessmentId:guid}/attempts", StartAttemptAsync).RequireAuthorization("tenant.assessment.attempt");
        tenant.MapGet("/assessment-attempts/{attemptId:guid}", GetAttemptAsync).RequireAuthorization("tenant.assessment.attempt");
        tenant.MapPut("/assessment-attempts/{attemptId:guid}/answers/{questionId:guid}", SaveAnswerAsync).RequireAuthorization("tenant.assessment.attempt");
        tenant.MapPost("/assessment-attempts/{attemptId:guid}/submit", SubmitAttemptAsync).RequireAuthorization("tenant.assessment.attempt");
        tenant.MapGet("/assessments/{assessmentId:guid}/attempts", ListAttemptsAsync).RequireAuthorization("tenant.grade.read");
        tenant.MapPost("/assessment-attempts/{attemptId:guid}/grade", GradeAttemptAsync).RequireAuthorization("tenant.grade.manage");
    }

    private static async Task<IResult> ListAssessmentsAsync(HttpContext httpContext, LmsDbContext db, Guid courseId, CancellationToken cancellationToken)
    {
        var canManage = HasPermission(httpContext, LmsPermissions.AssessmentManage);
        var query = db.Assessments.AsNoTracking().Where(item => item.CourseId == courseId);
        if (!canManage) query = query.Where(item => item.Status == AssessmentStatus.Published);
        var assessments = await query.OrderBy(item => item.Title).ToListAsync(cancellationToken);
        var counts = await db.AssessmentQuestions.AsNoTracking().Where(item => assessments.Select(assessment => assessment.Id).Contains(item.AssessmentId)).GroupBy(item => item.AssessmentId).Select(group => new { group.Key, Count = group.Count() }).ToDictionaryAsync(item => item.Key, item => item.Count, cancellationToken);
        return Results.Ok(assessments.Select(item => ToAssessmentResponse(item, counts.GetValueOrDefault(item.Id))).ToArray());
    }

    private static async Task<IResult> CreateAssessmentAsync(HttpContext httpContext, LmsDbContext db, ITenantContext tenantContext, Guid courseId, CreateAssessmentRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 250) return Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = ["Assessment title is required and must be 250 characters or fewer."] });
        if (request.TimeLimitMinutes is <= 0 or > 1440) return Results.ValidationProblem(new Dictionary<string, string[]> { ["timeLimitMinutes"] = ["Time limit must be between 1 and 1,440 minutes."] });
        if (request.AttemptLimit is < 1 or > 20) return Results.ValidationProblem(new Dictionary<string, string[]> { ["attemptLimit"] = ["Attempt limit must be between 1 and 20."] });
        var course = await db.Courses.SingleOrDefaultAsync(item => item.Id == courseId && item.Status != CourseStatus.Archived, cancellationToken);
        if (course is null || course.CurrentVersionId is not Guid versionId) return Results.NotFound(new { message = "The course or its current version was not found." });
        var now = DateTimeOffset.UtcNow;
        var questionBank = new QuestionBank { Id = Guid.NewGuid(), TenantId = tenantId, CourseId = courseId, Name = $"{request.Title.Trim()} question bank", CreatedAtUtc = now, CreatedByUserId = userId };
        var assessment = new Assessment { Id = Guid.NewGuid(), TenantId = tenantId, CourseId = courseId, CourseVersionId = versionId, QuestionBankId = questionBank.Id, Title = request.Title.Trim(), Instructions = request.Instructions?.Trim(), Status = AssessmentStatus.Draft, TimeLimitMinutes = request.TimeLimitMinutes, AttemptLimit = request.AttemptLimit ?? 1, CreatedByUserId = userId, CreatedAtUtc = now, UpdatedAtUtc = now };
        db.QuestionBanks.Add(questionBank);
        db.Assessments.Add(assessment);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/assessments/{assessment.Id:D}", ToAssessmentResponse(assessment, 0));
    }

    private static async Task<IResult> AddQuestionAsync(LmsDbContext db, Guid assessmentId, AddQuestionRequest request, CancellationToken cancellationToken)
    {
        var assessment = await db.Assessments.SingleOrDefaultAsync(item => item.Id == assessmentId, cancellationToken);
        if (assessment is null) return Results.NotFound();
        if (assessment.Status != AssessmentStatus.Draft) return Results.Conflict(new { message = "Questions can only be changed while the assessment is a draft." });
        if (!Enum.TryParse<QuestionType>(request.Type, true, out var questionType)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["type"] = ["Use MultipleChoice, MultipleResponse, TrueFalse, ShortAnswer, Essay, or FileUpload."] });
        if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Trim().Length > 10000) return Results.ValidationProblem(new Dictionary<string, string[]> { ["prompt"] = ["Question prompt is required and must be 10,000 characters or fewer."] });
        if (request.Points is < 1 or > 100) return Results.ValidationProblem(new Dictionary<string, string[]> { ["points"] = ["Points must be between 1 and 100."] });
        var options = request.Options ?? [];
        if (questionType == QuestionType.TrueFalse && options.Length == 0) options = ["true", "false"];
        if (questionType is QuestionType.MultipleChoice or QuestionType.MultipleResponse or QuestionType.TrueFalse && options.Length < 2) return Results.ValidationProblem(new Dictionary<string, string[]> { ["options"] = ["Choice questions require at least two options."] });
        var correctAnswers = (request.CorrectAnswers ?? []).Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()).ToArray();
        if (questionType is not (QuestionType.Essay or QuestionType.FileUpload) && correctAnswers.Length == 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["correctAnswers"] = ["Objective questions require at least one correct answer."] });
        if (questionType == QuestionType.MultipleResponse && correctAnswers.Length < 2) return Results.ValidationProblem(new Dictionary<string, string[]> { ["correctAnswers"] = ["Multiple response questions require at least two correct answers."] });
        if (correctAnswers.Any(answer => options.Length > 0 && !options.Contains(answer, StringComparer.OrdinalIgnoreCase))) return Results.ValidationProblem(new Dictionary<string, string[]> { ["correctAnswers"] = ["Every correct answer must match one of the supplied options."] });
        var now = DateTimeOffset.UtcNow;
        var question = new Question { Id = Guid.NewGuid(), TenantId = assessment.TenantId, QuestionBankId = assessment.QuestionBankId, Type = questionType, Prompt = request.Prompt.Trim(), OptionsJson = JsonSerializer.Serialize(options), CorrectAnswerJson = JsonSerializer.Serialize(correctAnswers), Points = request.Points, CreatedAtUtc = now, UpdatedAtUtc = now };
        var displayOrder = await db.AssessmentQuestions.CountAsync(item => item.AssessmentId == assessmentId, cancellationToken) + 1;
        db.Questions.Add(question);
        db.AssessmentQuestions.Add(new AssessmentQuestion { AssessmentId = assessmentId, QuestionId = question.Id, DisplayOrder = displayOrder, Points = request.Points });
        assessment.UpdatedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/assessments/{assessmentId:D}/questions/{question.Id:D}", ToQuestionResponse(question, true, displayOrder, request.Points));
    }

    private static async Task<IResult> PublishAssessmentAsync(LmsDbContext db, Guid assessmentId, CancellationToken cancellationToken)
    {
        var assessment = await db.Assessments.SingleOrDefaultAsync(item => item.Id == assessmentId, cancellationToken);
        if (assessment is null) return Results.NotFound();
        if (assessment.Status != AssessmentStatus.Draft) return Results.Conflict(new { message = "Only draft assessments can be published." });
        if (!await db.AssessmentQuestions.AnyAsync(item => item.AssessmentId == assessmentId, cancellationToken)) return Results.Conflict(new { message = "Add at least one question before publishing." });
        var course = await db.Courses.SingleOrDefaultAsync(item => item.Id == assessment.CourseId && item.Status == CourseStatus.Published && item.CurrentVersionId == assessment.CourseVersionId, cancellationToken);
        if (course is null) return Results.Conflict(new { message = "The assessment's course must be published first." });
        assessment.Status = AssessmentStatus.Published;
        assessment.PublishedAtUtc = DateTimeOffset.UtcNow;
        assessment.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToAssessmentResponse(assessment, await db.AssessmentQuestions.CountAsync(item => item.AssessmentId == assessmentId, cancellationToken)));
    }

    private static async Task<IResult> GetAssessmentAsync(HttpContext httpContext, LmsDbContext db, Guid assessmentId, CancellationToken cancellationToken)
    {
        var assessment = await db.Assessments.AsNoTracking().SingleOrDefaultAsync(item => item.Id == assessmentId, cancellationToken);
        if (assessment is null || (!HasPermission(httpContext, LmsPermissions.AssessmentManage) && assessment.Status != AssessmentStatus.Published)) return Results.NotFound();
        var links = await db.AssessmentQuestions.AsNoTracking().Where(item => item.AssessmentId == assessmentId).OrderBy(item => item.DisplayOrder).ToListAsync(cancellationToken);
        var questions = await db.Questions.AsNoTracking().Where(item => links.Select(link => link.QuestionId).Contains(item.Id)).ToDictionaryAsync(item => item.Id, cancellationToken);
        return Results.Ok(new AssessmentDetailResponse(ToAssessmentResponse(assessment, links.Count), links.Where(link => questions.ContainsKey(link.QuestionId)).Select(link => ToQuestionResponse(questions[link.QuestionId], HasPermission(httpContext, LmsPermissions.AssessmentManage), link.DisplayOrder, link.Points)).ToArray()));
    }

    private static async Task<IResult> StartAttemptAsync(HttpContext httpContext, LmsDbContext db, Guid assessmentId, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid learnerUserId) return Results.Unauthorized();
        var assessment = await db.Assessments.AsNoTracking().SingleOrDefaultAsync(item => item.Id == assessmentId && item.Status == AssessmentStatus.Published, cancellationToken);
        if (assessment is null) return Results.NotFound();
        var enrollment = await db.Enrollments.SingleOrDefaultAsync(item => item.CourseId == assessment.CourseId && item.LearnerUserId == learnerUserId && (item.Status == EnrollmentStatus.Active || item.Status == EnrollmentStatus.Completed), cancellationToken);
        if (enrollment is null) return Results.Forbid();
        var attempts = await db.AssessmentAttempts.Where(item => item.AssessmentId == assessmentId && item.LearnerUserId == learnerUserId).OrderBy(item => item.AttemptNumber).ToListAsync(cancellationToken);
        if (attempts.Count >= assessment.AttemptLimit) return Results.Conflict(new { message = "The attempt limit for this assessment has been reached." });
        var links = await db.AssessmentQuestions.AsNoTracking().Where(item => item.AssessmentId == assessmentId).ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var attempt = new AssessmentAttempt { Id = Guid.NewGuid(), TenantId = assessment.TenantId, AssessmentId = assessmentId, CourseId = assessment.CourseId, LearnerUserId = learnerUserId, AttemptNumber = attempts.Count + 1, Status = AttemptStatus.InProgress, PossiblePoints = links.Sum(item => item.Points), StartedAtUtc = now };
        db.AssessmentAttempts.Add(attempt);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/assessment-attempts/{attempt.Id:D}", await BuildAttemptAsync(db, attempt, includeCorrect: false, cancellationToken));
    }

    private static async Task<IResult> GetAttemptAsync(HttpContext httpContext, LmsDbContext db, Guid attemptId, CancellationToken cancellationToken)
    {
        var attempt = await db.AssessmentAttempts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == attemptId, cancellationToken);
        if (attempt is null) return Results.NotFound();
        var canReview = HasPermission(httpContext, LmsPermissions.GradeRead);
        if (!canReview && attempt.LearnerUserId != GetUserId(httpContext)) return Results.Forbid();
        var includeCorrect = HasPermission(httpContext, LmsPermissions.AssessmentManage) || HasPermission(httpContext, LmsPermissions.GradeManage);
        return Results.Ok(await BuildAttemptAsync(db, attempt, includeCorrect, cancellationToken));
    }

    private static async Task<IResult> SaveAnswerAsync(HttpContext httpContext, LmsDbContext db, Guid attemptId, Guid questionId, SaveAnswerRequest request, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid learnerUserId) return Results.Unauthorized();
        var attempt = await db.AssessmentAttempts.SingleOrDefaultAsync(item => item.Id == attemptId && item.LearnerUserId == learnerUserId, cancellationToken);
        if (attempt is null) return Results.NotFound();
        if (attempt.Status != AttemptStatus.InProgress) return Results.Conflict(new { message = "Answers cannot be changed after the attempt is submitted." });
        var assessment = await db.Assessments.AsNoTracking().SingleOrDefaultAsync(item => item.Id == attempt.AssessmentId, cancellationToken);
        if (assessment is null) return Results.NotFound();
        if (IsExpired(attempt, assessment)) return Results.Conflict(new { message = "The assessment time limit has expired. Submit the attempt to record the answers already saved.", expiresAtUtc = GetExpiry(attempt, assessment) });
        var questionLink = await db.AssessmentQuestions.SingleOrDefaultAsync(item => item.AssessmentId == attempt.AssessmentId && item.QuestionId == questionId, cancellationToken);
        var question = await db.Questions.SingleOrDefaultAsync(item => item.Id == questionId, cancellationToken);
        if (questionLink is null || question is null) return Results.NotFound();
        var answer = await db.AssessmentAnswers.SingleOrDefaultAsync(item => item.AttemptId == attemptId && item.QuestionId == questionId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (answer is null)
        {
            answer = new AssessmentAnswer { Id = Guid.NewGuid(), TenantId = attempt.TenantId, AttemptId = attemptId, QuestionId = questionId, AnswerJson = "{}", AnsweredAtUtc = now, UpdatedAtUtc = now };
            db.AssessmentAnswers.Add(answer);
        }
        answer.AnswerJson = JsonSerializer.Serialize(new StoredAnswer((request.Answers ?? []).Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()).ToArray(), request.Text?.Trim(), request.FileAssetId));
        answer.AnsweredAtUtc = answer.AnsweredAtUtc == default ? now : answer.AnsweredAtUtc;
        answer.UpdatedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new { answerId = answer.Id, questionId, savedAtUtc = now });
    }

    private static async Task<IResult> SubmitAttemptAsync(HttpContext httpContext, LmsDbContext db, NotificationService notifications, Guid attemptId, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid learnerUserId) return Results.Unauthorized();
        var attempt = await db.AssessmentAttempts.SingleOrDefaultAsync(item => item.Id == attemptId && item.LearnerUserId == learnerUserId, cancellationToken);
        if (attempt is null) return Results.NotFound();
        if (attempt.Status != AttemptStatus.InProgress) return Results.Conflict(new { message = "This attempt has already been submitted." });
        var assessment = await db.Assessments.AsNoTracking().SingleOrDefaultAsync(item => item.Id == attempt.AssessmentId, cancellationToken);
        if (assessment is null) return Results.NotFound();
        var isExpired = IsExpired(attempt, assessment);
        var links = await db.AssessmentQuestions.AsNoTracking().Where(item => item.AssessmentId == attempt.AssessmentId).OrderBy(item => item.DisplayOrder).ToListAsync(cancellationToken);
        var questionIds = links.Select(item => item.QuestionId).ToArray();
        var questions = await db.Questions.Where(item => questionIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, cancellationToken);
        var answers = await db.AssessmentAnswers.Where(item => item.AttemptId == attemptId).ToDictionaryAsync(item => item.QuestionId, cancellationToken);
        var hasManualReview = false;
        var totalScore = 0;
        foreach (var link in links)
        {
            if (!questions.TryGetValue(link.QuestionId, out var question)) continue;
            if (!answers.TryGetValue(link.QuestionId, out var answer))
            {
                answer = new AssessmentAnswer { Id = Guid.NewGuid(), TenantId = attempt.TenantId, AttemptId = attemptId, QuestionId = link.QuestionId, AnswerJson = JsonSerializer.Serialize(new StoredAnswer([], null, null)), AnsweredAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow };
                db.AssessmentAnswers.Add(answer);
            }
            var grading = GradeObjectiveQuestion(question, answer.AnswerJson);
            answer.ScorePoints = grading.ScorePoints * link.Points / Math.Max(1, question.Points);
            answer.IsCorrect = grading.IsCorrect;
            if (question.Type is QuestionType.Essay or QuestionType.FileUpload) hasManualReview = true;
            totalScore += answer.ScorePoints;
        }
        attempt.ScorePoints = totalScore;
        attempt.PossiblePoints = links.Sum(item => item.Points);
        attempt.Percentage = attempt.PossiblePoints == 0 ? 0 : Math.Round(totalScore * 100m / attempt.PossiblePoints, 2);
        attempt.SubmittedAfterTimeLimit = isExpired;
        attempt.Status = hasManualReview ? AttemptStatus.Submitted : AttemptStatus.Graded;
        attempt.SubmittedAtUtc = DateTimeOffset.UtcNow;
        if (!hasManualReview) attempt.GradedAtUtc = attempt.SubmittedAtUtc;
        if (!hasManualReview)
            await notifications.QueueAsync(db, attempt.TenantId, learnerUserId, "ASSESSMENT_GRADED", new Dictionary<string, string> { ["AssessmentTitle"] = assessment.Title, ["Percentage"] = attempt.Percentage?.ToString("0.##") ?? "0" }, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(await BuildAttemptAsync(db, attempt, false, cancellationToken));
    }

    private static async Task<IResult> ListAttemptsAsync(LmsDbContext db, Guid assessmentId, CancellationToken cancellationToken)
    {
        var assessment = await db.Assessments.AsNoTracking().SingleOrDefaultAsync(item => item.Id == assessmentId, cancellationToken);
        if (assessment is null) return Results.NotFound();
        var attempts = await db.AssessmentAttempts.AsNoTracking().Where(item => item.AssessmentId == assessmentId).OrderByDescending(item => item.StartedAtUtc).Select(item => new AttemptSummaryResponse(item.Id, item.LearnerUserId, item.AttemptNumber, item.Status.ToString(), item.ScorePoints, item.PossiblePoints, item.Percentage, item.StartedAtUtc, item.SubmittedAtUtc, item.GradedAtUtc, item.SubmittedAfterTimeLimit)).ToListAsync(cancellationToken);
        return Results.Ok(attempts);
    }

    private static async Task<IResult> GradeAttemptAsync(LmsDbContext db, NotificationService notifications, Guid attemptId, GradeAttemptRequest request, CancellationToken cancellationToken)
    {
        var attempt = await db.AssessmentAttempts.SingleOrDefaultAsync(item => item.Id == attemptId, cancellationToken);
        if (attempt is null) return Results.NotFound();
        if (attempt.Status != AttemptStatus.Submitted) return Results.Conflict(new { message = "Only submitted attempts require teacher grading." });
        if (request.ScorePoints < 0 || request.ScorePoints > attempt.PossiblePoints) return Results.ValidationProblem(new Dictionary<string, string[]> { ["scorePoints"] = ["Score must be between zero and the possible points."] });
        attempt.ScorePoints = request.ScorePoints;
        attempt.Percentage = attempt.PossiblePoints == 0 ? 0 : Math.Round(request.ScorePoints * 100m / attempt.PossiblePoints, 2);
        attempt.TeacherFeedback = request.Feedback?.Trim();
        attempt.Status = AttemptStatus.Graded;
        attempt.GradedAtUtc = DateTimeOffset.UtcNow;
        var assessment = await db.Assessments.AsNoTracking().SingleOrDefaultAsync(item => item.Id == attempt.AssessmentId, cancellationToken);
        if (assessment is not null)
            await notifications.QueueAsync(db, attempt.TenantId, attempt.LearnerUserId, "ASSESSMENT_GRADED", new Dictionary<string, string> { ["AssessmentTitle"] = assessment.Title, ["Percentage"] = attempt.Percentage?.ToString("0.##") ?? "0" }, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new AttemptSummaryResponse(attempt.Id, attempt.LearnerUserId, attempt.AttemptNumber, attempt.Status.ToString(), attempt.ScorePoints, attempt.PossiblePoints, attempt.Percentage, attempt.StartedAtUtc, attempt.SubmittedAtUtc, attempt.GradedAtUtc, attempt.SubmittedAfterTimeLimit));
    }

    private static async Task<AttemptDetailResponse> BuildAttemptAsync(LmsDbContext db, AssessmentAttempt attempt, bool includeCorrect, CancellationToken cancellationToken)
    {
        var assessment = await db.Assessments.AsNoTracking().SingleAsync(item => item.Id == attempt.AssessmentId, cancellationToken);
        var links = await db.AssessmentQuestions.AsNoTracking().Where(item => item.AssessmentId == assessment.Id).OrderBy(item => item.DisplayOrder).ToListAsync(cancellationToken);
        var questions = await db.Questions.AsNoTracking().Where(item => links.Select(link => link.QuestionId).Contains(item.Id)).ToDictionaryAsync(item => item.Id, cancellationToken);
        var answers = await db.AssessmentAnswers.AsNoTracking().Where(item => item.AttemptId == attempt.Id).ToDictionaryAsync(item => item.QuestionId, cancellationToken);
        return new AttemptDetailResponse(
            new AttemptSummaryResponse(attempt.Id, attempt.LearnerUserId, attempt.AttemptNumber, attempt.Status.ToString(), attempt.ScorePoints, attempt.PossiblePoints, attempt.Percentage, attempt.StartedAtUtc, attempt.SubmittedAtUtc, attempt.GradedAtUtc, attempt.SubmittedAfterTimeLimit),
            assessment.Title, assessment.Instructions, assessment.TimeLimitMinutes,
            links.Where(link => questions.ContainsKey(link.QuestionId)).Select(link =>
            {
                var question = questions[link.QuestionId];
                answers.TryGetValue(question.Id, out var answer);
                return new AttemptQuestionResponse(question.Id, question.Type.ToString(), question.Prompt, DeserializeArray(question.OptionsJson), includeCorrect ? DeserializeArray(question.CorrectAnswerJson) : [], link.DisplayOrder, link.Points, answer is null ? [] : DeserializeAnswer(answer.AnswerJson).Answers, answer?.ScorePoints ?? 0, answer?.IsCorrect, answer?.Feedback);
            }).ToArray(), attempt.TeacherFeedback);
    }

    private static AssessmentResponse ToAssessmentResponse(Assessment item, int questionCount) => new(item.Id, item.CourseId, item.Title, item.Instructions, item.Status.ToString(), item.TimeLimitMinutes, item.AttemptLimit, questionCount, item.CreatedAtUtc, item.PublishedAtUtc);

    private static QuestionResponse ToQuestionResponse(Question item, bool includeCorrect, int displayOrder, int points) => new(item.Id, item.Type.ToString(), item.Prompt, DeserializeArray(item.OptionsJson), includeCorrect ? DeserializeArray(item.CorrectAnswerJson) : [], displayOrder, points);

    private static ObjectiveGrade GradeObjectiveQuestion(Question question, string answerJson)
    {
        if (question.Type is QuestionType.Essay or QuestionType.FileUpload) return new ObjectiveGrade(0, null);
        var answer = DeserializeAnswer(answerJson).Answers.Select(Normalize).Where(item => item.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
        var expected = DeserializeArray(question.CorrectAnswerJson).Select(Normalize).Where(item => item.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
        var correct = answer.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase);
        return new ObjectiveGrade(correct ? question.Points : 0, correct);
    }

    private static string[] DeserializeArray(string json)
    {
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static StoredAnswer DeserializeAnswer(string json)
    {
        try { return JsonSerializer.Deserialize<StoredAnswer>(json) ?? new StoredAnswer([], null, null); }
        catch (JsonException) { return new StoredAnswer([], null, null); }
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();
    private static bool IsExpired(AssessmentAttempt attempt, Assessment assessment) => assessment.TimeLimitMinutes is > 0 && attempt.StartedAtUtc.AddMinutes(assessment.TimeLimitMinutes.Value) <= DateTimeOffset.UtcNow;
    private static DateTimeOffset? GetExpiry(AssessmentAttempt attempt, Assessment assessment) => assessment.TimeLimitMinutes is > 0 ? attempt.StartedAtUtc.AddMinutes(assessment.TimeLimitMinutes.Value) : null;
    private static Guid? GetUserId(HttpContext httpContext) => Guid.TryParse(httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    private static bool HasPermission(HttpContext httpContext, string permission) => httpContext.User.HasClaim("permission", permission);

    private sealed record StoredAnswer(string[] Answers, string? Text, Guid? FileAssetId);
    private sealed record ObjectiveGrade(int ScorePoints, bool? IsCorrect);
}

public sealed record CreateAssessmentRequest(string Title, string? Instructions = null, int? TimeLimitMinutes = null, int? AttemptLimit = null);
public sealed record AddQuestionRequest(string Type, string Prompt, string[]? Options = null, string[]? CorrectAnswers = null, int Points = 1);
public sealed record SaveAnswerRequest(string[]? Answers = null, string? Text = null, Guid? FileAssetId = null);
public sealed record GradeAttemptRequest(int ScorePoints, string? Feedback = null);
public sealed record AssessmentResponse(Guid Id, Guid CourseId, string Title, string? Instructions, string Status, int? TimeLimitMinutes, int AttemptLimit, int QuestionCount, DateTimeOffset CreatedAtUtc, DateTimeOffset? PublishedAtUtc);
public sealed record QuestionResponse(Guid Id, string Type, string Prompt, string[] Options, string[] CorrectAnswers, int DisplayOrder, int Points);
public sealed record AssessmentDetailResponse(AssessmentResponse Assessment, QuestionResponse[] Questions);
public sealed record AttemptSummaryResponse(Guid Id, Guid LearnerUserId, int AttemptNumber, string Status, int ScorePoints, int PossiblePoints, decimal? Percentage, DateTimeOffset StartedAtUtc, DateTimeOffset? SubmittedAtUtc, DateTimeOffset? GradedAtUtc, bool SubmittedAfterTimeLimit = false);
public sealed record AttemptQuestionResponse(Guid Id, string Type, string Prompt, string[] Options, string[] CorrectAnswers, int DisplayOrder, int Points, string[] Answers, int ScorePoints, bool? IsCorrect, string? Feedback);
public sealed record AttemptDetailResponse(AttemptSummaryResponse Attempt, string AssessmentTitle, string? Instructions, int? TimeLimitMinutes, AttemptQuestionResponse[] Questions, string? TeacherFeedback);
