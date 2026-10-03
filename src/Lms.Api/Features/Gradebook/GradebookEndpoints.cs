using System.Security.Claims;
using System.Text;
using Lms.Api.Domain.Assessments;
using Lms.Api.Domain.Assignments;
using Lms.Api.Domain.Courses;
using Lms.Api.Domain.Gradebook;
using Lms.Api.Domain.Learning;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Gradebook;

/// <summary>
/// Combines published assignments and assessments into one grade view.
/// Without categories the overall percentage is points earned / points possible across graded items.
/// With categories it is the weighted mean of each category's percentage, counting only categories that already have
/// graded work (so a learner is not penalised for a category that has not started). Items outside every category are not counted.
/// An assessment counts its best graded attempt; an assignment counts its final (post-late-penalty) points.
/// </summary>
public static class GradebookEndpoints
{
    private const int MaxCategories = 10;

    public static void MapGradebookEndpoints(this WebApplication app)
    {
        var tenant = app.MapGroup("/api/v1/tenant/gradebook").RequireAuthorization("tenant.authenticated");
        tenant.AddEndpointFilter(async (context, next) =>
        {
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (!tenantContext.IsResolved)
                return Results.BadRequest(new { message = "A tenant is required. Use the X-Tenant-Slug header or a tenant subdomain." });
            return await next(context);
        });

        tenant.MapGet("/me", MyGradesAsync).RequireAuthorization("tenant.grade.read");
        tenant.MapGet("/courses/{courseId:guid}", CourseGradebookAsync).RequireAuthorization("tenant.grade.manage");
        tenant.MapGet("/courses/{courseId:guid}/export.csv", ExportCsvAsync).RequireAuthorization("tenant.grade.manage");

        tenant.MapGet("/courses/{courseId:guid}/settings", GetSettingsAsync).RequireAuthorization("tenant.grade.manage");
        tenant.MapPut("/courses/{courseId:guid}/settings", SaveSettingsAsync).RequireAuthorization("tenant.grade.manage");
        tenant.MapPut("/courses/{courseId:guid}/items/category", SetItemCategoryAsync).RequireAuthorization("tenant.grade.manage");

        tenant.MapGet("/scales", ListScalesAsync).RequireAuthorization("tenant.grade.read");
        tenant.MapPost("/scales", CreateScaleAsync).RequireAuthorization("tenant.grade.manage");
        tenant.MapPut("/scales/{scaleId:guid}", UpdateScaleAsync).RequireAuthorization("tenant.grade.manage");
        tenant.MapDelete("/scales/{scaleId:guid}", DeleteScaleAsync).RequireAuthorization("tenant.grade.manage");
    }

    // ---------- teacher: whole class ----------
    private static async Task<IResult> CourseGradebookAsync(Guid courseId, LmsDbContext db, CancellationToken cancellationToken)
    {
        var book = await BuildCourseBookAsync(db, courseId, cancellationToken);
        return book is null ? Results.NotFound() : Results.Ok(book);
    }

    private static async Task<IResult> ExportCsvAsync(Guid courseId, LmsDbContext db, CancellationToken cancellationToken)
    {
        var book = await BuildCourseBookAsync(db, courseId, cancellationToken);
        if (book is null) return Results.NotFound();
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(',', new[] { "Learner", "Email" }
            .Concat(book.Items.Select(item => $"{item.Title} (/{item.MaxPoints})"))
            .Concat(book.Categories.Select(category => $"{category.Name} ({category.WeightPercent:0.##}%)"))
            .Concat(["Points earned", "Points possible", "Overall %", "Grade", "Result"]).Select(Csv)));
        foreach (var learner in book.Learners)
        {
            var cells = learner.Cells.Select(cell => cell.Status switch
            {
                "Graded" => cell.Score?.ToString("0.##") ?? string.Empty,
                "Pending" => "Pending",
                _ => string.Empty
            });
            csv.AppendLine(string.Join(',', new[] { learner.Name, learner.Email }.Concat(cells)
                .Concat(learner.CategoryPercents.Select(percent => percent?.ToString("0.##") ?? string.Empty))
                .Concat([learner.EarnedPoints.ToString("0.##"), learner.PossiblePoints.ToString("0.##"), learner.OverallPercent?.ToString("0.##") ?? string.Empty,
                    learner.Letter ?? string.Empty, learner.Passed is null ? string.Empty : learner.Passed.Value ? "Pass" : "Fail"]).Select(Csv)));
        }
        return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", $"gradebook-{book.CourseCode}.csv");
    }

    private static async Task<CourseBook?> BuildCourseBookAsync(LmsDbContext db, Guid courseId, CancellationToken cancellationToken)
    {
        var course = await db.Courses.AsNoTracking().SingleOrDefaultAsync(item => item.Id == courseId, cancellationToken);
        if (course is null) return null;
        var context = await LoadContextAsync(db, courseId, cancellationToken);

        var learnerIds = await db.Enrollments.AsNoTracking()
            .Where(item => item.CourseId == courseId && (item.Status == EnrollmentStatus.Active || item.Status == EnrollmentStatus.Completed))
            .Select(item => item.LearnerUserId).Distinct().ToListAsync(cancellationToken);
        var users = await db.Users.AsNoTracking().Where(item => learnerIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, cancellationToken);

        var assignments = await db.Assignments.AsNoTracking().Where(item => item.CourseId == courseId && item.Status != AssignmentStatus.Draft).OrderBy(item => item.DueAtUtc).ThenBy(item => item.Title).ToListAsync(cancellationToken);
        var assessments = await db.Assessments.AsNoTracking().Where(item => item.CourseId == courseId && item.Status == AssessmentStatus.Published).OrderBy(item => item.Title).ToListAsync(cancellationToken);
        var submissions = await LoadSubmissionsAsync(db, assignments.Select(item => item.Id).ToList(), learnerIds, cancellationToken);
        var attempts = await LoadAttemptsAsync(db, assessments.Select(item => item.Id).ToList(), learnerIds, cancellationToken);

        // Column max for an assessment is the largest possible score seen across attempts.
        var assessmentMax = assessments.ToDictionary(item => item.Id, item => attempts.Where(a => a.AssessmentId == item.Id).Select(a => (decimal)a.PossiblePoints).DefaultIfEmpty(0).Max());

        var items = assignments.Select(item => new BookItem($"assignment:{item.Id}", "assignment", item.Title, item.MaxPoints, item.DueAtUtc, context.CategoryOf($"assignment:{item.Id}")))
            .Concat(assessments.Select(item => new BookItem($"assessment:{item.Id}", "assessment", item.Title, assessmentMax[item.Id], null, context.CategoryOf($"assessment:{item.Id}")))).ToList();

        var learners = new List<BookLearner>();
        foreach (var learnerId in learnerIds.Where(users.ContainsKey).OrderBy(id => users[id].DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var cells = new List<BookCell>();
            foreach (var assignment in assignments)
                cells.Add(AssignmentCell(assignment, submissions.FirstOrDefault(s => s.AssignmentId == assignment.Id && s.LearnerUserId == learnerId)));
            foreach (var assessment in assessments)
                cells.Add(AssessmentCell(assessment, assessmentMax[assessment.Id], attempts.Where(a => a.AssessmentId == assessment.Id && a.LearnerUserId == learnerId).ToList()));
            var summary = Summarize(cells, context);
            learners.Add(new BookLearner(learnerId, users[learnerId].DisplayName, users[learnerId].Email, cells, summary.Earned, summary.Possible, summary.Overall,
                summary.CategoryPercents, summary.Letter, summary.GradePoints, summary.Passed));
        }

        var averages = items.Select((item, index) =>
        {
            var graded = learners.Select(l => l.Cells[index]).Where(c => c.Percent is not null).Select(c => c.Percent!.Value).ToList();
            return graded.Count == 0 ? (decimal?)null : Math.Round(graded.Average(), 2);
        }).ToList();
        var classOverall = learners.Where(l => l.OverallPercent is not null).Select(l => l.OverallPercent!.Value).ToList();
        return new CourseBook(course.Id, course.Code, course.Title, items, averages, learners, classOverall.Count == 0 ? null : Math.Round(classOverall.Average(), 2),
            context.Categories.Select(c => new BookCategory(c.Id, c.Name, c.WeightPercent)).ToList(), context.Categories.Count > 0, context.ScaleName, context.PassPercent);
    }

    // ---------- learner: own grades ----------
    private static async Task<IResult> MyGradesAsync(HttpContext httpContext, LmsDbContext db, CancellationToken cancellationToken)
    {
        if (GetUserId(httpContext) is not Guid userId) return Results.Unauthorized();
        var courseIds = await db.Enrollments.AsNoTracking()
            .Where(item => item.LearnerUserId == userId && (item.Status == EnrollmentStatus.Active || item.Status == EnrollmentStatus.Completed))
            .Select(item => item.CourseId).Distinct().ToListAsync(cancellationToken);
        var courses = await db.Courses.AsNoTracking().Where(item => courseIds.Contains(item.Id) && item.Status != CourseStatus.Draft).OrderBy(item => item.Title).ToListAsync(cancellationToken);
        var learner = new List<Guid> { userId };

        var result = new List<MyCourseGrades>();
        foreach (var course in courses)
        {
            var context = await LoadContextAsync(db, course.Id, cancellationToken);
            var assignments = await db.Assignments.AsNoTracking().Where(item => item.CourseId == course.Id && item.Status != AssignmentStatus.Draft).OrderBy(item => item.DueAtUtc).ToListAsync(cancellationToken);
            var assessments = await db.Assessments.AsNoTracking().Where(item => item.CourseId == course.Id && item.Status == AssessmentStatus.Published).OrderBy(item => item.Title).ToListAsync(cancellationToken);
            var submissions = await LoadSubmissionsAsync(db, assignments.Select(item => item.Id).ToList(), learner, cancellationToken);
            var attempts = await LoadAttemptsAsync(db, assessments.Select(item => item.Id).ToList(), learner, cancellationToken);

            var rows = new List<MyGradeRow>();
            var cells = new List<BookCell>();
            foreach (var assignment in assignments)
            {
                var submission = submissions.FirstOrDefault(item => item.AssignmentId == assignment.Id);
                var cell = AssignmentCell(assignment, submission);
                cells.Add(cell);
                rows.Add(new MyGradeRow("assignment", assignment.Title, cell.Status, cell.Score, cell.MaxPoints, cell.Percent, cell.IsLate, submission?.Status == SubmissionStatus.Graded ? submission.Feedback : null,
                    context.CategoryName(cell.ItemId)));
            }
            foreach (var assessment in assessments)
            {
                var mine = attempts.Where(item => item.AssessmentId == assessment.Id).ToList();
                var max = mine.Select(item => (decimal)item.PossiblePoints).DefaultIfEmpty(0).Max();
                var cell = AssessmentCell(assessment, max, mine);
                cells.Add(cell);
                var best = mine.Where(item => item.Status == AttemptStatus.Graded).OrderByDescending(item => item.Percentage).FirstOrDefault();
                rows.Add(new MyGradeRow("assessment", assessment.Title, cell.Status, cell.Score, cell.MaxPoints, cell.Percent, false, best?.TeacherFeedback, context.CategoryName(cell.ItemId)));
            }
            var summary = Summarize(cells, context);
            var breakdown = context.Categories.Select((category, index) => new MyCategoryResult(category.Name, category.WeightPercent, summary.CategoryPercents[index])).ToList();
            result.Add(new MyCourseGrades(course.Id, course.Code, course.Title, rows, summary.Earned, summary.Possible, summary.Overall, summary.Letter, summary.Passed, context.Categories.Count > 0, breakdown, context.ScaleName));
        }
        return Results.Ok(result);
    }

    // ---------- settings ----------
    private static async Task<IResult> GetSettingsAsync(Guid courseId, LmsDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Courses.AnyAsync(item => item.Id == courseId, cancellationToken)) return Results.NotFound();
        return Results.Ok(await BuildSettingsAsync(db, courseId, cancellationToken));
    }

    private static async Task<IResult> SaveSettingsAsync(Guid courseId, ITenantContext tenantContext, LmsDbContext db, SaveSettingsRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId) return Results.Unauthorized();
        if (!await db.Courses.AnyAsync(item => item.Id == courseId, cancellationToken)) return Results.NotFound();
        if (request.PassPercent is < 0 or > 100) return Problem("The pass mark must be between 0 and 100.");
        if (request.GradeScaleId is Guid scaleId && !await db.GradeScales.AnyAsync(item => item.Id == scaleId, cancellationToken)) return Problem("The chosen grade scale does not exist.");

        var requested = request.Categories ?? [];
        var error = ValidateCategories(requested);
        if (error is not null) return Problem(error);

        var existing = await db.GradeCategories.Where(item => item.CourseId == courseId).ToListAsync(cancellationToken);
        if (requested.Any(item => item.Id is Guid id && existing.All(e => e.Id != id))) return Problem("One of the categories does not belong to this course.");

        // Update the ones that are kept, add the new ones, remove the rest (and forget which items were in them).
        var keptIds = requested.Where(item => item.Id is not null).Select(item => item.Id!.Value).ToHashSet();
        var removed = existing.Where(item => !keptIds.Contains(item.Id)).ToList();
        if (removed.Count > 0)
        {
            var removedIds = removed.Select(item => item.Id).ToList();
            db.GradeItemCategories.RemoveRange(await db.GradeItemCategories.Where(item => removedIds.Contains(item.CategoryId)).ToListAsync(cancellationToken));
            db.GradeCategories.RemoveRange(removed);
        }
        for (var i = 0; i < requested.Count; i++)
        {
            var dto = requested[i];
            var entity = dto.Id is Guid id ? existing.Single(e => e.Id == id) : null;
            if (entity is null)
            {
                entity = new GradeCategory { Id = Guid.NewGuid(), TenantId = tenantId, CourseId = courseId };
                db.GradeCategories.Add(entity);
            }
            entity.Name = dto.Name!.Trim(); entity.WeightPercent = dto.WeightPercent; entity.DisplayOrder = i + 1;
        }

        var settings = await db.CourseGradingSettings.SingleOrDefaultAsync(item => item.CourseId == courseId, cancellationToken);
        if (settings is null) { settings = new CourseGradingSettings { Id = Guid.NewGuid(), TenantId = tenantId, CourseId = courseId }; db.CourseGradingSettings.Add(settings); }
        settings.GradeScaleId = request.GradeScaleId; settings.PassPercent = request.PassPercent; settings.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(await BuildSettingsAsync(db, courseId, cancellationToken));
    }

    private static string? ValidateCategories(List<CategoryDto> categories)
    {
        if (categories.Count == 0) return null; // no categories: graded by total points
        if (categories.Count > MaxCategories) return $"A course can have at most {MaxCategories} categories.";
        if (categories.Any(item => string.IsNullOrWhiteSpace(item.Name) || item.Name.Trim().Length > 60)) return "Each category needs a name of 1 to 60 characters.";
        if (categories.Select(item => item.Name!.Trim().ToLowerInvariant()).Distinct().Count() != categories.Count) return "Category names must be different from each other.";
        if (categories.Any(item => item.WeightPercent <= 0 || item.WeightPercent > 100)) return "Each weight must be more than 0 and at most 100.";
        if (Math.Abs(categories.Sum(item => item.WeightPercent) - 100m) > 0.01m) return $"The weights must add up to 100% (they add up to {categories.Sum(item => item.WeightPercent):0.##}%).";
        return null;
    }

    private static async Task<IResult> SetItemCategoryAsync(Guid courseId, ITenantContext tenantContext, LmsDbContext db, SetItemCategoryRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId) return Results.Unauthorized();
        var kind = request.ItemKind?.Trim().ToLowerInvariant();
        if (kind is not ("assignment" or "assessment")) return Problem("The item kind must be assignment or assessment.");
        var belongs = kind == "assignment"
            ? await db.Assignments.AnyAsync(item => item.Id == request.ItemId && item.CourseId == courseId, cancellationToken)
            : await db.Assessments.AnyAsync(item => item.Id == request.ItemId && item.CourseId == courseId, cancellationToken);
        if (!belongs) return Results.NotFound(new { message = "That item is not part of this course." });
        if (request.CategoryId is Guid categoryId && !await db.GradeCategories.AnyAsync(item => item.Id == categoryId && item.CourseId == courseId, cancellationToken))
            return Problem("That category does not belong to this course.");

        var mapping = await db.GradeItemCategories.SingleOrDefaultAsync(item => item.ItemKind == kind && item.ItemId == request.ItemId, cancellationToken);
        if (request.CategoryId is null) { if (mapping is not null) db.GradeItemCategories.Remove(mapping); }
        else if (mapping is null) db.GradeItemCategories.Add(new GradeItemCategory { Id = Guid.NewGuid(), TenantId = tenantId, CourseId = courseId, ItemKind = kind, ItemId = request.ItemId, CategoryId = request.CategoryId.Value });
        else mapping.CategoryId = request.CategoryId.Value;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<SettingsResponse> BuildSettingsAsync(LmsDbContext db, Guid courseId, CancellationToken cancellationToken)
    {
        var settings = await db.CourseGradingSettings.AsNoTracking().SingleOrDefaultAsync(item => item.CourseId == courseId, cancellationToken);
        var categories = await db.GradeCategories.AsNoTracking().Where(item => item.CourseId == courseId).OrderBy(item => item.DisplayOrder).ToListAsync(cancellationToken);
        var mappings = await db.GradeItemCategories.AsNoTracking().Where(item => item.CourseId == courseId).ToListAsync(cancellationToken);
        var assignments = await db.Assignments.AsNoTracking().Where(item => item.CourseId == courseId && item.Status != AssignmentStatus.Draft).OrderBy(item => item.Title).ToListAsync(cancellationToken);
        var assessments = await db.Assessments.AsNoTracking().Where(item => item.CourseId == courseId && item.Status == AssessmentStatus.Published).OrderBy(item => item.Title).ToListAsync(cancellationToken);
        Guid? CategoryOf(string kind, Guid id) => mappings.FirstOrDefault(m => m.ItemKind == kind && m.ItemId == id)?.CategoryId;
        var items = assignments.Select(item => new SettingsItem(item.Id, "assignment", item.Title, CategoryOf("assignment", item.Id)))
            .Concat(assessments.Select(item => new SettingsItem(item.Id, "assessment", item.Title, CategoryOf("assessment", item.Id)))).ToList();
        return new SettingsResponse(settings?.GradeScaleId, settings?.PassPercent ?? 50, categories.Select(c => new CategoryDto(c.Id, c.Name, c.WeightPercent)).ToList(), items);
    }

    // ---------- scales ----------
    private static async Task<IResult> ListScalesAsync(LmsDbContext db, CancellationToken cancellationToken)
    {
        var scales = await db.GradeScales.AsNoTracking().OrderBy(item => item.Name).ToListAsync(cancellationToken);
        var list = scales.Select(ToScale).ToList();
        // The built-in scale applies whenever no organization default is set; show it so people can see what they would get.
        if (scales.All(item => !item.IsDefault)) list.Insert(0, new ScaleResponse(null, "Standard (built-in)", true, true, GradeScales.BuiltIn));
        else list.Add(new ScaleResponse(null, "Standard (built-in)", false, true, GradeScales.BuiltIn));
        return Results.Ok(list);
    }

    private static async Task<IResult> CreateScaleAsync(ITenantContext tenantContext, LmsDbContext db, SaveScaleRequest request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId) return Results.Unauthorized();
        var error = await ValidateScaleAsync(db, null, request, cancellationToken);
        if (error is not null) return Problem(error);
        var now = DateTimeOffset.UtcNow;
        var scale = new GradeScale { Id = Guid.NewGuid(), TenantId = tenantId, Name = request.Name!.Trim(), IsDefault = request.IsDefault, CreatedAtUtc = now, UpdatedAtUtc = now, Bands = Normalise(request.Bands!) };
        if (request.IsDefault) await ClearDefaultAsync(db, scale.Id, cancellationToken);
        db.GradeScales.Add(scale);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/tenant/gradebook/scales/{scale.Id}", ToScale(scale));
    }

    private static async Task<IResult> UpdateScaleAsync(Guid scaleId, LmsDbContext db, SaveScaleRequest request, CancellationToken cancellationToken)
    {
        var scale = await db.GradeScales.SingleOrDefaultAsync(item => item.Id == scaleId, cancellationToken);
        if (scale is null) return Results.NotFound();
        var error = await ValidateScaleAsync(db, scaleId, request, cancellationToken);
        if (error is not null) return Problem(error);
        scale.Name = request.Name!.Trim(); scale.IsDefault = request.IsDefault; scale.Bands = Normalise(request.Bands!); scale.UpdatedAtUtc = DateTimeOffset.UtcNow;
        if (request.IsDefault) await ClearDefaultAsync(db, scaleId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToScale(scale));
    }

    private static async Task<IResult> DeleteScaleAsync(Guid scaleId, LmsDbContext db, CancellationToken cancellationToken)
    {
        var scale = await db.GradeScales.SingleOrDefaultAsync(item => item.Id == scaleId, cancellationToken);
        if (scale is null) return Results.NotFound();
        if (await db.CourseGradingSettings.AnyAsync(item => item.GradeScaleId == scaleId, cancellationToken)) return Results.Conflict(new { message = "A course uses this scale. Choose another scale for that course first." });
        db.GradeScales.Remove(scale);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<string?> ValidateScaleAsync(LmsDbContext db, Guid? scaleId, SaveScaleRequest request, CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 100) return "The scale needs a name of 1 to 100 characters.";
        var problems = GradeScales.Validate(request.Bands);
        if (problems.Count > 0) return problems[0];
        var lower = name.ToLowerInvariant();
        if (await db.GradeScales.AnyAsync(item => item.Name.ToLower() == lower && item.Id != scaleId, cancellationToken)) return "A scale with this name already exists.";
        return null;
    }

    private static async Task ClearDefaultAsync(LmsDbContext db, Guid keepId, CancellationToken cancellationToken)
    {
        foreach (var other in await db.GradeScales.Where(item => item.IsDefault && item.Id != keepId).ToListAsync(cancellationToken)) other.IsDefault = false;
    }

    private static List<GradeBand> Normalise(List<GradeBand> bands) => bands.Select(band => new GradeBand(band.MinPercent, band.Label.Trim(), band.Points)).OrderByDescending(band => band.MinPercent).ToList();
    private static ScaleResponse ToScale(GradeScale scale) => new(scale.Id, scale.Name, scale.IsDefault, false, scale.Bands.OrderByDescending(band => band.MinPercent).ToList());

    // ---------- shared calculations ----------
    private sealed record GradingContext(IReadOnlyList<GradeCategory> Categories, Dictionary<string, Guid> ItemCategories, IReadOnlyList<GradeBand> Bands, string ScaleName, decimal PassPercent)
    {
        public Guid? CategoryOf(string itemId) => ItemCategories.TryGetValue(itemId, out var id) ? id : null;
        public string? CategoryName(string itemId) => CategoryOf(itemId) is Guid id ? Categories.FirstOrDefault(c => c.Id == id)?.Name : null;
    }

    private static async Task<GradingContext> LoadContextAsync(LmsDbContext db, Guid courseId, CancellationToken cancellationToken)
    {
        var settings = await db.CourseGradingSettings.AsNoTracking().SingleOrDefaultAsync(item => item.CourseId == courseId, cancellationToken);
        var categories = await db.GradeCategories.AsNoTracking().Where(item => item.CourseId == courseId).OrderBy(item => item.DisplayOrder).ToListAsync(cancellationToken);
        var mappings = (await db.GradeItemCategories.AsNoTracking().Where(item => item.CourseId == courseId).ToListAsync(cancellationToken))
            .ToDictionary(item => $"{item.ItemKind}:{item.ItemId}", item => item.CategoryId);

        // The course's own scale, else the organization default, else the built-in one.
        GradeScale? scale = settings?.GradeScaleId is Guid chosen ? await db.GradeScales.AsNoTracking().SingleOrDefaultAsync(item => item.Id == chosen, cancellationToken) : null;
        scale ??= await db.GradeScales.AsNoTracking().FirstOrDefaultAsync(item => item.IsDefault, cancellationToken);
        return new GradingContext(categories, mappings, scale?.Bands ?? GradeScales.BuiltIn, scale?.Name ?? "Standard (built-in)", settings?.PassPercent ?? 50);
    }

    private sealed record Summary(decimal Earned, decimal Possible, decimal? Overall, IReadOnlyList<decimal?> CategoryPercents, string? Letter, decimal? GradePoints, bool? Passed);

    private static Summary Summarize(IReadOnlyList<BookCell> cells, GradingContext context)
    {
        var graded = cells.Where(cell => cell.Status == "Graded" && cell.Score is not null).ToList();
        var earned = graded.Sum(cell => cell.Score!.Value);
        var possible = graded.Sum(cell => cell.MaxPoints);

        var categoryPercents = new List<decimal?>();
        decimal? overall;
        if (context.Categories.Count == 0)
            overall = possible <= 0 ? null : Math.Round(earned * 100m / possible, 2);
        else
        {
            decimal weighted = 0, weights = 0;
            foreach (var category in context.Categories)
            {
                var inCategory = graded.Where(cell => context.CategoryOf(cell.ItemId) == category.Id).ToList();
                var max = inCategory.Sum(cell => cell.MaxPoints);
                decimal? percent = max <= 0 ? null : Math.Round(inCategory.Sum(cell => cell.Score!.Value) * 100m / max, 2);
                categoryPercents.Add(percent);
                if (percent is not null) { weighted += category.WeightPercent * percent.Value; weights += category.WeightPercent; }
            }
            overall = weights <= 0 ? null : Math.Round(weighted / weights, 2);
        }

        var band = GradeScales.Lookup(context.Bands, overall);
        return new Summary(earned, possible, overall, categoryPercents, band?.Label, band?.Points, overall is null ? null : overall >= context.PassPercent);
    }

    private static async Task<List<AssignmentSubmission>> LoadSubmissionsAsync(LmsDbContext db, List<Guid> assignmentIds, List<Guid> learnerIds, CancellationToken cancellationToken)
        => await db.AssignmentSubmissions.AsNoTracking().Where(item => assignmentIds.Contains(item.AssignmentId) && learnerIds.Contains(item.LearnerUserId)).ToListAsync(cancellationToken);

    private static async Task<List<AssessmentAttempt>> LoadAttemptsAsync(LmsDbContext db, List<Guid> assessmentIds, List<Guid> learnerIds, CancellationToken cancellationToken)
        => await db.AssessmentAttempts.AsNoTracking().Where(item => assessmentIds.Contains(item.AssessmentId) && learnerIds.Contains(item.LearnerUserId)).ToListAsync(cancellationToken);

    private static BookCell AssignmentCell(Assignment assignment, AssignmentSubmission? submission)
    {
        var id = $"assignment:{assignment.Id}";
        var max = (decimal)assignment.MaxPoints;
        if (submission is null) return new BookCell(id, "Missing", null, max, null, false);
        if (submission.Status != SubmissionStatus.Graded || submission.FinalPoints is null) return new BookCell(id, "Pending", null, max, null, submission.IsLate);
        return new BookCell(id, "Graded", submission.FinalPoints, max, Percent(submission.FinalPoints.Value, max), submission.IsLate);
    }

    private static BookCell AssessmentCell(Assessment assessment, decimal max, List<AssessmentAttempt> attempts)
    {
        var id = $"assessment:{assessment.Id}";
        var best = attempts.Where(item => item.Status == AttemptStatus.Graded).OrderByDescending(item => item.Percentage ?? 0).FirstOrDefault();
        if (best is not null) return new BookCell(id, "Graded", best.ScorePoints, max, Percent(best.ScorePoints, best.PossiblePoints), false);
        return new BookCell(id, attempts.Any(item => item.Status == AttemptStatus.Submitted) ? "Pending" : "Missing", null, max, null, false);
    }

    private static decimal? Percent(decimal score, decimal max) => max <= 0 ? null : Math.Round(score * 100m / max, 2);

    /// <summary>Quotes a CSV field and neutralises spreadsheet formulas (=, +, -, @) in learner-controlled text.</summary>
    private static string Csv(string? value)
    {
        value ??= string.Empty;
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0]) && !decimal.TryParse(value, out _)) value = "'" + value;
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static IResult Problem(string message) => Results.BadRequest(new { message });

    private static Guid? GetUserId(HttpContext context)
        => Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub"), out var id) ? id : null;
}

public sealed record BookItem(string Id, string Kind, string Title, decimal MaxPoints, DateTimeOffset? DueAtUtc, Guid? CategoryId);
public sealed record BookCategory(Guid Id, string Name, decimal WeightPercent);
/// <summary>Status is Graded, Pending (submitted, not graded yet) or Missing (nothing submitted).</summary>
public sealed record BookCell(string ItemId, string Status, decimal? Score, decimal MaxPoints, decimal? Percent, bool IsLate);
/// <summary>CategoryPercents lines up with the book's Categories. OverallPercent is weighted when the course has categories.</summary>
public sealed record BookLearner(Guid UserId, string Name, string Email, IReadOnlyList<BookCell> Cells, decimal EarnedPoints, decimal PossiblePoints, decimal? OverallPercent,
    IReadOnlyList<decimal?> CategoryPercents, string? Letter, decimal? GradePoints, bool? Passed);
public sealed record CourseBook(Guid CourseId, string CourseCode, string CourseTitle, IReadOnlyList<BookItem> Items, IReadOnlyList<decimal?> ItemAverages, IReadOnlyList<BookLearner> Learners,
    decimal? ClassAverage, IReadOnlyList<BookCategory> Categories, bool Weighted, string ScaleName, decimal PassPercent);
public sealed record MyGradeRow(string Kind, string Title, string Status, decimal? Score, decimal MaxPoints, decimal? Percent, bool IsLate, string? Feedback, string? CategoryName);
public sealed record MyCategoryResult(string Name, decimal WeightPercent, decimal? Percent);
public sealed record MyCourseGrades(Guid CourseId, string CourseCode, string CourseTitle, IReadOnlyList<MyGradeRow> Rows, decimal EarnedPoints, decimal PossiblePoints, decimal? OverallPercent,
    string? Letter, bool? Passed, bool Weighted, IReadOnlyList<MyCategoryResult> Categories, string ScaleName);

public sealed record CategoryDto(Guid? Id, string? Name, decimal WeightPercent);
public sealed record SettingsItem(Guid ItemId, string Kind, string Title, Guid? CategoryId);
public sealed record SettingsResponse(Guid? GradeScaleId, decimal PassPercent, IReadOnlyList<CategoryDto> Categories, IReadOnlyList<SettingsItem> Items);
public sealed record SaveSettingsRequest(Guid? GradeScaleId, decimal PassPercent, List<CategoryDto>? Categories);
public sealed record SetItemCategoryRequest(string? ItemKind, Guid ItemId, Guid? CategoryId);
public sealed record ScaleResponse(Guid? Id, string Name, bool IsDefault, bool BuiltIn, IReadOnlyList<GradeBand> Bands);
public sealed record SaveScaleRequest(string? Name, bool IsDefault, List<GradeBand>? Bands);
