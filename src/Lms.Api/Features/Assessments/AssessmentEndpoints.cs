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

/// <summary>
/// Taking and grading assessments. An assessment keeps one identity while its questions are versioned (see AssessmentAuthoringEndpoints):
/// every attempt records the version it was started on and a plan of the questions it was dealt (drawn from pools, shuffled), so editing
/// or republishing never changes an attempt in progress or one already graded.
/// </summary>
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
        tenant.MapGet("/assessments/{assessmentId:guid}", GetAssessmentAsync).RequireAuthorization("tenant.assessment.read");
        tenant.MapPost("/assessments/{assessmentId:guid}/attempts", StartAttemptAsync).RequireAuthorization("tenant.assessment.attempt");
        tenant.MapGet("/assessment-attempts/{attemptId:guid}", GetAttemptAsync).RequireAuthorization("tenant.assessment.attempt");
        tenant.MapPut("/assessment-attempts/{attemptId:guid}/answers/{questionId:guid}", SaveAnswerAsync).RequireAuthorization("tenant.assessment.attempt");
        tenant.MapPost("/assessment-attempts/{attemptId:guid}/submit", SubmitAttemptAsync).RequireAuthorization("tenant.assessment.attempt");
        tenant.MapGet("/assessments/{assessmentId:guid}/attempts", ListAttemptsAsync).RequireAuthorization("tenant.grade.manage");
        tenant.MapPost("/assessment-attempts/{attemptId:guid}/grade", GradeAttemptAsync).RequireAuthorization("tenant.grade.manage");
    }

    // ---------- listing and reading ----------
    private static async Task<IResult> ListAssessmentsAsync(HttpContext httpContext, LmsDbContext db, Guid courseId, CancellationToken cancellationToken)
    {
        var canManage = HasPermission(httpContext, LmsPermissions.AssessmentManage);
        var query = db.Assessments.AsNoTracking().Where(item => item.CourseId == courseId);
        if (!canManage) query = query.Where(item => item.Status == AssessmentStatus.Published);
        var assessments = await query.OrderBy(item => item.Title).ToListAsync(cancellationToken);
        var ids = assessments.Select(item => item.Id).ToList();
        var links = await db.AssessmentQuestions.AsNoTracking().Where(item => ids.Contains(item.AssessmentId)).ToListAsync(cancellationToken);
        var pools = await db.AssessmentPools.AsNoTracking().Where(item => ids.Contains(item.AssessmentId)).ToListAsync(cancellationToken);
        return Results.Ok(assessments.Select(item => ToAssessmentResponse(item, DealtCount(links.Where(link => link.AssessmentId == item.Id && link.Version == item.CurrentVersion), pools.Where(pool => pool.AssessmentId == item.Id && pool.Version == item.CurrentVersion)))).ToArray());
    }

    private static async Task<IResult> CreateAssessmentAsync(HttpContext httpContext, LmsDbContext db, ITenantContext tenantContext, Guid courseId, CreateAssessmentRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        if (ValidateSettings(request.Title, request.TimeLimitMinutes, request.AttemptLimit, request.OpensAtUtc, request.DueAtUtc) is { } invalid) return invalid;
        var course = await db.Courses.SingleOrDefaultAsync(item => item.Id == courseId && item.Status != CourseStatus.Archived, cancellationToken);
        if (course is null || course.CurrentVersionId is not Guid versionId) return Results.NotFound(new { message = "The course or its current version was not found." });
        var now = DateTimeOffset.UtcNow;
        var questionBank = new QuestionBank { Id = Guid.NewGuid(), TenantId = tenantId, CourseId = courseId, Name = $"{request.Title.Trim()} question bank", CreatedAtUtc = now, CreatedByUserId = userId };
        var assessment = new Assessment
        {
            Id = Guid.NewGuid(), TenantId = tenantId, CourseId = courseId, CourseVersionId = versionId, QuestionBankId = questionBank.Id, Title = request.Title.Trim(),
            Instructions = request.Instructions?.Trim(), Status = AssessmentStatus.Draft, TimeLimitMinutes = request.TimeLimitMinutes, AttemptLimit = request.AttemptLimit ?? 1,
            ShuffleQuestions = request.ShuffleQuestions ?? false, ShuffleOptions = request.ShuffleOptions ?? false, OpensAtUtc = request.OpensAtUtc, DueAtUtc = request.DueAtUtc, CreatedByUserId = userId, CreatedAtUtc = now, UpdatedAtUtc = now
        };
        db.QuestionBanks.Add(questionBank);
        db.Assessments.Add(assessment);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/assessments/{assessment.Id:D}", ToAssessmentResponse(assessment, 0));
    }

    internal static IResult? ValidateSettings(string? title, int? timeLimitMinutes, int? attemptLimit, DateTimeOffset? opensAtUtc = null, DateTimeOffset? dueAtUtc = null)
    {
        if (opensAtUtc is DateTimeOffset opens && dueAtUtc is DateTimeOffset due && due <= opens) return Results.ValidationProblem(new Dictionary<string, string[]> { ["dueAtUtc"] = ["The deadline must be after the opening time."] });
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 250) return Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = ["Assessment title is required and must be 250 characters or fewer."] });
        if (timeLimitMinutes is <= 0 or > 1440) return Results.ValidationProblem(new Dictionary<string, string[]> { ["timeLimitMinutes"] = ["Time limit must be between 1 and 1,440 minutes."] });
        if (attemptLimit is < 1 or > 20) return Results.ValidationProblem(new Dictionary<string, string[]> { ["attemptLimit"] = ["Attempt limit must be between 1 and 20."] });
        return null;
    }

    private static async Task<IResult> GetAssessmentAsync(HttpContext httpContext, LmsDbContext db, Guid assessmentId, CancellationToken cancellationToken)
    {
        var assessment = await db.Assessments.AsNoTracking().SingleOrDefaultAsync(item => item.Id == assessmentId, cancellationToken);
        var canManage = HasPermission(httpContext, LmsPermissions.AssessmentManage);
        if (assessment is null || (!canManage && assessment.Status != AssessmentStatus.Published)) return Results.NotFound();

        var currentLinks = await LinksAsync(db, assessment, assessment.CurrentVersion, cancellationToken);
        var currentPools = await db.AssessmentPools.AsNoTracking().Where(item => item.AssessmentId == assessmentId && item.Version == assessment.CurrentVersion).ToListAsync(cancellationToken);
        var response = ToAssessmentResponse(assessment, DealtCount(currentLinks, currentPools));

        if (!canManage)
        {
            // A learner is told how it works for them (accommodations included) but never sees the questions before an attempt deals them.
            var learnerId = GetUserId(httpContext);
            var accommodation = await db.LearnerAccommodations.AsNoTracking().SingleOrDefaultAsync(item => item.CourseId == assessment.CourseId && item.LearnerUserId == learnerId, cancellationToken);
            var used = await db.AssessmentAttempts.CountAsync(item => item.AssessmentId == assessmentId && item.LearnerUserId == learnerId, cancellationToken);
            return Results.Ok(new AssessmentDetailResponse(response, [], null, false, EffectiveTime(assessment.TimeLimitMinutes, accommodation), assessment.AttemptLimit + (accommodation?.ExtraAttempts ?? 0), used,
                accommodation is null ? null : new MyAccommodation(accommodation.ExtraTimePercent, accommodation.ExtraAttempts)));
        }

        // Staff see the version being edited: the draft version when there is one, otherwise the current one.
        var editing = EditingVersion(assessment) ?? assessment.CurrentVersion;
        var links = editing == assessment.CurrentVersion ? currentLinks : await LinksAsync(db, assessment, editing, cancellationToken);
        var pools = editing == assessment.CurrentVersion ? currentPools : await db.AssessmentPools.AsNoTracking().Where(item => item.AssessmentId == assessmentId && item.Version == editing).ToListAsync(cancellationToken);
        var questions = await QuestionsAsync(db, links.Select(link => link.QuestionId), cancellationToken);
        var rubricNames = await RubricNamesAsync(db, questions.Values, cancellationToken);
        return Results.Ok(new AssessmentDetailResponse(response,
            links.Where(link => questions.ContainsKey(link.QuestionId)).Select(link => ToQuestionResponse(questions[link.QuestionId], true, link.DisplayOrder, link.Points, link.PoolName, rubricNames)).ToArray(),
            pools.OrderBy(pool => pool.Name).Select(pool => new PoolResponse(pool.Name, pool.DrawCount, links.Count(link => string.Equals(link.PoolName, pool.Name, StringComparison.OrdinalIgnoreCase)))).ToArray(),
            assessment.DraftVersion is not null, assessment.TimeLimitMinutes, assessment.AttemptLimit, null, null));
    }

    // ---------- taking ----------
    private static async Task<IResult> StartAttemptAsync(HttpContext httpContext, LmsDbContext db, Guid assessmentId, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid learnerUserId) return Results.Unauthorized();
        var assessment = await db.Assessments.AsNoTracking().SingleOrDefaultAsync(item => item.Id == assessmentId && item.Status == AssessmentStatus.Published, cancellationToken);
        if (assessment is null) return Results.NotFound();
        var enrollment = await db.Enrollments.SingleOrDefaultAsync(item => item.CourseId == assessment.CourseId && item.LearnerUserId == learnerUserId && (item.Status == EnrollmentStatus.Active || item.Status == EnrollmentStatus.Completed), cancellationToken);
        if (enrollment is null) return Results.Forbid();
        var rightNow = DateTimeOffset.UtcNow;
        if (assessment.OpensAtUtc is DateTimeOffset opens && opens > rightNow) return Results.Conflict(new { message = "This assessment is not open yet.", opensAtUtc = opens });
        if (assessment.DueAtUtc is DateTimeOffset closes && closes <= rightNow) return Results.Conflict(new { message = "This assessment has closed.", dueAtUtc = closes });
        var accommodation = await db.LearnerAccommodations.AsNoTracking().SingleOrDefaultAsync(item => item.CourseId == assessment.CourseId && item.LearnerUserId == learnerUserId, cancellationToken);
        var attemptCount = await db.AssessmentAttempts.CountAsync(item => item.AssessmentId == assessmentId && item.LearnerUserId == learnerUserId, cancellationToken);
        if (attemptCount >= assessment.AttemptLimit + (accommodation?.ExtraAttempts ?? 0)) return Results.Conflict(new { message = "The attempt limit for this assessment has been reached." });

        var (plan, possiblePoints) = await DealAsync(db, assessment, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var attempt = new AssessmentAttempt
        {
            Id = Guid.NewGuid(), TenantId = assessment.TenantId, AssessmentId = assessmentId, CourseId = assessment.CourseId, LearnerUserId = learnerUserId, AttemptNumber = attemptCount + 1,
            Status = AttemptStatus.InProgress, PossiblePoints = possiblePoints, Version = assessment.CurrentVersion, PlanJson = JsonSerializer.Serialize(plan),
            TimeLimitMinutes = EffectiveTime(assessment.TimeLimitMinutes, accommodation), StartedAtUtc = now
        };
        db.AssessmentAttempts.Add(attempt);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/assessment-attempts/{attempt.Id:D}", await BuildAttemptAsync(db, attempt, includeCorrect: false, cancellationToken));
    }

    /// <summary>The time limit with the learner's extra time added, rounded up to a whole minute; null when there is no limit.</summary>
    internal static int? EffectiveTime(int? limitMinutes, LearnerAccommodation? accommodation)
        => limitMinutes is int limit ? (int)Math.Ceiling(limit * (100 + (accommodation?.ExtraTimePercent ?? 0)) / 100.0) : null;

    /// <summary>
    /// Deals one learner's attempt: every question outside a pool, plus the pool's draw count picked at random from each pool, in the assessment's
    /// order or shuffled; choice questions get their options shuffled when the assessment asks for it.
    /// </summary>
    private static async Task<(List<PlanEntry> Plan, int PossiblePoints)> DealAsync(LmsDbContext db, Assessment assessment, CancellationToken cancellationToken)
    {
        var links = await LinksAsync(db, assessment, assessment.CurrentVersion, cancellationToken);
        var pools = await db.AssessmentPools.AsNoTracking().Where(item => item.AssessmentId == assessment.Id && item.Version == assessment.CurrentVersion).ToListAsync(cancellationToken);
        var questions = await QuestionsAsync(db, links.Select(link => link.QuestionId), cancellationToken);

        var dealt = links.Where(link => string.IsNullOrEmpty(link.PoolName)).ToList();
        foreach (var group in links.Where(link => !string.IsNullOrEmpty(link.PoolName)).GroupBy(link => link.PoolName!, StringComparer.OrdinalIgnoreCase))
        {
            var draw = pools.FirstOrDefault(pool => pool.Name.Equals(group.Key, StringComparison.OrdinalIgnoreCase))?.DrawCount ?? group.Count();
            dealt.AddRange(Shuffled(group.ToList()).Take(Math.Min(draw, group.Count())));
        }
        dealt = assessment.ShuffleQuestions ? Shuffled(dealt) : dealt.OrderBy(link => link.DisplayOrder).ToList();

        var plan = dealt.Select(link =>
        {
            string[]? order = null;
            if (assessment.ShuffleOptions && questions.TryGetValue(link.QuestionId, out var question) && question.Type is QuestionType.MultipleChoice or QuestionType.MultipleResponse)
                order = Shuffled(DeserializeArray(question.OptionsJson).ToList()).ToArray();
            return new PlanEntry(link.QuestionId, order);
        }).ToList();
        return (plan, dealt.Sum(link => link.Points));
    }

    private static List<T> Shuffled<T>(List<T> items)
    {
        var copy = items.ToList();
        for (var index = copy.Count - 1; index > 0; index--)
        {
            var other = Random.Shared.Next(index + 1);
            (copy[index], copy[other]) = (copy[other], copy[index]);
        }
        return copy;
    }

    private static async Task<IResult> GetAttemptAsync(HttpContext httpContext, LmsDbContext db, Guid attemptId, CancellationToken cancellationToken)
    {
        var attempt = await db.AssessmentAttempts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == attemptId, cancellationToken);
        if (attempt is null) return Results.NotFound();
        // Only the learner and the people who grade may open an attempt: it holds their answers and uploaded files.
        var isGrader = HasPermission(httpContext, LmsPermissions.GradeManage);
        if (!isGrader && attempt.LearnerUserId != GetUserId(httpContext)) return Results.Forbid();
        var includeCorrect = HasPermission(httpContext, LmsPermissions.AssessmentManage) || isGrader;
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
        if (!(await PlanAsync(db, attempt, cancellationToken)).Any(entry => entry.Q == questionId) || await db.Questions.SingleOrDefaultAsync(item => item.Id == questionId, cancellationToken) is null) return Results.NotFound();
        var text = request.Text?.Trim();
        if (text is { Length: > 20000 }) return Results.BadRequest(new { message = "The answer is too long." });

        var answer = await db.AssessmentAnswers.SingleOrDefaultAsync(item => item.AttemptId == attemptId && item.QuestionId == questionId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (answer is null)
        {
            answer = new AssessmentAnswer { Id = Guid.NewGuid(), TenantId = attempt.TenantId, AttemptId = attemptId, QuestionId = questionId, AnswerJson = "{}", AnsweredAtUtc = now, UpdatedAtUtc = now };
            db.AssessmentAnswers.Add(answer);
        }
        // Saving text or choices never drops a file the learner already uploaded.
        var existingFile = DeserializeAnswer(answer.AnswerJson).File;
        answer.AnswerJson = JsonSerializer.Serialize(new StoredAnswer((request.Answers ?? []).Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()).ToArray(), text, null, existingFile));
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
        var plan = await PlanAsync(db, attempt, cancellationToken);
        var questionIds = plan.Select(entry => entry.Q).ToArray();
        var links = await db.AssessmentQuestions.AsNoTracking().Where(item => item.AssessmentId == attempt.AssessmentId && item.Version == attempt.Version && questionIds.Contains(item.QuestionId)).ToDictionaryAsync(item => item.QuestionId, cancellationToken);
        var questions = await db.Questions.Where(item => questionIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, cancellationToken);
        var answers = await db.AssessmentAnswers.Where(item => item.AttemptId == attemptId).ToDictionaryAsync(item => item.QuestionId, cancellationToken);
        var hasManualReview = false;
        var totalScore = 0;
        foreach (var entry in plan)
        {
            if (!questions.TryGetValue(entry.Q, out var question) || !links.TryGetValue(entry.Q, out var link)) continue;
            if (!answers.TryGetValue(entry.Q, out var answer))
            {
                answer = new AssessmentAnswer { Id = Guid.NewGuid(), TenantId = attempt.TenantId, AttemptId = attemptId, QuestionId = entry.Q, AnswerJson = JsonSerializer.Serialize(new StoredAnswer([], null, null)), AnsweredAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow };
                db.AssessmentAnswers.Add(answer);
            }
            var grading = GradeObjectiveQuestion(question, answer.AnswerJson);
            answer.ScorePoints = grading.ScorePoints * link.Points / Math.Max(1, question.Points);
            answer.IsCorrect = grading.IsCorrect;
            if (question.Type is QuestionType.Essay or QuestionType.FileUpload) hasManualReview = true;
            totalScore += answer.ScorePoints;
        }
        attempt.ScorePoints = totalScore;
        attempt.PossiblePoints = plan.Sum(entry => links.TryGetValue(entry.Q, out var link) ? link.Points : 0);
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

    // ---------- grading ----------
    private static async Task<IResult> ListAttemptsAsync(LmsDbContext db, Guid assessmentId, CancellationToken cancellationToken)
    {
        var assessment = await db.Assessments.AsNoTracking().SingleOrDefaultAsync(item => item.Id == assessmentId, cancellationToken);
        if (assessment is null) return Results.NotFound();
        var attempts = await db.AssessmentAttempts.AsNoTracking().Where(item => item.AssessmentId == assessmentId).OrderByDescending(item => item.StartedAtUtc).ToListAsync(cancellationToken);
        var learnerIds = attempts.Select(item => item.LearnerUserId).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(item => learnerIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, item => item.DisplayName, cancellationToken);
        return Results.Ok(attempts.Select(item => ToSummary(item, names.GetValueOrDefault(item.LearnerUserId))).ToList());
    }

    private static async Task<IResult> GradeAttemptAsync(LmsDbContext db, NotificationService notifications, Guid attemptId, GradeAttemptRequest request, CancellationToken cancellationToken)
    {
        var attempt = await db.AssessmentAttempts.SingleOrDefaultAsync(item => item.Id == attemptId, cancellationToken);
        if (attempt is null) return Results.NotFound();
        if (attempt.Status != AttemptStatus.Submitted) return Results.Conflict(new { message = "Only submitted attempts require teacher grading." });

        if (request.Answers is null)
        {
            // The simple way: one total for the whole attempt.
            if (request.ScorePoints < 0 || request.ScorePoints > attempt.PossiblePoints) return Results.ValidationProblem(new Dictionary<string, string[]> { ["scorePoints"] = ["Score must be between zero and the possible points."] });
            attempt.ScorePoints = request.ScorePoints;
        }
        else
        {
            // Question by question: every essay and file question is scored (by rubric when it has one) and the total is worked out here.
            var plan = await PlanAsync(db, attempt, cancellationToken);
            var questionIds = plan.Select(entry => entry.Q).ToArray();
            var questions = await db.Questions.AsNoTracking().Where(item => questionIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, cancellationToken);
            var links = await db.AssessmentQuestions.AsNoTracking().Where(item => item.AssessmentId == attempt.AssessmentId && item.Version == attempt.Version && questionIds.Contains(item.QuestionId)).ToDictionaryAsync(item => item.QuestionId, cancellationToken);
            var answers = await db.AssessmentAnswers.Where(item => item.AttemptId == attemptId).ToDictionaryAsync(item => item.QuestionId, cancellationToken);
            var rubricIds = questions.Values.Where(item => item.RubricId is not null).Select(item => item.RubricId!.Value).Distinct().ToList();
            var rubrics = await db.Rubrics.AsNoTracking().Where(item => rubricIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, cancellationToken);

            var manual = plan.Where(entry => questions.TryGetValue(entry.Q, out var question) && question.Type is QuestionType.Essay or QuestionType.FileUpload).Select(entry => entry.Q).ToList();
            if (request.Answers.Any(item => !manual.Contains(item.QuestionId))) return Results.ValidationProblem(new Dictionary<string, string[]> { ["answers"] = ["A grade names a question that does not need grading in this attempt."] });
            foreach (var questionId in manual)
            {
                var given = request.Answers.Where(item => item.QuestionId == questionId).ToList();
                if (given.Count != 1) return Results.ValidationProblem(new Dictionary<string, string[]> { ["answers"] = [$"Grade “{Shorten(questions[questionId].Prompt)}” once."] });
                var question = questions[questionId];
                var answer = answers[questionId];
                if (given[0].Feedback is { Length: > 4000 }) return Results.ValidationProblem(new Dictionary<string, string[]> { ["answers"] = ["Feedback is too long."] });
                if (question.RubricId is Guid rubricId && rubrics.TryGetValue(rubricId, out var rubric))
                {
                    if (RubricRules.Score(RubricRules.Read(rubric.CriteriaJson), given[0].CriterionScores, out var scores) is { } problem) return Results.ValidationProblem(new Dictionary<string, string[]> { ["answers"] = [problem] });
                    answer.ScorePoints = scores.Sum(item => item.Points);
                    answer.RubricScoresJson = JsonSerializer.Serialize(scores);
                }
                else
                {
                    var most = links[questionId].Points;
                    if (given[0].ScorePoints is not int points || points < 0 || points > most) return Results.ValidationProblem(new Dictionary<string, string[]> { ["answers"] = [$"“{Shorten(question.Prompt)}” is scored from 0 to {most}."] });
                    answer.ScorePoints = points;
                }
                answer.Feedback = string.IsNullOrWhiteSpace(given[0].Feedback) ? null : given[0].Feedback!.Trim();
                answer.UpdatedAtUtc = DateTimeOffset.UtcNow;
            }
            attempt.ScorePoints = answers.Where(item => plan.Any(entry => entry.Q == item.Key)).Sum(item => item.Value.ScorePoints);
        }

        attempt.Percentage = attempt.PossiblePoints == 0 ? 0 : Math.Round(attempt.ScorePoints * 100m / attempt.PossiblePoints, 2);
        attempt.TeacherFeedback = request.Feedback?.Trim();
        attempt.Status = AttemptStatus.Graded;
        attempt.GradedAtUtc = DateTimeOffset.UtcNow;
        var assessment = await db.Assessments.AsNoTracking().SingleOrDefaultAsync(item => item.Id == attempt.AssessmentId, cancellationToken);
        if (assessment is not null)
            await notifications.QueueAsync(db, attempt.TenantId, attempt.LearnerUserId, "ASSESSMENT_GRADED", new Dictionary<string, string> { ["AssessmentTitle"] = assessment.Title, ["Percentage"] = attempt.Percentage?.ToString("0.##") ?? "0" }, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToSummary(attempt, null));
    }

    private static string Shorten(string text) => text.Length <= 60 ? text : text[..60] + "…";

    // ---------- building responses ----------
    private static async Task<AttemptDetailResponse> BuildAttemptAsync(LmsDbContext db, AssessmentAttempt attempt, bool includeCorrect, CancellationToken cancellationToken)
    {
        var assessment = await db.Assessments.AsNoTracking().SingleAsync(item => item.Id == attempt.AssessmentId, cancellationToken);
        var plan = await PlanAsync(db, attempt, cancellationToken);
        var ids = plan.Select(entry => entry.Q).ToArray();
        var links = await db.AssessmentQuestions.AsNoTracking().Where(item => item.AssessmentId == assessment.Id && item.Version == attempt.Version && ids.Contains(item.QuestionId)).ToDictionaryAsync(item => item.QuestionId, cancellationToken);
        var questions = await QuestionsAsync(db, ids, cancellationToken);
        var answers = await db.AssessmentAnswers.AsNoTracking().Where(item => item.AttemptId == attempt.Id).ToDictionaryAsync(item => item.QuestionId, cancellationToken);
        var rubricIds = questions.Values.Where(item => item.RubricId is not null).Select(item => item.RubricId!.Value).Distinct().ToList();
        var rubrics = await db.Rubrics.AsNoTracking().Where(item => rubricIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, cancellationToken);
        var showScores = attempt.Status == AttemptStatus.Graded || includeCorrect;

        var order = 0;
        var items = plan.Where(entry => questions.ContainsKey(entry.Q) && links.ContainsKey(entry.Q)).Select(entry =>
        {
            var question = questions[entry.Q];
            answers.TryGetValue(question.Id, out var answer);
            var stored = answer is null ? null : DeserializeAnswer(answer.AnswerJson);
            var options = entry.O is { Length: > 0 } ? entry.O : DeserializeArray(question.OptionsJson);
            RubricView? rubric = question.RubricId is Guid rubricId && rubrics.TryGetValue(rubricId, out var found)
                ? new RubricView(found.Id, found.Name, found.TotalPoints, RubricRules.Read(found.CriteriaJson)) : null;
            var scores = showScores && answer?.RubricScoresJson is { } json ? JsonSerializer.Deserialize<List<CriterionScore>>(json) : null;
            return new AttemptQuestionResponse(question.Id, question.Type.ToString(), question.Prompt, options, includeCorrect ? DeserializeArray(question.CorrectAnswerJson) : [], ++order, links[question.Id].Points,
                stored?.Answers ?? [], showScores ? answer?.ScorePoints ?? 0 : 0, answer?.IsCorrect, showScores ? answer?.Feedback : null,
                stored?.Text, stored?.File is { } file ? new AnswerFileView(file.FileName, file.SizeBytes) : null, rubric, scores);
        }).ToArray();
        return new AttemptDetailResponse(ToSummary(attempt, null), assessment.Title, assessment.Instructions, LimitMinutes(attempt, assessment), items, attempt.TeacherFeedback, GetExpiry(attempt, assessment));
    }

    internal static AssessmentResponse ToAssessmentResponse(Assessment item, int questionCount)
        => new(item.Id, item.CourseId, item.Title, item.Instructions, item.Status.ToString(), item.TimeLimitMinutes, item.AttemptLimit, questionCount, item.CreatedAtUtc, item.PublishedAtUtc,
            item.ShuffleQuestions, item.ShuffleOptions, item.CurrentVersion, item.DraftVersion, item.OpensAtUtc, item.DueAtUtc);

    internal static AttemptSummaryResponse ToSummary(AssessmentAttempt item, string? learnerName)
        => new(item.Id, item.LearnerUserId, item.AttemptNumber, item.Status.ToString(), item.ScorePoints, item.PossiblePoints, item.Percentage, item.StartedAtUtc, item.SubmittedAtUtc, item.GradedAtUtc, item.SubmittedAfterTimeLimit, learnerName, item.Version);

    internal static QuestionResponse ToQuestionResponse(Question item, bool includeCorrect, int displayOrder, int points, string? pool = null, IReadOnlyDictionary<Guid, string>? rubricNames = null)
        => new(item.Id, item.Type.ToString(), item.Prompt, DeserializeArray(item.OptionsJson), includeCorrect ? DeserializeArray(item.CorrectAnswerJson) : [], displayOrder, points, pool, item.RubricId,
            item.RubricId is Guid id && rubricNames is not null ? rubricNames.GetValueOrDefault(id) : null);

    // ---------- shared with the authoring endpoints ----------
    /// <summary>The version staff may change: the draft version when one exists, the current one while the assessment is still a draft, otherwise none.</summary>
    internal static int? EditingVersion(Assessment assessment) => assessment.Status == AssessmentStatus.Draft ? assessment.CurrentVersion : assessment.DraftVersion;

    internal static async Task<List<AssessmentQuestion>> LinksAsync(LmsDbContext db, Assessment assessment, int version, CancellationToken cancellationToken)
        => await db.AssessmentQuestions.AsNoTracking().Where(item => item.AssessmentId == assessment.Id && item.Version == version).OrderBy(item => item.DisplayOrder).ToListAsync(cancellationToken);

    internal static async Task<Dictionary<Guid, Question>> QuestionsAsync(LmsDbContext db, IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var list = ids.ToList();
        return await db.Questions.AsNoTracking().Where(item => list.Contains(item.Id)).ToDictionaryAsync(item => item.Id, cancellationToken);
    }

    internal static async Task<Dictionary<Guid, string>> RubricNamesAsync(LmsDbContext db, IEnumerable<Question> questions, CancellationToken cancellationToken)
    {
        var ids = questions.Where(item => item.RubricId is not null).Select(item => item.RubricId!.Value).Distinct().ToList();
        return await db.Rubrics.AsNoTracking().Where(item => ids.Contains(item.Id)).ToDictionaryAsync(item => item.Id, item => item.Name, cancellationToken);
    }

    /// <summary>How many questions one attempt gets: everything outside a pool, plus each pool's draw count.</summary>
    internal static int DealtCount(IEnumerable<AssessmentQuestion> links, IEnumerable<AssessmentPool> pools)
    {
        var list = links.ToList();
        var count = list.Count(link => string.IsNullOrEmpty(link.PoolName));
        foreach (var group in list.Where(link => !string.IsNullOrEmpty(link.PoolName)).GroupBy(link => link.PoolName!, StringComparer.OrdinalIgnoreCase))
            count += Math.Min(group.Count(), pools.FirstOrDefault(pool => pool.Name.Equals(group.Key, StringComparison.OrdinalIgnoreCase))?.DrawCount ?? group.Count());
        return count;
    }

    /// <summary>The questions an attempt was dealt. Attempts made before plans existed get every question of their version in order.</summary>
    internal static async Task<List<PlanEntry>> PlanAsync(LmsDbContext db, AssessmentAttempt attempt, CancellationToken cancellationToken)
    {
        if (attempt.PlanJson is not null)
        {
            try { return JsonSerializer.Deserialize<List<PlanEntry>>(attempt.PlanJson) ?? []; }
            catch (JsonException) { return []; }
        }
        var links = await db.AssessmentQuestions.AsNoTracking().Where(item => item.AssessmentId == attempt.AssessmentId && item.Version == attempt.Version).OrderBy(item => item.DisplayOrder).ToListAsync(cancellationToken);
        return links.Select(link => new PlanEntry(link.QuestionId, null)).ToList();
    }

    private static ObjectiveGrade GradeObjectiveQuestion(Question question, string answerJson)
    {
        if (question.Type is QuestionType.Essay or QuestionType.FileUpload) return new ObjectiveGrade(0, null);
        var stored = DeserializeAnswer(answerJson);
        var expected = DeserializeArray(question.CorrectAnswerJson).Select(Normalize).Where(item => item.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
        if (question.Type == QuestionType.ShortAnswer)
        {
            // Typed text counts when it matches any one of the accepted answers.
            var typed = Normalize(stored.Text ?? string.Join(' ', stored.Answers));
            var accepted = typed.Length > 0 && expected.Contains(typed, StringComparer.OrdinalIgnoreCase);
            return new ObjectiveGrade(accepted ? question.Points : 0, accepted);
        }
        var answer = stored.Answers.Select(Normalize).Where(item => item.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
        var correct = answer.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase);
        return new ObjectiveGrade(correct ? question.Points : 0, correct);
    }

    internal static string[] DeserializeArray(string json)
    {
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    internal static StoredAnswer DeserializeAnswer(string json)
    {
        try { return JsonSerializer.Deserialize<StoredAnswer>(json) ?? new StoredAnswer([], null, null); }
        catch (JsonException) { return new StoredAnswer([], null, null); }
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();
    /// <summary>The attempt's own limit (with any accommodation); attempts from before plans existed use the assessment's.</summary>
    internal static int? LimitMinutes(AssessmentAttempt attempt, Assessment assessment) => attempt.PlanJson is null ? assessment.TimeLimitMinutes : attempt.TimeLimitMinutes;
    internal static bool IsExpired(AssessmentAttempt attempt, Assessment assessment) => GetExpiry(attempt, assessment) is DateTimeOffset expiry && expiry <= DateTimeOffset.UtcNow;
    /// <summary>
    /// When an attempt must end: its time limit after it started, or the assessment's deadline, whichever is first.
    /// A learner with extra time gets the same extra minutes past the deadline, so an accommodation is not cut off by the close.
    /// </summary>
    internal static DateTimeOffset? GetExpiry(AssessmentAttempt attempt, Assessment assessment)
    {
        DateTimeOffset? byLimit = LimitMinutes(attempt, assessment) is > 0 and int minutes ? attempt.StartedAtUtc.AddMinutes(minutes) : null;
        var extra = attempt.PlanJson is not null && attempt.TimeLimitMinutes is int given && assessment.TimeLimitMinutes is int baseMinutes ? Math.Max(0, given - baseMinutes) : 0;
        DateTimeOffset? byDeadline = assessment.DueAtUtc?.AddMinutes(extra);
        return byLimit is null ? byDeadline : byDeadline is null ? byLimit : (byLimit < byDeadline ? byLimit : byDeadline);
    }
    internal static Guid? GetUserId(HttpContext httpContext) => Guid.TryParse(httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    internal static bool HasPermission(HttpContext httpContext, string permission) => httpContext.User.HasClaim("permission", permission);

    private sealed record ObjectiveGrade(int ScorePoints, bool? IsCorrect);
}

/// <summary>A file a learner attached to a question, kept in content storage.</summary>
internal sealed record AnswerFile(string StorageKey, string FileName, string ContentType, long SizeBytes);
internal sealed record StoredAnswer(string[] Answers, string? Text, Guid? FileAssetId, AnswerFile? File = null);
/// <summary>One dealt question: its id and, for choice questions, the order this learner sees the options in.</summary>
internal sealed record PlanEntry(Guid Q, string[]? O);

public sealed record CreateAssessmentRequest(string Title, string? Instructions = null, int? TimeLimitMinutes = null, int? AttemptLimit = null, bool? ShuffleQuestions = null, bool? ShuffleOptions = null, DateTimeOffset? OpensAtUtc = null, DateTimeOffset? DueAtUtc = null);
public sealed record UpdateAssessmentRequest(string Title, string? Instructions = null, int? TimeLimitMinutes = null, int? AttemptLimit = null, bool? ShuffleQuestions = null, bool? ShuffleOptions = null, DateTimeOffset? OpensAtUtc = null, DateTimeOffset? DueAtUtc = null);
public sealed record AddQuestionRequest(string Type, string Prompt, string[]? Options = null, string[]? CorrectAnswers = null, int Points = 1, string? Pool = null, Guid? RubricId = null);
public sealed record SaveAnswerRequest(string[]? Answers = null, string? Text = null, Guid? FileAssetId = null);
/// <summary>Either one total (ScorePoints), or Answers with a score or rubric scores for every essay and file question.</summary>
public sealed record GradeAttemptRequest(int ScorePoints, string? Feedback = null, List<AnswerGrade>? Answers = null);
public sealed record AnswerGrade(Guid QuestionId, int? ScorePoints = null, List<CriterionScoreInput>? CriterionScores = null, string? Feedback = null);
public sealed record AssessmentResponse(Guid Id, Guid CourseId, string Title, string? Instructions, string Status, int? TimeLimitMinutes, int AttemptLimit, int QuestionCount, DateTimeOffset CreatedAtUtc, DateTimeOffset? PublishedAtUtc,
    bool ShuffleQuestions = false, bool ShuffleOptions = false, int CurrentVersion = 1, int? DraftVersion = null, DateTimeOffset? OpensAtUtc = null, DateTimeOffset? DueAtUtc = null);
public sealed record QuestionResponse(Guid Id, string Type, string Prompt, string[] Options, string[] CorrectAnswers, int DisplayOrder, int Points, string? Pool = null, Guid? RubricId = null, string? RubricName = null);
public sealed record PoolResponse(string Name, int DrawCount, int QuestionCount);
public sealed record MyAccommodation(int ExtraTimePercent, int ExtraAttempts);
/// <summary>Staff get the questions of the version they can edit and the pools; a learner gets no questions, only how the assessment works for them (time and attempts including any accommodation).</summary>
public sealed record AssessmentDetailResponse(AssessmentResponse Assessment, QuestionResponse[] Questions, PoolResponse[]? Pools = null, bool EditingDraftVersion = false,
    int? EffectiveTimeLimitMinutes = null, int? EffectiveAttemptLimit = null, int? AttemptsUsed = null, MyAccommodation? Accommodation = null);
public sealed record AttemptSummaryResponse(Guid Id, Guid LearnerUserId, int AttemptNumber, string Status, int ScorePoints, int PossiblePoints, decimal? Percentage, DateTimeOffset StartedAtUtc, DateTimeOffset? SubmittedAtUtc, DateTimeOffset? GradedAtUtc,
    bool SubmittedAfterTimeLimit = false, string? LearnerName = null, int Version = 1);
public sealed record AnswerFileView(string FileName, long SizeBytes);
public sealed record RubricView(Guid Id, string Name, int TotalPoints, List<RubricCriterion> Criteria);
public sealed record AttemptQuestionResponse(Guid Id, string Type, string Prompt, string[] Options, string[] CorrectAnswers, int DisplayOrder, int Points, string[] Answers, int ScorePoints, bool? IsCorrect, string? Feedback,
    string? Text = null, AnswerFileView? File = null, RubricView? Rubric = null, List<CriterionScore>? RubricScores = null);
public sealed record AttemptDetailResponse(AttemptSummaryResponse Attempt, string AssessmentTitle, string? Instructions, int? TimeLimitMinutes, AttemptQuestionResponse[] Questions, string? TeacherFeedback, DateTimeOffset? ExpiresAtUtc = null);
