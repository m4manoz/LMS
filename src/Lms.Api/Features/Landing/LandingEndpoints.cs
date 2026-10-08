using System.Net.Mail;
using System.Security.Claims;
using Lms.Api.Domain.Courses;
using Lms.Api.Domain.Landing;
using Lms.Api.Domain.Learning;
using Lms.Api.Domain.Tenants;
using Lms.Api.Infrastructure.CourseAccess;
using Lms.Api.Infrastructure.Landing;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Features.Landing;

/// <summary>
/// An organization's public front page: its published courses, the words it chose for the page, and a way for visitors to apply for a course.
/// Visitors need no account; staff manage the page's content and decide on applications.
/// </summary>
public static class LandingEndpoints
{
    private const int MaxCourses = 100;

    public static void MapLandingEndpoints(this WebApplication app)
    {
        var pub = app.MapGroup("/api/v1/public").AllowAnonymous();
        pub.MapGet("/organizations/default", DefaultOrganizationAsync);
        pub.MapGet("/site", SiteAsync);
        pub.MapGet("/{slug}/landing", LandingAsync);
        pub.MapGet("/{slug}/courses/{courseId:guid}", CourseAsync);
        pub.MapPost("/{slug}/courses/{courseId:guid}/applications", ApplyAsync);

        // The platform operator gives an organization its own website address.
        var platform = app.MapGroup("/api/v1/platform").AllowAnonymous();
        platform.MapGet("/tenants/{slug}/domains", ListDomainsAsync);
        platform.MapPut("/tenants/{slug}/domains", AddDomainAsync);
        platform.MapDelete("/tenants/{slug}/domains/{host}", RemoveDomainAsync);

        var tenant = app.MapGroup("/api/v1/tenant").RequireAuthorization("tenant.authenticated");
        tenant.AddEndpointFilter(async (context, next) =>
            context.HttpContext.RequestServices.GetRequiredService<ITenantContext>().IsResolved
                ? await next(context)
                : Results.BadRequest(new { message = "A tenant is required. Use the X-Tenant-Slug header or a tenant subdomain." }));
        tenant.MapGet("/landing", GetContentAsync).RequireAuthorization("tenant.tenant.manage");
        tenant.MapPut("/landing", SaveContentAsync).RequireAuthorization("tenant.tenant.manage");
        tenant.MapPost("/landing/reset", ResetContentAsync).RequireAuthorization("tenant.tenant.manage");
        tenant.MapGet("/applications", ListApplicationsAsync).RequireAuthorization("tenant.enrollment.manage");
        tenant.MapPost("/applications/{applicationId:guid}/approve", ApproveAsync).RequireAuthorization("tenant.enrollment.manage");
        tenant.MapPost("/applications/{applicationId:guid}/decline", DeclineAsync).RequireAuthorization("tenant.enrollment.manage");
    }

    // ---------- public ----------
    private static async Task<Tenant?> FindTenantAsync(string slug, LmsDbContext db, ITenantContext tenantContext, CancellationToken cancellationToken)
    {
        var normalized = TenantSlug.Normalize(slug);
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(item => item.Slug == normalized && item.Status == TenantStatus.Active, cancellationToken);
        if (tenant is not null) tenantContext.Set(tenant.Id, tenant.Slug);   // the address, not a header, says which organization this is
        return tenant;
    }

    /// <summary>The organization a front page shows when the address does not name one (single-organization installs).</summary>
    private static async Task<IResult> DefaultOrganizationAsync(HttpContext httpContext, LmsDbContext db, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var slug = configuration["Public:DefaultTenantSlug"]?.Trim();
        if (string.IsNullOrEmpty(slug)) return Results.NotFound();
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(item => item.Slug == TenantSlug.Normalize(slug) && item.Status == TenantStatus.Active, cancellationToken);
        return tenant is null ? Results.NotFound() : Results.Ok(new PublicOrganization(tenant.Slug, tenant.Name));
    }

    /// <summary>
    /// What this website is. An address that belongs to an organization (its own domain, a subdomain, or the one this installation is set up for)
    /// is that organization's website. Any other address is the shared portal, where people name their organization to sign in.
    /// </summary>
    private static async Task<IResult> SiteAsync(string? host, LmsDbContext db, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var tenant = await TenantHosts.FindAsync(db, configuration, host, cancellationToken);
        if (tenant is null && configuration["Public:DefaultTenantSlug"]?.Trim() is { Length: > 0 } slug)
            tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(item => item.Slug == TenantSlug.Normalize(slug) && item.Status == TenantStatus.Active, cancellationToken);
        return tenant is null ? Results.Ok(new SiteResponse("portal", null)) : Results.Ok(new SiteResponse("tenant", new PublicOrganization(tenant.Slug, tenant.Name)));
    }

    private static bool HasPlatformAccess(HttpContext httpContext, IConfiguration configuration)
        => !string.IsNullOrWhiteSpace(configuration["Platform:ProvisioningKey"])
           && string.Equals(configuration["Platform:ProvisioningKey"], httpContext.Request.Headers["X-Platform-Key"].FirstOrDefault(), StringComparison.Ordinal);

    private static async Task<IResult> ListDomainsAsync(string slug, HttpContext httpContext, LmsDbContext db, IConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!HasPlatformAccess(httpContext, configuration)) return Results.Unauthorized();
        var tenantId = await db.Tenants.AsNoTracking().Where(item => item.Slug == TenantSlug.Normalize(slug)).Select(item => (Guid?)item.Id).SingleOrDefaultAsync(cancellationToken);
        if (tenantId is null) return Results.NotFound();
        return Results.Ok(await db.TenantDomains.AsNoTracking().Where(item => item.TenantId == tenantId).OrderBy(item => item.Host).Select(item => item.Host).ToListAsync(cancellationToken));
    }

    private static async Task<IResult> AddDomainAsync(string slug, AddDomainRequest request, HttpContext httpContext, LmsDbContext db, IConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!HasPlatformAccess(httpContext, configuration)) return Results.Unauthorized();
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(item => item.Slug == TenantSlug.Normalize(slug), cancellationToken);
        if (tenant is null) return Results.NotFound();
        if (!TenantHosts.IsCustomDomain(request.Host)) return Results.BadRequest(new { message = "Enter a website address such as learn.school.edu (no https://, path or port)." });
        var host = TenantHosts.Normalize(request.Host)!;
        var existing = await db.TenantDomains.SingleOrDefaultAsync(item => item.Host == host, cancellationToken);
        if (existing is not null) return existing.TenantId == tenant.Id ? Results.Ok(new { host }) : Results.Conflict(new { message = "That address already belongs to another organization." });
        if (await db.TenantDomains.CountAsync(item => item.TenantId == tenant.Id, cancellationToken) >= 10) return Results.Conflict(new { message = "An organization can have at most 10 website addresses." });
        db.TenantDomains.Add(new TenantDomain { Id = Guid.NewGuid(), TenantId = tenant.Id, Host = host, CreatedAtUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/platform/tenants/{tenant.Slug}/domains", new { host });
    }

    private static async Task<IResult> RemoveDomainAsync(string slug, string host, HttpContext httpContext, LmsDbContext db, IConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!HasPlatformAccess(httpContext, configuration)) return Results.Unauthorized();
        var tenantId = await db.Tenants.AsNoTracking().Where(item => item.Slug == TenantSlug.Normalize(slug)).Select(item => (Guid?)item.Id).SingleOrDefaultAsync(cancellationToken);
        var normalized = TenantHosts.Normalize(host);
        var row = tenantId is null || normalized is null ? null : await db.TenantDomains.SingleOrDefaultAsync(item => item.TenantId == tenantId && item.Host == normalized, cancellationToken);
        if (row is null) return Results.NotFound();
        db.TenantDomains.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> LandingAsync(string slug, HttpContext httpContext, LmsDbContext db, ITenantContext tenantContext, CancellationToken cancellationToken)
    {
        if (await FindTenantAsync(slug, db, tenantContext, cancellationToken) is not { } tenant) return Results.NotFound(new { message = "That organization was not found." });
        var stored = await db.LandingPages.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var content = LandingContentRules.Read(stored?.ContentJson, tenant.Name);

        var courses = await db.Courses.AsNoTracking().Where(item => item.Status == CourseStatus.Published)
            .OrderByDescending(item => item.PublishedAtUtc ?? item.CreatedAtUtc).Take(MaxCourses).ToListAsync(cancellationToken);
        var ids = courses.Select(item => item.Id).ToList();
        var owners = courses.Select(item => item.OwnerUserId).Distinct().ToList();
        var teachers = await db.Users.AsNoTracking().Where(item => owners.Contains(item.Id)).ToDictionaryAsync(item => item.Id, item => item.DisplayName, cancellationToken);
        var categories = await db.CourseCategories.AsNoTracking().OrderBy(item => item.Name).ToListAsync(cancellationToken);
        var taken = (await db.Enrollments.AsNoTracking().Where(item => ids.Contains(item.CourseId) && (item.Status == EnrollmentStatus.Active || item.Status == EnrollmentStatus.Completed))
            .GroupBy(item => item.CourseId).Select(group => new { group.Key, Count = group.Count() }).ToListAsync(cancellationToken)).ToDictionary(item => item.Key, item => item.Count);
        var categoryNames = categories.ToDictionary(item => item.Id, item => item.Name);
        var ratings = await RatingEndpoints.SummariesAsync(db, ids, cancellationToken);

        PublicCourse Card(Course course)
        {
            var count = taken.GetValueOrDefault(course.Id);
            ratings.TryGetValue(course.Id, out var rating);
            return new PublicCourse(course.Id, course.Code, course.Title, Summarise(course.Description), course.CategoryId, course.CategoryId is Guid category ? categoryNames.GetValueOrDefault(category) : null,
                teachers.GetValueOrDefault(course.OwnerUserId), course.StartDateAd, course.EndDateAd, course.Capacity is int capacity ? Math.Max(0, capacity - count) : null,
                rating?.Average, rating?.Count ?? 0);
        }

        var cards = courses.Select(Card).ToList();
        var byId = courses.ToDictionary(item => item.Id);
        var rows = content.Rows.Select(row =>
        {
            IEnumerable<Course> source = row.Mode switch
            {
                "popular" => courses.OrderByDescending(item => taken.GetValueOrDefault(item.Id)).ThenByDescending(item => item.PublishedAtUtc ?? item.CreatedAtUtc),
                "category" => courses.Where(item => item.CategoryId == row.CategoryId),
                "manual" => row.CourseIds.Where(byId.ContainsKey).Select(id => byId[id]),
                _ => courses
            };
            return new PublicRow(row.Id, row.Title, row.Subtitle, source.Take(row.Limit).Select(item => item.Id).ToList());
        }).Where(row => row.CourseIds.Count > 0).ToList();

        var counts = courses.Where(item => item.CategoryId != null).GroupBy(item => item.CategoryId!.Value).ToDictionary(group => group.Key, group => group.Count());
        var shownCategories = categories.Where(item => counts.ContainsKey(item.Id)).Select(item => new PublicCategory(item.Id, item.Name, counts[item.Id])).ToList();
        // A picture that was deleted after the page was saved is simply not shown.
        var pictures = (await db.LandingImages.AsNoTracking().Select(item => item.Id).ToListAsync(cancellationToken)).Select(item => item.ToString("D")).ToHashSet();
        string Keep(string id) => pictures.Contains(id) ? id : string.Empty;
        content = content with { LogoImageId = Keep(content.LogoImageId), HeroImageId = Keep(content.HeroImageId), Banners = content.Banners.Select(item => item with { ImageId = Keep(item.ImageId) }).ToList() };
        httpContext.Response.Headers.CacheControl = "no-cache";   // always ask again: a change staff just saved must show straight away
        return Results.Ok(new PublicLanding(new PublicOrganization(tenant.Slug, tenant.Name), content, cards, shownCategories, rows));
    }

    private static async Task<IResult> CourseAsync(string slug, Guid courseId, LmsDbContext db, ITenantContext tenantContext, CancellationToken cancellationToken)
    {
        if (await FindTenantAsync(slug, db, tenantContext, cancellationToken) is null) return Results.NotFound(new { message = "That organization was not found." });
        var course = await db.Courses.AsNoTracking().SingleOrDefaultAsync(item => item.Id == courseId && item.Status == CourseStatus.Published, cancellationToken);
        if (course is null) return Results.NotFound(new { message = "That course was not found." });
        var teacher = await db.Users.AsNoTracking().Where(item => item.Id == course.OwnerUserId).Select(item => item.DisplayName).SingleOrDefaultAsync(cancellationToken);
        var category = course.CategoryId is Guid categoryId ? await db.CourseCategories.AsNoTracking().Where(item => item.Id == categoryId).Select(item => item.Name).SingleOrDefaultAsync(cancellationToken) : null;
        var taken = await db.Enrollments.AsNoTracking().CountAsync(item => item.CourseId == course.Id && (item.Status == EnrollmentStatus.Active || item.Status == EnrollmentStatus.Completed), cancellationToken);
        var modules = course.CurrentVersionId is Guid versionId
            ? await db.CourseModules.AsNoTracking().Where(item => item.CourseVersionId == versionId).OrderBy(item => item.DisplayOrder).ToListAsync(cancellationToken) : [];
        var moduleIds = modules.Select(item => item.Id).ToList();
        var lessons = await db.CourseLessons.AsNoTracking().Where(item => moduleIds.Contains(item.CourseModuleId)).OrderBy(item => item.DisplayOrder).Select(item => new { item.CourseModuleId, item.Title }).ToListAsync(cancellationToken);
        var rating = (await RatingEndpoints.SummariesAsync(db, [course.Id], cancellationToken)).GetValueOrDefault(course.Id);
        return Results.Ok(new PublicCourseDetail(course.Id, course.Code, course.Title, course.Description, category, teacher, course.StartDateAd, course.EndDateAd,
            course.Capacity is int capacity ? Math.Max(0, capacity - taken) : null,
            modules.Select(module => new PublicModule(module.Title, lessons.Where(item => item.CourseModuleId == module.Id).Select(item => item.Title).ToList())).ToList(),
            rating, await RatingEndpoints.ReviewsAsync(db, course.Id, 20, cancellationToken)));
    }

    private static async Task<IResult> ApplyAsync(string slug, Guid courseId, ApplyRequest request, LmsDbContext db, ITenantContext tenantContext, CancellationToken cancellationToken)
    {
        if (await FindTenantAsync(slug, db, tenantContext, cancellationToken) is not { } tenant) return Results.NotFound(new { message = "That organization was not found." });
        var thanks = new { message = "Thank you! We received your application. We will review it and send an invitation to your email address." };
        if (!string.IsNullOrWhiteSpace(request.Website)) return Results.Accepted(null, thanks);          // a hidden box only a program fills in
        var name = request.FullName?.Trim() ?? string.Empty;
        var email = request.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        if (name.Length is < 2 or > 120) return Results.BadRequest(new { message = "Enter your name (2 to 120 characters)." });
        if (!IsEmail(email) || email.Length > 200) return Results.BadRequest(new { message = "Enter a valid email address." });
        if (request.Phone is { Length: > 40 }) return Results.BadRequest(new { message = "The phone number must be 40 characters or fewer." });
        if (request.Message is { Length: > 1000 }) return Results.BadRequest(new { message = "The message must be 1000 characters or fewer." });
        var course = await db.Courses.AsNoTracking().SingleOrDefaultAsync(item => item.Id == courseId && item.Status == CourseStatus.Published, cancellationToken);
        if (course is null) return Results.NotFound(new { message = "That course was not found." });

        // Applying twice for the same course changes nothing; one address cannot flood the inbox.
        if (await db.CourseApplications.AnyAsync(item => item.CourseId == courseId && item.Email == email && item.Status == ApplicationStatus.Pending, cancellationToken)) return Results.Accepted(null, thanks);
        if (await db.CourseApplications.CountAsync(item => item.Email == email && item.Status == ApplicationStatus.Pending, cancellationToken) >= 10) return Results.Accepted(null, thanks);
        db.CourseApplications.Add(new CourseApplication
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, CourseId = courseId, FullName = name, Email = email,
            Phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim(), Message = string.IsNullOrWhiteSpace(request.Message) ? null : request.Message.Trim(),
            Status = ApplicationStatus.Pending, CreatedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
        return Results.Accepted(null, thanks);
    }

    // ---------- content management ----------
    private static async Task<IResult> GetContentAsync(LmsDbContext db, ITenantContext tenantContext, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(item => item.Id == tenantContext.TenantId, cancellationToken);
        var stored = await db.LandingPages.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var domains = await db.TenantDomains.AsNoTracking().Where(item => item.TenantId == tenant.Id).OrderBy(item => item.Host).Select(item => item.Host).ToListAsync(cancellationToken);
        return Results.Ok(new ContentResponse(LandingContentRules.Read(stored?.ContentJson, tenant.Name), stored is null, stored?.UpdatedAtUtc, tenant.Slug, LandingContentRules.Themes, LandingContentRules.Modes, LandingContentRules.Icons, domains));
    }

    private static async Task<IResult> SaveContentAsync(HttpContext httpContext, LmsDbContext db, ITenantContext tenantContext, LandingContent request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId) return Results.Unauthorized();
        var content = LandingContentRules.Normalize(request, out var error);
        if (content is null) return Results.BadRequest(new { message = error ?? "The page could not be saved." });
        var wanted = LandingContentRules.ImageIds(content);
        if (wanted.Count > 0 && await db.LandingImages.CountAsync(item => wanted.Contains(item.Id), cancellationToken) != wanted.Count)
            return Results.BadRequest(new { message = "A picture on the page was deleted. Choose another one, or remove it from the page." });
        var page = await db.LandingPages.SingleOrDefaultAsync(cancellationToken);
        if (page is null) { page = new LandingPage { Id = Guid.NewGuid(), TenantId = tenantId }; db.LandingPages.Add(page); }
        page.ContentJson = LandingContentRules.Serialize(content);
        page.UpdatedByUserId = Guid.TryParse(httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ? userId : null;
        page.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(item => item.Id == tenantId, cancellationToken);
        var domains = await db.TenantDomains.AsNoTracking().Where(item => item.TenantId == tenantId).OrderBy(item => item.Host).Select(item => item.Host).ToListAsync(cancellationToken);
        return Results.Ok(new ContentResponse(content, false, page.UpdatedAtUtc, tenant.Slug, LandingContentRules.Themes, LandingContentRules.Modes, LandingContentRules.Icons, domains));
    }

    private static async Task<IResult> ResetContentAsync(LmsDbContext db, ITenantContext tenantContext, CancellationToken cancellationToken)
    {
        var page = await db.LandingPages.SingleOrDefaultAsync(cancellationToken);
        if (page is not null) { db.LandingPages.Remove(page); await db.SaveChangesAsync(cancellationToken); }
        return await GetContentAsync(db, tenantContext, cancellationToken);
    }

    // ---------- applications ----------
    private static async Task<IResult> ListApplicationsAsync(string? status, LmsDbContext db, CancellationToken cancellationToken)
    {
        var query = db.CourseApplications.AsNoTracking().AsQueryable();
        if (Enum.TryParse<ApplicationStatus>(status, true, out var parsed)) query = query.Where(item => item.Status == parsed);
        var rows = await query.OrderBy(item => item.Status).ThenByDescending(item => item.CreatedAtUtc).Take(300).ToListAsync(cancellationToken);
        var courseIds = rows.Select(item => item.CourseId).Distinct().ToList();
        var titles = await db.Courses.AsNoTracking().Where(item => courseIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, item => item.Title, cancellationToken);
        return Results.Ok(rows.Select(item => ToResponse(item, titles.GetValueOrDefault(item.CourseId, "Course"))).ToList());
    }

    private static async Task<IResult> ApproveAsync(Guid applicationId, HttpContext httpContext, LmsDbContext db, ITenantContext tenantContext, InvitationService invitations, IConfiguration configuration, ApproveRequest? request, CancellationToken cancellationToken)
    {
        if (tenantContext.TenantId is not Guid tenantId || !Guid.TryParse(httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), out var staffId)) return Results.Unauthorized();
        var application = await db.CourseApplications.SingleOrDefaultAsync(item => item.Id == applicationId, cancellationToken);
        if (application is null) return Results.NotFound();
        var days = request?.ExpiresInDays ?? InvitationService.DefaultDays;
        if (days is < 1 or > 90) return Results.BadRequest(new { message = "An invitation can last 1 to 90 days." });
        var course = await db.Courses.AsNoTracking().SingleOrDefaultAsync(item => item.Id == application.CourseId && item.Status == CourseStatus.Published, cancellationToken);
        if (course is null) return Results.Conflict(new { message = "The course is not published any more, so it cannot take new learners." });

        var inviter = httpContext.User.FindFirstValue(ClaimTypes.Name) ?? "A teacher";
        var result = await invitations.InviteAsync(db, tenantId, course, application.Email, staffId, inviter, request?.Message, days, cancellationToken);
        application.Status = ApplicationStatus.Approved;
        application.DecidedAtUtc = DateTimeOffset.UtcNow;
        application.DecidedByUserId = staffId;
        if (result.Outcome == InviteOutcome.AlreadyEnrolled)
        {
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(new ApprovalResponse(ToResponse(application, course.Title), null, null, null, "AlreadyEnrolled", null));
        }
        application.InvitationId = result.Invitation!.Id;
        await db.SaveChangesAsync(cancellationToken);
        var tenantSlug = tenantContext.TenantSlug ?? string.Empty;
        var link = InvitationService.BuildLink(WebBaseUrl(httpContext, configuration), tenantSlug, result.Token!);
        var email = result.HasAccount ? new InviteEmailResult("NotNeeded", null) : await invitations.SendInvitationEmailAsync(db, result.Invitation, course, result.Token!, inviter, link, cancellationToken);
        return Results.Ok(new ApprovalResponse(ToResponse(application, course.Title), result.Token, link, result.Invitation.ExpiresAtUtc, email.Status, email.Error));
    }

    private static async Task<IResult> DeclineAsync(Guid applicationId, HttpContext httpContext, LmsDbContext db, CancellationToken cancellationToken)
    {
        var application = await db.CourseApplications.SingleOrDefaultAsync(item => item.Id == applicationId, cancellationToken);
        if (application is null) return Results.NotFound();
        if (application.Status == ApplicationStatus.Approved) return Results.Conflict(new { message = "This application was already approved." });
        application.Status = ApplicationStatus.Declined;
        application.DecidedAtUtc = DateTimeOffset.UtcNow;
        application.DecidedByUserId = Guid.TryParse(httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), out var staffId) ? staffId : null;
        await db.SaveChangesAsync(cancellationToken);
        var title = await db.Courses.AsNoTracking().Where(item => item.Id == application.CourseId).Select(item => item.Title).SingleOrDefaultAsync(cancellationToken) ?? "Course";
        return Results.Ok(ToResponse(application, title));
    }

    // ---------- helpers ----------
    private static ApplicationResponse ToResponse(CourseApplication item, string courseTitle)
        => new(item.Id, item.CourseId, courseTitle, item.FullName, item.Email, item.Phone, item.Message, item.Status.ToString(), item.CreatedAtUtc, item.DecidedAtUtc, item.InvitationId);

    private static string Summarise(string? description)
    {
        var text = string.Join(' ', (description ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 180 ? text : text[..177].TrimEnd() + "…";
    }

    private static bool IsEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { return new MailAddress(value.Trim()).Address.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase); }
        catch (FormatException) { return false; }
    }

    private static string? WebBaseUrl(HttpContext context, IConfiguration configuration)
    {
        var configured = configuration["App:PublicUrl"];
        if (Uri.TryCreate(configured, UriKind.Absolute, out var set) && set.Scheme is "http" or "https") return set.ToString().TrimEnd('/');
        var origin = context.Request.Headers.Origin.FirstOrDefault();
        return Uri.TryCreate(origin, UriKind.Absolute, out var seen) && seen.Scheme is "http" or "https" ? seen.GetLeftPart(UriPartial.Authority) : null;
    }
}

public sealed record PublicOrganization(string Slug, string Name);
public sealed record PublicCourse(Guid Id, string Code, string Title, string Summary, Guid? CategoryId, string? Category, string? Teacher, DateOnly? StartDate, DateOnly? EndDate, int? SeatsLeft, double? RatingAverage = null, int RatingCount = 0);
public sealed record PublicCategory(Guid Id, string Name, int Courses);
public sealed record PublicRow(string Id, string Title, string? Subtitle, List<Guid> CourseIds);
public sealed record PublicLanding(PublicOrganization Organization, LandingContent Content, List<PublicCourse> Courses, List<PublicCategory> Categories, List<PublicRow> Rows);
public sealed record PublicModule(string Title, List<string> Lessons);
public sealed record PublicCourseDetail(Guid Id, string Code, string Title, string? Description, string? Category, string? Teacher, DateOnly? StartDate, DateOnly? EndDate, int? SeatsLeft, List<PublicModule> Modules, RatingSummary? Rating = null, List<PublicReview>? Reviews = null);
public sealed record ApplyRequest(string? FullName, string? Email, string? Phone = null, string? Message = null, string? Website = null);
public sealed record ApproveRequest(string? Message = null, int? ExpiresInDays = null);
public sealed record ApplicationResponse(Guid Id, Guid CourseId, string CourseTitle, string FullName, string Email, string? Phone, string? Message, string Status, DateTimeOffset CreatedAtUtc, DateTimeOffset? DecidedAtUtc, Guid? InvitationId);
public sealed record ApprovalResponse(ApplicationResponse Application, string? Token, string? Link, DateTimeOffset? ExpiresAtUtc, string EmailStatus, string? EmailError);
public sealed record ContentResponse(LandingContent Content, bool IsDefault, DateTimeOffset? UpdatedAtUtc, string Slug, string[] Themes, string[] Modes, string[] Icons, List<string>? Domains = null);
public sealed record SiteResponse(string Mode, PublicOrganization? Organization);
public sealed record AddDomainRequest(string? Host);
