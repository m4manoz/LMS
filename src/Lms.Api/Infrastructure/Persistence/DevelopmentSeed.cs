using Lms.Api.Domain.Courses;
using Lms.Api.Domain.Identity;
using Lms.Api.Domain.Tenants;
using Lms.Api.Features.Identity;
using Lms.Api.Infrastructure.Security;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lms.Api.Infrastructure.Persistence;

/// <summary>
/// Creates a demo organization, its administrator and a few course categories so a fresh
/// development database is usable immediately. Enabled with Seed:DevData:Enabled and never in Production.
/// Safe to run on every start. Course content comes from scripts/seed-demo-data.ps1.
/// </summary>
public static class DevelopmentSeed
{
    private static readonly string[] CategoryNames =
        ["Mathematics", "Science", "Languages", "Technology", "Arts", "Professional skills"];

    public static async Task SeedAsync(
        LmsDbContext db,
        PasswordService passwordService,
        IConfiguration configuration,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        if (environment.IsProduction() || !configuration.GetValue("Seed:DevData:Enabled", false))
        {
            return;
        }

        var slug = TenantSlug.Normalize(configuration["Seed:DevData:TenantSlug"] ?? "acme");
        var tenant = await db.Tenants.SingleOrDefaultAsync(item => item.Slug == slug, cancellationToken);
        if (tenant is null)
        {
            tenant = new Tenant
            {
                Id = Guid.NewGuid(),
                Slug = slug,
                Name = configuration["Seed:DevData:TenantName"] ?? "Acme Academy",
                Status = TenantStatus.Active,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync(cancellationToken);
        }

        await IdentityEndpoints.EnsureDefaultRolesAsync(db, tenant.Id, cancellationToken);

        var adminEmail = configuration["Seed:DevData:AdminEmail"] ?? "admin@acme.test";
        var adminPassword = configuration["Seed:DevData:AdminPassword"] ?? "Admin-pass-123";
        var normalizedEmail = adminEmail.Trim().ToUpperInvariant();
        var user = await db.Users.SingleOrDefaultAsync(item => item.NormalizedEmail == normalizedEmail, cancellationToken);
        if (user is null)
        {
            user = new AppUser
            {
                Id = Guid.NewGuid(),
                Email = adminEmail.Trim(),
                NormalizedEmail = normalizedEmail,
                DisplayName = "Admin",
                PasswordHash = passwordService.Hash(adminPassword),
                Status = UserStatus.Active,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            db.Users.Add(user);
        }

        var hasMembership = await db.TenantMemberships.IgnoreQueryFilters()
            .AnyAsync(item => item.TenantId == tenant.Id && item.UserId == user.Id, cancellationToken);
        if (!hasMembership)
        {
            var role = await db.Roles.IgnoreQueryFilters()
                .SingleAsync(item => item.TenantId == tenant.Id && item.Code == "TENANT_ADMIN", cancellationToken);
            db.TenantMemberships.Add(new TenantMembership
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                UserId = user.Id,
                RoleId = role.Id,
                Status = MembershipStatus.Active,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
        }

        var existingSlugs = (await db.CourseCategories.IgnoreQueryFilters()
            .Where(item => item.TenantId == tenant.Id)
            .Select(item => item.Slug)
            .ToListAsync(cancellationToken)).ToHashSet();
        foreach (var name in CategoryNames)
        {
            var categorySlug = name.ToLowerInvariant().Replace(' ', '-');
            if (existingSlugs.Add(categorySlug))
            {
                db.CourseCategories.Add(new CourseCategory
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenant.Id,
                    Name = name,
                    Slug = categorySlug
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
