using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Lms.Api.Infrastructure.Persistence;

public sealed class LmsDbContextFactory : IDesignTimeDbContextFactory<LmsDbContext>
{
    public LmsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LmsDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=lms;Username=postgres;Password=postgre")
            .Options;
        return new LmsDbContext(options, new TenantContext());
    }
}
