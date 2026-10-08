using System.Text.Json;
using Lms.Api.Domain.Assessments;
using Lms.Api.Domain.Courses;
using Lms.Api.Domain.Learning;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Security;
using Lms.Api.Infrastructure.Storage;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using static Lms.Api.Features.Assessments.AssessmentEndpoints;

namespace Lms.Api.Features.Assessments;

/// <summary>
/// Building assessments: questions (edited, pooled, scored by a rubric), new versions of a published assessment, reusable rubrics,
/// learner accommodations, and the files learners attach to file-upload questions.
/// A published assessment is changed through a draft version, a copy of its questions: learners keep using the current version until the draft is published,
/// and attempts already made stay with the version they were made on.
/// </summary>
public static class AssessmentAuthoringEndpoints
{
    private const int MaxPools = 20;

    public static void MapAssessmentAuthoringEndpoints(this WebApplication app)
    {
        var tenant = app.MapGroup("/api/v1/tenant").RequireAuthorization("tenant.authenticated");
        tenant.AddEndpointFilter(async (context, next) =>
            context.HttpContext.RequestServices.GetRequiredService<ITenantContext>().IsResolved
                ? await next(context)
                : Results.BadRequest(new { message = "A tenant is required. Use the X-Tenant-Slug header or a tenant subdomain." }));

        tenant.MapPut("/assessments/{assessmentId:guid}", UpdateSettingsAsync).RequireAuthorization("tenant.assessment.manage");
        tenant.MapPost("/assessments/{assessmentId:guid}/questions", AddQuestionAsync).RequireAuthorization("tenant.assessment.manage");
        tenant.MapPut("/assessments/{assessmentId:guid}/questions/{questionId:guid}", UpdateQuestionAsync).RequireAuthorization("tenant.assessment.manage");
        tenant.MapDelete("/assessments/{assessmentId:guid}/questions/{questionId:guid}", DeleteQuestionAsync).RequireAuthorization("tenant.assessment.manage");
        tenant.MapPut("/assessments/{assessmentId:guid}/pools/{name}", SetPoolAsync).RequireAuthorization("tenant.assessment.manage");
        tenant.MapPost("/assessments/{assessmentId:guid}/publish", PublishAsync).RequireAuthorization("tenant.assessment.manage");
        tenant.MapPost("/assessments/{assessmentId:guid}/versions", StartVersionAsync).RequireAuthorization("tenant.assessment.manage");
        tenant.MapDelete("/assessments/{assessmentId:guid}/versions/draft", DiscardVersionAsync).RequireAuthorization("tenant.assessment.manage");

        tenant.MapGet("/courses/{courseId:guid}/rubrics", ListRubricsAsync).RequireAuthorization("tenant.assessment.manage");
        tenant.MapPost("/courses/{courseId:guid}/rubrics", CreateRubricAsync).RequireAuthorization("tenant.assessment.manage");
        tenant.MapPut("/rubrics/{rubricId:guid}", UpdateRubricAsync).RequireAuthorization("tenant.assessment.manage");
        tenant.MapDelete("/rubrics/{rubricId:guid}", DeleteRubricAsync).RequireAuthorization("tenant.assessment.manage");

        tenant.MapGet("/courses/{courseId:guid}/accommodations", ListAccommodationsAsync).RequireAuthorization("tenant.assessment.manage");
        tenant.MapPut("/courses/{courseId:guid}/accommodations/{learnerUserId:guid}", SetAccommodationAsync).RequireAuthorization("tenant.assessment.manage");
        tenant.MapDelete("/courses/{courseId:guid}/accommodations/{learnerUserId:guid}", RemoveAccommodationAsync).RequireAuthorization("tenant.assessment.manage");

        tenant.MapPost("/assessment-attempts/{attemptId:guid}/answers/{questionId:guid}/file", UploadAnswerFileAsync).RequireAuthorization("tenant.assessment.attempt");
        tenant.MapDelete("/assessment-attempts/{attemptId:guid}/answers/{questionId:guid}/file", RemoveAnswerFileAsync).RequireAuthorization("tenant.assessment.attempt");
        tenant.MapGet("/assessment-attempts/{attemptId:guid}/answers/{questionId:guid}/file", DownloadAnswerFileAsync).RequireAuthorization("tenant.assessment.attempt");
    }

    private static IResult Problem(string field, string message) => Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    // ---------- settings ----------
    private static async Task<IResult> UpdateSettingsAsync(LmsDbContext db, Guid assessmentId, UpdateAssessmentRequest request, CancellationToken cancellationToken)
    {
        if (ValidateSettings(request.Title, request.TimeLimitMinutes, request.AttemptLimit, request.OpensAtUtc, request.DueAtUtc) is { } invalid) return invalid;
        var assessment = await db.Assessments.SingleOrDefaultAsync(item => item.Id == assessmentId, cancellationToken);
        if (assessment is null) return Results.NotFound();
        // These only shape attempts started from now on; attempts under way keep the limit and questions they were given.
        assessment.Title = request.Title.Trim();
        assessment.Instructions = string.IsNullOrWhiteSpace(request.Instructions) ? null : request.Instructions.Trim();
        assessment.TimeLimitMinutes = request.TimeLimitMinutes;
        assessment.AttemptLimit = request.AttemptLimit ?? assessment.AttemptLimit;
        assessment.ShuffleQuestions = request.ShuffleQuestions ?? assessment.ShuffleQuestions;
        assessment.ShuffleOptions = request.ShuffleOptions ?? assessment.ShuffleOptions;
        assessment.OpensAtUtc = request.OpensAtUtc;
        assessment.DueAtUtc = request.DueAtUtc;
        assessment.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        var links = await LinksAsync(db, assessment, assessment.CurrentVersion, cancellationToken);
        var pools = await db.AssessmentPools.AsNoTracking().Where(item => item.AssessmentId == assessmentId && item.Version == assessment.CurrentVersion).ToListAsync(cancellationToken);
        return Results.Ok(ToAssessmentResponse(assessment, DealtCount(links, pools)));
    }

    // ---------- questions ----------
    private sealed record QuestionData(QuestionType Type, string Prompt, string[] Options, string[] Correct, int Points, string? Pool, Guid? RubricId);

    /// <summary>Checks a question the way a person typed it. Returns the cleaned question, or the problem to send back.</summary>
    private static async Task<(QuestionData? Data, IResult? Error)> ValidateQuestionAsync(LmsDbContext db, Assessment assessment, AddQuestionRequest request, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<QuestionType>(request.Type, true, out var type) || !Enum.IsDefined(type)) return (null, Problem("type", "Use MultipleChoice, MultipleResponse, TrueFalse, ShortAnswer, Essay, or FileUpload."));
        if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Trim().Length > 10000) return (null, Problem("prompt", "Question prompt is required and must be 10,000 characters or fewer."));
        var points = request.Points;
        var options = (request.Options ?? []).Select(item => item?.Trim() ?? string.Empty).Where(item => item.Length > 0).ToArray();
        if (type == QuestionType.TrueFalse && options.Length == 0) options = ["true", "false"];
        if (type is QuestionType.MultipleChoice or QuestionType.MultipleResponse or QuestionType.TrueFalse && options.Length < 2) return (null, Problem("options", "Choice questions require at least two options."));
        if (options.Length != options.Distinct(StringComparer.OrdinalIgnoreCase).Count()) return (null, Problem("options", "Options must all be different."));
        var correct = (request.CorrectAnswers ?? []).Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()).ToArray();
        if (type is not (QuestionType.Essay or QuestionType.FileUpload) && correct.Length == 0) return (null, Problem("correctAnswers", "Objective questions require at least one correct answer."));
        if (type == QuestionType.MultipleResponse && correct.Length < 2) return (null, Problem("correctAnswers", "Multiple response questions require at least two correct answers."));
        if (type != QuestionType.ShortAnswer && correct.Any(answer => options.Length > 0 && !options.Contains(answer, StringComparer.OrdinalIgnoreCase))) return (null, Problem("correctAnswers", "Every correct answer must match one of the supplied options."));

        Guid? rubricId = null;
        if (request.RubricId is Guid wanted)
        {
            if (type is not (QuestionType.Essay or QuestionType.FileUpload)) return (null, Problem("rubricId", "Only essay and file-upload questions are scored with a rubric."));
            var rubric = await db.Rubrics.AsNoTracking().SingleOrDefaultAsync(item => item.Id == wanted && item.CourseId == assessment.CourseId, cancellationToken);
            if (rubric is null) return (null, Problem("rubricId", "That rubric was not found in this course."));
            rubricId = rubric.Id;
            points = rubric.TotalPoints;   // a rubric question is worth exactly what its rubric adds up to
        }
        if (points is < 1 or > 100) return (null, Problem("points", "Points must be between 1 and 100."));

        var pool = string.IsNullOrWhiteSpace(request.Pool) ? null : request.Pool.Trim();
        if (pool is { Length: > 100 }) return (null, Problem("pool", "A pool name can have at most 100 characters."));
        return (new QuestionData(type, request.Prompt.Trim(), options, correct, points, pool, rubricId), null);
    }

    private static async Task<(Assessment? Assessment, int Version, IResult? Error)> EditableAsync(LmsDbContext db, Guid assessmentId, CancellationToken cancellationToken)
    {
        var assessment = await db.Assessments.SingleOrDefaultAsync(item => item.Id == assessmentId, cancellationToken);
        if (assessment is null) return (null, 0, Results.NotFound());
        if (EditingVersion(assessment) is not int version)
            return (assessment, 0, Results.Conflict(new { message = "This assessment is published. Start a new version to change its questions." }));
        return (assessment, version, null);
    }

    /// <summary>Makes sure the pool exists in this version (drawing one question by default) and removes pools no question uses any more.</summary>
    private static async Task TidyPoolsAsync(LmsDbContext db, Assessment assessment, int version, CancellationToken cancellationToken)
    {
        var used = (await db.AssessmentQuestions.AsNoTracking().Where(item => item.AssessmentId == assessment.Id && item.Version == version && item.PoolName != null).Select(item => item.PoolName!).ToListAsync(cancellationToken))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var pools = await db.AssessmentPools.Where(item => item.AssessmentId == assessment.Id && item.Version == version).ToListAsync(cancellationToken);
        db.AssessmentPools.RemoveRange(pools.Where(pool => !used.Contains(pool.Name, StringComparer.OrdinalIgnoreCase)));
        foreach (var name in used.Where(name => !pools.Any(pool => pool.Name.Equals(name, StringComparison.OrdinalIgnoreCase))))
            db.AssessmentPools.Add(new AssessmentPool { Id = Guid.NewGuid(), TenantId = assessment.TenantId, AssessmentId = assessment.Id, Version = version, Name = name, DrawCount = 1 });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Spells a pool the way the pool already is in this version, so "Algebra" and "algebra" are one pool.</summary>
    private static async Task<string?> CanonicalPoolAsync(LmsDbContext db, Assessment assessment, int version, string? pool, CancellationToken cancellationToken)
    {
        if (pool is null) return null;
        var existing = await db.AssessmentQuestions.AsNoTracking().Where(item => item.AssessmentId == assessment.Id && item.Version == version && item.PoolName != null).Select(item => item.PoolName!).ToListAsync(cancellationToken);
        return existing.FirstOrDefault(name => name.Equals(pool, StringComparison.OrdinalIgnoreCase)) ?? pool;
    }

    private static async Task<IResult> AddQuestionAsync(LmsDbContext db, Guid assessmentId, AddQuestionRequest request, CancellationToken cancellationToken)
    {
        var (assessment, version, notEditable) = await EditableAsync(db, assessmentId, cancellationToken);
        if (notEditable is not null) return notEditable;
        var (data, error) = await ValidateQuestionAsync(db, assessment!, request, cancellationToken);
        if (error is not null) return error;
        var pool = await CanonicalPoolAsync(db, assessment!, version, data!.Pool, cancellationToken);
        if (pool is not null && !await PoolHasRoomAsync(db, assessment!, version, pool, null, cancellationToken)) return Problem("pool", $"An assessment can have at most {MaxPools} pools.");

        var now = DateTimeOffset.UtcNow;
        var question = new Question { Id = Guid.NewGuid(), TenantId = assessment!.TenantId, QuestionBankId = assessment.QuestionBankId, Type = data.Type, Prompt = data.Prompt, OptionsJson = JsonSerializer.Serialize(data.Options), CorrectAnswerJson = JsonSerializer.Serialize(data.Correct), Points = data.Points, RubricId = data.RubricId, CreatedAtUtc = now, UpdatedAtUtc = now };
        var displayOrder = await db.AssessmentQuestions.CountAsync(item => item.AssessmentId == assessmentId && item.Version == version, cancellationToken) + 1;
        db.Questions.Add(question);
        db.AssessmentQuestions.Add(new AssessmentQuestion { AssessmentId = assessmentId, QuestionId = question.Id, DisplayOrder = displayOrder, Points = data.Points, Version = version, PoolName = pool });
        assessment.UpdatedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        await TidyPoolsAsync(db, assessment, version, cancellationToken);
        return Results.Created($"/api/v1/tenant/assessments/{assessmentId:D}/questions/{question.Id:D}", ToQuestionResponse(question, true, displayOrder, data.Points, pool));
    }

    private static async Task<bool> PoolHasRoomAsync(LmsDbContext db, Assessment assessment, int version, string pool, Guid? ignoring, CancellationToken cancellationToken)
    {
        var names = (await db.AssessmentQuestions.AsNoTracking().Where(item => item.AssessmentId == assessment.Id && item.Version == version && item.PoolName != null && item.QuestionId != ignoring).Select(item => item.PoolName!).ToListAsync(cancellationToken))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return names.Contains(pool, StringComparer.OrdinalIgnoreCase) || names.Count < MaxPools;
    }

    private static async Task<IResult> UpdateQuestionAsync(LmsDbContext db, Guid assessmentId, Guid questionId, AddQuestionRequest request, CancellationToken cancellationToken)
    {
        var (assessment, version, notEditable) = await EditableAsync(db, assessmentId, cancellationToken);
        if (notEditable is not null) return notEditable;
        var link = await db.AssessmentQuestions.SingleOrDefaultAsync(item => item.AssessmentId == assessmentId && item.QuestionId == questionId && item.Version == version, cancellationToken);
        var question = link is null ? null : await db.Questions.SingleOrDefaultAsync(item => item.Id == questionId, cancellationToken);
        if (link is null || question is null) return Results.NotFound();
        var (data, error) = await ValidateQuestionAsync(db, assessment!, request, cancellationToken);
        if (error is not null) return error;
        var pool = await CanonicalPoolAsync(db, assessment!, version, data!.Pool, cancellationToken);
        if (pool is not null && !await PoolHasRoomAsync(db, assessment!, version, pool, questionId, cancellationToken)) return Problem("pool", $"An assessment can have at most {MaxPools} pools.");

        question.Type = data.Type; question.Prompt = data.Prompt; question.OptionsJson = JsonSerializer.Serialize(data.Options); question.CorrectAnswerJson = JsonSerializer.Serialize(data.Correct);
        question.Points = data.Points; question.RubricId = data.RubricId; question.UpdatedAtUtc = DateTimeOffset.UtcNow;
        link.Points = data.Points; link.PoolName = pool;
        assessment!.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await TidyPoolsAsync(db, assessment, version, cancellationToken);
        return Results.Ok(ToQuestionResponse(question, true, link.DisplayOrder, link.Points, pool));
    }

    private static async Task<IResult> DeleteQuestionAsync(LmsDbContext db, Guid assessmentId, Guid questionId, CancellationToken cancellationToken)
    {
        var (assessment, version, notEditable) = await EditableAsync(db, assessmentId, cancellationToken);
        if (notEditable is not null) return notEditable;
        var link = await db.AssessmentQuestions.SingleOrDefaultAsync(item => item.AssessmentId == assessmentId && item.QuestionId == questionId && item.Version == version, cancellationToken);
        if (link is null) return Results.NotFound();
        // Only a version being edited can lose a question, and no attempt has ever been dealt one of its questions.
        db.Questions.RemoveRange(await db.Questions.Where(item => item.Id == questionId).ToListAsync(cancellationToken));
        db.AssessmentQuestions.Remove(link);
        await db.SaveChangesAsync(cancellationToken);
        var rest = await db.AssessmentQuestions.Where(item => item.AssessmentId == assessmentId && item.Version == version).OrderBy(item => item.DisplayOrder).ToListAsync(cancellationToken);
        for (var index = 0; index < rest.Count; index++) rest[index].DisplayOrder = index + 1;
        assessment!.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await TidyPoolsAsync(db, assessment, version, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> SetPoolAsync(LmsDbContext db, Guid assessmentId, string name, SetPoolRequest request, CancellationToken cancellationToken)
    {
        var (assessment, version, notEditable) = await EditableAsync(db, assessmentId, cancellationToken);
        if (notEditable is not null) return notEditable;
        var pool = await db.AssessmentPools.SingleOrDefaultAsync(item => item.AssessmentId == assessmentId && item.Version == version && item.Name.ToLower() == name.Trim().ToLower(), cancellationToken);
        if (pool is null) return Results.NotFound();
        var size = await db.AssessmentQuestions.CountAsync(item => item.AssessmentId == assessmentId && item.Version == version && item.PoolName != null && item.PoolName.ToLower() == pool.Name.ToLower(), cancellationToken);
        if (request.DrawCount < 1 || request.DrawCount > size) return Problem("drawCount", $"Draw between 1 and {size} question{(size == 1 ? "" : "s")} from this pool.");
        pool.DrawCount = request.DrawCount;
        assessment!.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new PoolResponse(pool.Name, pool.DrawCount, size));
    }

    // ---------- publishing and versions ----------
    private static async Task<IResult> PublishAsync(LmsDbContext db, Guid assessmentId, CancellationToken cancellationToken)
    {
        var assessment = await db.Assessments.SingleOrDefaultAsync(item => item.Id == assessmentId, cancellationToken);
        if (assessment is null) return Results.NotFound();
        if (EditingVersion(assessment) is not int version) return Results.Conflict(new { message = "Only a draft, or a new version, can be published." });
        var firstTime = assessment.Status == AssessmentStatus.Draft;

        var links = await LinksAsync(db, assessment, version, cancellationToken);
        if (links.Count == 0) return Results.Conflict(new { message = "Add at least one question before publishing." });
        var pools = await db.AssessmentPools.AsNoTracking().Where(item => item.AssessmentId == assessmentId && item.Version == version).ToListAsync(cancellationToken);
        foreach (var pool in pools)
        {
            var members = links.Where(link => string.Equals(link.PoolName, pool.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (pool.DrawCount > members.Count) return Results.Conflict(new { message = $"Pool “{pool.Name}” draws {pool.DrawCount} questions but only has {members.Count}." });
            if (members.Select(link => link.Points).Distinct().Count() > 1) return Results.Conflict(new { message = $"The questions in pool “{pool.Name}” must all be worth the same points, or learners would be scored unfairly." });
        }
        if (firstTime)
        {
            var course = await db.Courses.SingleOrDefaultAsync(item => item.Id == assessment.CourseId && item.Status == CourseStatus.Published && item.CurrentVersionId == assessment.CourseVersionId, cancellationToken);
            if (course is null) return Results.Conflict(new { message = "The assessment's course must be published first." });
            assessment.Status = AssessmentStatus.Published;
        }
        else
        {
            assessment.CurrentVersion = version;
            assessment.DraftVersion = null;
        }
        assessment.PublishedAtUtc = DateTimeOffset.UtcNow;
        assessment.UpdatedAtUtc = assessment.PublishedAtUtc.Value;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToAssessmentResponse(assessment, DealtCount(links, pools)));
    }

    private static async Task<IResult> StartVersionAsync(LmsDbContext db, Guid assessmentId, CancellationToken cancellationToken)
    {
        var assessment = await db.Assessments.SingleOrDefaultAsync(item => item.Id == assessmentId, cancellationToken);
        if (assessment is null) return Results.NotFound();
        if (assessment.Status != AssessmentStatus.Published) return Results.Conflict(new { message = "Only a published assessment needs a new version; a draft can be edited directly." });
        if (assessment.DraftVersion is not null) return Results.Conflict(new { message = "A new version is already being edited." });

        var next = assessment.CurrentVersion + 1;
        var links = await LinksAsync(db, assessment, assessment.CurrentVersion, cancellationToken);
        var questions = await QuestionsAsync(db, links.Select(link => link.QuestionId), cancellationToken);
        var now = DateTimeOffset.UtcNow;
        foreach (var link in links.Where(link => questions.ContainsKey(link.QuestionId)))
        {
            var source = questions[link.QuestionId];
            var copy = new Question { Id = Guid.NewGuid(), TenantId = source.TenantId, QuestionBankId = source.QuestionBankId, Type = source.Type, Prompt = source.Prompt, OptionsJson = source.OptionsJson, CorrectAnswerJson = source.CorrectAnswerJson, Points = source.Points, RubricId = source.RubricId, CreatedAtUtc = now, UpdatedAtUtc = now };
            db.Questions.Add(copy);
            db.AssessmentQuestions.Add(new AssessmentQuestion { AssessmentId = assessmentId, QuestionId = copy.Id, DisplayOrder = link.DisplayOrder, Points = link.Points, Version = next, PoolName = link.PoolName });
        }
        foreach (var pool in await db.AssessmentPools.AsNoTracking().Where(item => item.AssessmentId == assessmentId && item.Version == assessment.CurrentVersion).ToListAsync(cancellationToken))
            db.AssessmentPools.Add(new AssessmentPool { Id = Guid.NewGuid(), TenantId = pool.TenantId, AssessmentId = assessmentId, Version = next, Name = pool.Name, DrawCount = pool.DrawCount });
        assessment.DraftVersion = next;
        assessment.UpdatedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToAssessmentResponse(assessment, DealtCount(links, [])));
    }

    private static async Task<IResult> DiscardVersionAsync(LmsDbContext db, Guid assessmentId, CancellationToken cancellationToken)
    {
        var assessment = await db.Assessments.SingleOrDefaultAsync(item => item.Id == assessmentId, cancellationToken);
        if (assessment is null) return Results.NotFound();
        if (assessment.DraftVersion is not int draft) return Results.NotFound(new { message = "There is no new version to discard." });
        var links = await db.AssessmentQuestions.Where(item => item.AssessmentId == assessmentId && item.Version == draft).ToListAsync(cancellationToken);
        var ids = links.Select(link => link.QuestionId).ToList();
        db.AssessmentQuestions.RemoveRange(links);
        db.Questions.RemoveRange(await db.Questions.Where(item => ids.Contains(item.Id)).ToListAsync(cancellationToken));
        db.AssessmentPools.RemoveRange(await db.AssessmentPools.Where(item => item.AssessmentId == assessmentId && item.Version == draft).ToListAsync(cancellationToken));
        assessment.DraftVersion = null;
        assessment.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    // ---------- rubrics ----------
    private static async Task<RubricResponse> ToRubricAsync(LmsDbContext db, Rubric rubric, CancellationToken cancellationToken)
        => new(rubric.Id, rubric.CourseId, rubric.Name, rubric.TotalPoints, RubricRules.Read(rubric.CriteriaJson), await UsesOfAsync(db, rubric.Id, cancellationToken));

    /// <summary>How many questions and assignments score with this rubric.</summary>
    private static async Task<int> UsesOfAsync(LmsDbContext db, Guid rubricId, CancellationToken cancellationToken)
        => await db.Questions.CountAsync(item => item.RubricId == rubricId, cancellationToken) + await db.Assignments.CountAsync(item => item.RubricId == rubricId, cancellationToken);

    private static async Task<IResult> ListRubricsAsync(LmsDbContext db, Guid courseId, CancellationToken cancellationToken)
    {
        var rubrics = await db.Rubrics.AsNoTracking().Where(item => item.CourseId == courseId).OrderBy(item => item.Name).ToListAsync(cancellationToken);
        var result = new List<RubricResponse>();
        foreach (var rubric in rubrics) result.Add(await ToRubricAsync(db, rubric, cancellationToken));
        return Results.Ok(result);
    }

    private static async Task<IResult> CreateRubricAsync(HttpContext httpContext, LmsDbContext db, ITenantContext tenantContext, Guid courseId, RubricRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 200) return Problem("name", "Enter a name of up to 200 characters.");
        if (RubricRules.Clean(request.Criteria, out var criteria) is { } problem) return Problem("criteria", problem);
        if (!await db.Courses.AnyAsync(item => item.Id == courseId && item.Status != CourseStatus.Archived, cancellationToken)) return Results.NotFound();
        if (await db.Rubrics.AnyAsync(item => item.CourseId == courseId && item.Name == name, cancellationToken)) return Results.Conflict(new { message = "This course already has a rubric with that name." });
        var now = DateTimeOffset.UtcNow;
        var rubric = new Rubric { Id = Guid.NewGuid(), TenantId = tenantId, CourseId = courseId, Name = name, CriteriaJson = RubricRules.Write(criteria), TotalPoints = RubricRules.Total(criteria), CreatedByUserId = userId, CreatedAtUtc = now, UpdatedAtUtc = now };
        db.Rubrics.Add(rubric);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/rubrics/{rubric.Id:D}", await ToRubricAsync(db, rubric, cancellationToken));
    }

    private static async Task<IResult> UpdateRubricAsync(LmsDbContext db, Guid rubricId, RubricRequest request, CancellationToken cancellationToken)
    {
        var rubric = await db.Rubrics.SingleOrDefaultAsync(item => item.Id == rubricId, cancellationToken);
        if (rubric is null) return Results.NotFound();
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 200) return Problem("name", "Enter a name of up to 200 characters.");
        if (RubricRules.Clean(request.Criteria, out var criteria) is { } problem) return Problem("criteria", problem);
        // A rubric that questions already use is never changed under them; the author copies it instead.
        if (await UsesOfAsync(db, rubricId, cancellationToken) > 0) return Results.Conflict(new { message = "Questions or assignments already use this rubric, so it cannot be changed. Create a new rubric instead." });
        if (await db.Rubrics.AnyAsync(item => item.CourseId == rubric.CourseId && item.Name == name && item.Id != rubricId, cancellationToken)) return Results.Conflict(new { message = "This course already has a rubric with that name." });
        rubric.Name = name; rubric.CriteriaJson = RubricRules.Write(criteria); rubric.TotalPoints = RubricRules.Total(criteria); rubric.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(await ToRubricAsync(db, rubric, cancellationToken));
    }

    private static async Task<IResult> DeleteRubricAsync(LmsDbContext db, Guid rubricId, CancellationToken cancellationToken)
    {
        var rubric = await db.Rubrics.SingleOrDefaultAsync(item => item.Id == rubricId, cancellationToken);
        if (rubric is null) return Results.NotFound();
        if (await UsesOfAsync(db, rubricId, cancellationToken) > 0) return Results.Conflict(new { message = "Questions or assignments use this rubric, so it cannot be deleted." });
        db.Rubrics.Remove(rubric);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    // ---------- accommodations ----------
    private static async Task<IResult> ListAccommodationsAsync(LmsDbContext db, Guid courseId, CancellationToken cancellationToken)
    {
        var rows = await db.LearnerAccommodations.AsNoTracking().Where(item => item.CourseId == courseId).ToListAsync(cancellationToken);
        var ids = rows.Select(item => item.LearnerUserId).ToList();
        var names = await db.Users.AsNoTracking().Where(item => ids.Contains(item.Id)).ToDictionaryAsync(item => item.Id, item => item.DisplayName, cancellationToken);
        return Results.Ok(rows.Select(item => new AccommodationResponse(item.LearnerUserId, names.GetValueOrDefault(item.LearnerUserId, "Unknown"), item.ExtraTimePercent, item.ExtraAttempts, item.Note, item.UpdatedAtUtc))
            .OrderBy(item => item.LearnerName).ToList());
    }

    private static async Task<IResult> SetAccommodationAsync(HttpContext httpContext, LmsDbContext db, ITenantContext tenantContext, SecurityAuditService audit, Guid courseId, Guid learnerUserId, AccommodationRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        if (request.ExtraTimePercent is < 0 or > 200) return Problem("extraTimePercent", "Extra time is from 0% to 200%.");
        if (request.ExtraAttempts is < 0 or > 10) return Problem("extraAttempts", "Extra attempts are from 0 to 10.");
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is { Length: > 1000 }) return Problem("note", "The note is too long.");
        if (!await db.Enrollments.AnyAsync(item => item.CourseId == courseId && item.LearnerUserId == learnerUserId && item.Status != EnrollmentStatus.Withdrawn, cancellationToken))
            return Results.NotFound(new { message = "That person is not enrolled in this course." });

        var existing = await db.LearnerAccommodations.SingleOrDefaultAsync(item => item.CourseId == courseId && item.LearnerUserId == learnerUserId, cancellationToken);
        if (request.ExtraTimePercent == 0 && request.ExtraAttempts == 0)
        {
            if (existing is not null) { db.LearnerAccommodations.Remove(existing); audit.Add(db, httpContext, tenantId, "assessment.accommodation.removed", "course", courseId, new { learnerUserId }); await db.SaveChangesAsync(cancellationToken); }
            return Results.NoContent();
        }
        if (existing is null) db.LearnerAccommodations.Add(existing = new LearnerAccommodation { Id = Guid.NewGuid(), TenantId = tenantId, CourseId = courseId, LearnerUserId = learnerUserId });
        existing.ExtraTimePercent = request.ExtraTimePercent; existing.ExtraAttempts = request.ExtraAttempts; existing.Note = note;
        existing.UpdatedByUserId = userId; existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
        audit.Add(db, httpContext, tenantId, "assessment.accommodation.set", "course", courseId, new { learnerUserId, request.ExtraTimePercent, request.ExtraAttempts });
        await db.SaveChangesAsync(cancellationToken);
        var name = await db.Users.AsNoTracking().Where(item => item.Id == learnerUserId).Select(item => item.DisplayName).SingleOrDefaultAsync(cancellationToken) ?? "Unknown";
        return Results.Ok(new AccommodationResponse(learnerUserId, name, existing.ExtraTimePercent, existing.ExtraAttempts, existing.Note, existing.UpdatedAtUtc));
    }

    private static async Task<IResult> RemoveAccommodationAsync(HttpContext httpContext, LmsDbContext db, ITenantContext tenantContext, SecurityAuditService audit, Guid courseId, Guid learnerUserId, CancellationToken cancellationToken)
    {
        var existing = await db.LearnerAccommodations.SingleOrDefaultAsync(item => item.CourseId == courseId && item.LearnerUserId == learnerUserId, cancellationToken);
        if (existing is null) return Results.NotFound();
        db.LearnerAccommodations.Remove(existing);
        if (tenantContext.TenantId is Guid tenantId) audit.Add(db, httpContext, tenantId, "assessment.accommodation.removed", "course", courseId, new { learnerUserId });
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    // ---------- file answers ----------
    /// <summary>The attempt, question and answer a learner may attach a file to now, or the reason they may not.</summary>
    private static async Task<(AssessmentAttempt? Attempt, AssessmentAnswer? Answer, IResult? Error)> OpenFileAnswerAsync(HttpContext httpContext, LmsDbContext db, Guid attemptId, Guid questionId, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid learnerUserId) return (null, null, Results.Unauthorized());
        var attempt = await db.AssessmentAttempts.SingleOrDefaultAsync(item => item.Id == attemptId && item.LearnerUserId == learnerUserId, cancellationToken);
        if (attempt is null) return (null, null, Results.NotFound());
        if (attempt.Status != AttemptStatus.InProgress) return (null, null, Results.Conflict(new { message = "Answers cannot be changed after the attempt is submitted." }));
        var assessment = await db.Assessments.AsNoTracking().SingleOrDefaultAsync(item => item.Id == attempt.AssessmentId, cancellationToken);
        if (assessment is null) return (null, null, Results.NotFound());
        if (IsExpired(attempt, assessment)) return (null, null, Results.Conflict(new { message = "The assessment time limit has expired. Submit the attempt to record the answers already saved." }));
        var question = await db.Questions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == questionId, cancellationToken);
        if (question is null || !(await PlanAsync(db, attempt, cancellationToken)).Any(entry => entry.Q == questionId)) return (null, null, Results.NotFound());
        if (question.Type != QuestionType.FileUpload) return (null, null, Results.BadRequest(new { message = "This question does not take a file." }));
        var answer = await db.AssessmentAnswers.SingleOrDefaultAsync(item => item.AttemptId == attemptId && item.QuestionId == questionId, cancellationToken);
        if (answer is null)
        {
            var now = DateTimeOffset.UtcNow;
            answer = new AssessmentAnswer { Id = Guid.NewGuid(), TenantId = attempt.TenantId, AttemptId = attemptId, QuestionId = questionId, AnswerJson = JsonSerializer.Serialize(new StoredAnswer([], null, null)), AnsweredAtUtc = now, UpdatedAtUtc = now };
            db.AssessmentAnswers.Add(answer);
        }
        return (attempt, answer, null);
    }

    private static async Task<IResult> UploadAnswerFileAsync(HttpRequest request, LmsDbContext db, IContentAssetStorage storage, Guid attemptId, Guid questionId, CancellationToken cancellationToken)
    {
        var (attempt, answer, error) = await OpenFileAnswerAsync(request.HttpContext, db, attemptId, questionId, cancellationToken);
        if (error is not null) return error;
        var (file, _) = await AttachmentRules.ReadAsync(request, cancellationToken);
        if (AttachmentRules.Problem(file) is { } rejected) return Results.BadRequest(new { message = rejected });
        file = file!;

        var stored = await storage.SaveAsync(attempt!.TenantId, attempt.CourseId, file, cancellationToken);
        var current = DeserializeAnswer(answer!.AnswerJson);
        if (current.File is { } previous) await storage.DeleteAsync(previous.StorageKey, cancellationToken);
        answer.AnswerJson = JsonSerializer.Serialize(current with { File = new AnswerFile(stored.StorageKey, stored.OriginalFileName, stored.ContentType, stored.SizeBytes) });
        answer.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new AnswerFileView(stored.OriginalFileName, stored.SizeBytes));
    }

    private static async Task<IResult> RemoveAnswerFileAsync(HttpContext httpContext, LmsDbContext db, IContentAssetStorage storage, Guid attemptId, Guid questionId, CancellationToken cancellationToken)
    {
        var (_, answer, error) = await OpenFileAnswerAsync(httpContext, db, attemptId, questionId, cancellationToken);
        if (error is not null) return error;
        var current = DeserializeAnswer(answer!.AnswerJson);
        if (current.File is not { } file) return Results.NoContent();
        await storage.DeleteAsync(file.StorageKey, cancellationToken);
        answer.AnswerJson = JsonSerializer.Serialize(current with { File = null });
        answer.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> DownloadAnswerFileAsync(HttpContext httpContext, LmsDbContext db, IContentAssetStorage storage, Guid attemptId, Guid questionId, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var attempt = await db.AssessmentAttempts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == attemptId, cancellationToken);
        // The learner may fetch their own file; graders may fetch any. Everyone else is told it does not exist.
        if (attempt is null || (attempt.LearnerUserId != userId && !HasPermission(httpContext, Domain.Identity.LmsPermissions.GradeManage))) return Results.NotFound();
        var answer = await db.AssessmentAnswers.AsNoTracking().SingleOrDefaultAsync(item => item.AttemptId == attemptId && item.QuestionId == questionId, cancellationToken);
        if (answer is null || DeserializeAnswer(answer.AnswerJson).File is not { } file) return Results.NotFound();
        var stream = await storage.OpenReadAsync(file.StorageKey, cancellationToken);
        return stream is null ? Results.NotFound() : Results.File(stream, "application/octet-stream", file.FileName);
    }
}

public sealed record SetPoolRequest(int DrawCount);
public sealed record RubricRequest(string? Name, List<RubricCriterionInput>? Criteria);
public sealed record RubricResponse(Guid Id, Guid CourseId, string Name, int TotalPoints, List<RubricCriterion> Criteria, int UsedByQuestions);
public sealed record AccommodationRequest(int ExtraTimePercent, int ExtraAttempts, string? Note = null);
public sealed record AccommodationResponse(Guid LearnerUserId, string LearnerName, int ExtraTimePercent, int ExtraAttempts, string? Note, DateTimeOffset UpdatedAtUtc);
