using System.Text.Json;
using Lms.Api.Domain.Identity;
using Lms.Api.Domain.Offline;
using Lms.Api.Domain.VirtualLabs;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Tenancy;
using Lms.Api.Infrastructure.VirtualLabs;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Lms.Api.Tests;

public sealed class TrustBoundaryTests
{
    [Fact]
    public async Task TenantQueryFilterDoesNotExposeAnotherTenantDevice()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await using var db = CreateDb(tenantA);
        db.OfflineDevices.AddRange(
            new OfflineDevice { Id = Guid.NewGuid(), TenantId = tenantA, UserId = Guid.NewGuid(), Name = "A", FingerprintHash = "a", SecretHash = "a", CreatedAtUtc = DateTimeOffset.UtcNow, LastSeenAtUtc = DateTimeOffset.UtcNow },
            new OfflineDevice { Id = Guid.NewGuid(), TenantId = tenantB, UserId = Guid.NewGuid(), Name = "B", FingerprintHash = "b", SecretHash = "b", CreatedAtUtc = DateTimeOffset.UtcNow, LastSeenAtUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var visible = await db.OfflineDevices.ToListAsync();

        var device = Assert.Single(visible);
        Assert.Equal(tenantA, device.TenantId);
    }

    [Fact]
    public async Task InvalidWebhookIsDeadLetteredAfterMaximumAttempts()
    {
        var tenantId = Guid.NewGuid();
        var labId = Guid.NewGuid();
        await using var db = CreateDb(tenantId);
        var webhook = new VirtualLabWebhookEvent
        {
            Id = Guid.NewGuid(), TenantId = tenantId, VirtualLabId = labId, ExternalEventId = "invalid-1",
            PayloadJson = "{not-json", Signature = "signature", MaxAttempts = 3, ReceivedAtUtc = DateTimeOffset.UtcNow
        };
        db.VirtualLabWebhookEvents.Add(webhook);
        await db.SaveChangesAsync();
        var processor = new VirtualLabWebhookProcessor();

        for (var attempt = 0; attempt < 3; attempt++) await processor.ProcessAsync(db, webhook, CancellationToken.None);

        Assert.Equal("DeadLetter", webhook.Status);
        Assert.Equal(3, webhook.AttemptCount);
        Assert.NotNull(webhook.LastError);
    }

    [Fact]
    public async Task ValidWebhookCreatesOneResultAndIsIdempotent()
    {
        var tenantId = Guid.NewGuid();
        var labId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await using var db = CreateDb(tenantId);
        db.VirtualLabs.Add(new VirtualLab { Id = labId, TenantId = tenantId, Code = "LAB", Name = "Lab", ProviderType = "test", Status = VirtualLabStatus.Active, CreatedByUserId = userId, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        db.TenantMemberships.Add(new TenantMembership { Id = Guid.NewGuid(), TenantId = tenantId, UserId = userId, RoleId = Guid.NewGuid(), Status = MembershipStatus.Active, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        var webhook = new VirtualLabWebhookEvent
        {
            Id = Guid.NewGuid(), TenantId = tenantId, VirtualLabId = labId, ExternalEventId = "event-1",
            PayloadJson = JsonSerializer.Serialize(new { userId, externalAttemptId = "attempt-1", completed = true, scorePercent = 88 }),
            Signature = "signature", ReceivedAtUtc = DateTimeOffset.UtcNow
        };
        db.VirtualLabWebhookEvents.Add(webhook);
        await db.SaveChangesAsync();
        var processor = new VirtualLabWebhookProcessor();

        await processor.ProcessAsync(db, webhook, CancellationToken.None);
        await processor.ProcessAsync(db, webhook, CancellationToken.None);

        Assert.Equal("Processed", webhook.Status);
        Assert.Single(await db.VirtualLabResults.ToListAsync());
        Assert.Equal(1, webhook.AttemptCount);
    }

    private static LmsDbContext CreateDb(Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.Set(tenantId, "test");
        var options = new DbContextOptionsBuilder<LmsDbContext>()
            .UseInMemoryDatabase($"phase12-{Guid.NewGuid():N}")
            .Options;
        var db = new LmsDbContext(options, tenantContext);
        db.Database.EnsureCreated();
        return db;
    }
}
