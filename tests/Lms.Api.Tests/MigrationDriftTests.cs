using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Lms.Api.Tests;

public sealed class MigrationDriftTests
{
    /// <summary>
    /// Fails when the EF model has changes that are not captured in a migration, which would make the API
    /// refuse to start with PendingModelChangesWarning. No database connection is opened.
    /// </summary>
    [Fact]
    public void Model_matches_latest_migration_snapshot()
    {
        var options = new DbContextOptionsBuilder<LmsDbContext>()
            .UseNpgsql("Host=localhost;Database=lms_drift_check;Username=x;Password=x")
            .Options;
        using var db = new LmsDbContext(options, new TenantContext());

        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot;
        Assert.NotNull(snapshot);

        var differ = db.GetService<IMigrationsModelDiffer>();
        var designTimeModel = db.GetService<IDesignTimeModel>().Model;
        var snapshotModel = db.GetService<IModelRuntimeInitializer>().Initialize(snapshot!.Model);

        var differences = differ.GetDifferences(snapshotModel.GetRelationalModel(), designTimeModel.GetRelationalModel());
        Assert.True(differences.Count == 0,
            $"Model has {differences.Count} unmigrated change(s). Run: dotnet ef migrations add <Name> --project src/Lms.Api");
    }
}
