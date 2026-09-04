using BookSpace.Application.Common;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// Proves the RowVersion-backed optimistic concurrency guard on Resource and BlackoutPeriod against a
// REAL SQL Server (LocalDB) - the earlier RefreshTokenRotationConcurrencyTests only proved the shared
// mechanism (RowVersion + SaveChangesHandlingConflictsAsync) for RefreshToken; this proves it was
// actually wired up correctly for these two entities specifically, not merely assumed to follow along
// because the code looks similar. SQLite can't raise DbUpdateConcurrencyException the same way (see
// BookSpaceDbContext.OnModelCreating's useRowVersionColumns gate), so this can't be proven there.
public sealed class OptimisticConcurrencyTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceOptimisticConcurrencyTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    private Guid _tenantId;
    private Guid _resourceId;
    private Guid _blackoutPeriodId;

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureCreatedAsync();

        _tenantId = Guid.NewGuid();
        var resourceTypeId = Guid.NewGuid();
        _resourceId = Guid.NewGuid();
        _blackoutPeriodId = Guid.NewGuid();

        dbContext.Tenants.Add(new Tenant
        {
            Id = _tenantId, Name = "Optimistic Concurrency Tenant", DefaultTimeZoneId = "UTC",
            Status = TenantStatus.Active, CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        dbContext.ResourceTypes.Add(new ResourceType { Id = resourceTypeId, TenantId = _tenantId, Name = "Meeting Room" });
        dbContext.Resources.Add(new Resource
        {
            Id = _resourceId, TenantId = _tenantId, ResourceTypeId = resourceTypeId, Name = "Concurrency Test Room",
            Capacity = 4, RequiresApproval = false, Status = ResourceStatus.Active, TimeZoneId = "UTC",
        });
        dbContext.BlackoutPeriods.Add(new BlackoutPeriod
        {
            Id = _blackoutPeriodId, TenantId = _tenantId, ResourceId = _resourceId,
            StartUtc = DateTimeOffset.UtcNow.AddDays(1), EndUtc = DateTimeOffset.UtcNow.AddDays(2), Reason = "Original reason",
        });
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureDeletedAsync();
    }

    [Fact]
    public async Task ConcurrentUpdates_OfTheSameResource_SecondStaleWriteThrowsConflictException()
    {
        await using var firstReaderContext = CreateDbContext();
        var firstCopy = await firstReaderContext.Resources.SingleAsync(resource => resource.Id == _resourceId);

        await using var secondReaderContext = CreateDbContext();
        var secondCopy = await secondReaderContext.Resources.SingleAsync(resource => resource.Id == _resourceId);

        firstCopy.Capacity = 10;
        await firstReaderContext.SaveChangesHandlingConflictsAsync(CancellationToken.None);

        secondCopy.Capacity = 20;
        await Assert.ThrowsAsync<ConflictException>(() => secondReaderContext.SaveChangesHandlingConflictsAsync(CancellationToken.None));

        await using var verifyContext = CreateDbContext();
        var current = await verifyContext.Resources.SingleAsync(resource => resource.Id == _resourceId);
        // The first (non-stale) write won; the second, stale write never silently applied on top of it.
        Assert.Equal(10, current.Capacity);
    }

    [Fact]
    public async Task ConcurrentUpdates_OfTheSameBlackoutPeriod_SecondStaleWriteThrowsConflictException()
    {
        await using var firstReaderContext = CreateDbContext();
        var firstCopy = await firstReaderContext.BlackoutPeriods.SingleAsync(period => period.Id == _blackoutPeriodId);

        await using var secondReaderContext = CreateDbContext();
        var secondCopy = await secondReaderContext.BlackoutPeriods.SingleAsync(period => period.Id == _blackoutPeriodId);

        firstCopy.Reason = "Updated by the first writer";
        await firstReaderContext.SaveChangesHandlingConflictsAsync(CancellationToken.None);

        secondCopy.Reason = "Updated by the second, stale writer";
        await Assert.ThrowsAsync<ConflictException>(() => secondReaderContext.SaveChangesHandlingConflictsAsync(CancellationToken.None));

        await using var verifyContext = CreateDbContext();
        var current = await verifyContext.BlackoutPeriods.SingleAsync(period => period.Id == _blackoutPeriodId);
        Assert.Equal("Updated by the first writer", current.Reason);
    }

    [Fact]
    public async Task InsertingANewResource_IsUnaffectedByRowVersion()
    {
        await using var dbContext = CreateDbContext();
        var resourceTypeId = await dbContext.ResourceTypes.Select(type => type.Id).FirstAsync();

        dbContext.Resources.Add(new Resource
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, ResourceTypeId = resourceTypeId, Name = "Freshly Inserted Room",
            Capacity = 2, RequiresApproval = false, Status = ResourceStatus.Active, TimeZoneId = "UTC",
        });

        await dbContext.SaveChangesHandlingConflictsAsync(CancellationToken.None);

        Assert.Equal(2, await dbContext.Resources.CountAsync());
    }

    private BookSpaceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>().UseSqlServer(_connectionString).Options;
        return new BookSpaceDbContext(options, new FixedCurrentUserContext(_tenantId));
    }

    private sealed class FixedCurrentUserContext(Guid tenantId) : ICurrentUserContext
    {
        public Guid? UserId => null;
        public Guid? TenantId => tenantId;
        public IReadOnlyCollection<string> Roles => [];
    }
}
