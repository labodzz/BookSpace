using BookSpace.Application.Bookings;
using BookSpace.Application.Common;
using BookSpace.Application.Resources;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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
        // Required for the resource to legitimately stay Active through UpdateResourceCommandHandler -
        // see Resource.AvailabilityRuleRequired.
        dbContext.AvailabilityRules.Add(new AvailabilityRule
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, ResourceId = _resourceId,
            DayOfWeek = DayOfWeek.Monday, StartTime = TimeOnly.MinValue, EndTime = TimeOnly.MaxValue,
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

    // Documents a real, non-obvious consequence of the IResourceBookingLock fix described in
    // docs/resource-lifecycle-and-capacity.md and docs/bookings-and-concurrency.md §7: the two tests
    // above prove RowVersion by mutating tracked entities directly, bypassing UpdateResourceCommandHandler
    // and its lock entirely - that is a valid proof that the mapping/mechanism works, but it does NOT
    // prove a RowVersion conflict is still reachable through two concurrent real Handle calls now that
    // the handler always wraps its entire read-modify-write in resourceBookingLock.RunExclusiveAsync.
    // Once the lock serializes both callers, whichever acquires it first fully commits (transaction
    // commit releases HOLDLOCK) before the second caller's own post-lock FindByIdAsync even runs - so the
    // second caller's read is always of the first caller's already-committed RowVersion, never a stale
    // one. Both requests below intentionally change DIFFERENT fields (Name vs. Description) so there is
    // no ordinary business conflict (e.g. duplicate name) that could mask the result: if a RowVersion
    // conflict were still reachable through this handler, one of the two would throw ConflictException.
    // Neither does - the pessimistic lock has made the optimistic-concurrency path unreachable for this
    // specific handler, which is worth knowing explicitly rather than assuming "RowVersion still protects
    // concurrent admin edits to a Resource" without having actually exercised the real code path.
    [Fact]
    public async Task ConcurrentUpdateResourceCommandHandlerCalls_ForTheSameResource_BothSucceed_RowVersionConflictIsUnreachableThroughTheHandler()
    {
        await using var setupContext = CreateDbContext();
        var resourceTypeId = await setupContext.ResourceTypes.Select(type => type.Id).FirstAsync();
        var resource = await setupContext.Resources.SingleAsync(r => r.Id == _resourceId);

        using var startGate = new SemaphoreSlim(0, 2);

        async Task<bool> RenameAsync()
        {
            await startGate.WaitAsync();
            await using var dbContext = CreateDbContext();
            var handler = new UpdateResourceCommandHandler(
                new ResourceBookingLock(dbContext, NullLogger<ResourceBookingLock>.Instance),
                new ResourceRepository(dbContext),
                new BookingAvailabilityRepository(dbContext),
                new AvailabilityRuleRepository(dbContext));
            try
            {
                await handler.Handle(
                    new UpdateResourceCommandRequest(
                        _resourceId, resourceTypeId, "Renamed by the first admin", resource.Description,
                        resource.Capacity, resource.RequiresApproval, resource.TimeZoneId, resource.Status),
                    CancellationToken.None);
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
        }

        async Task<bool> RedescribeAsync()
        {
            await startGate.WaitAsync();
            await using var dbContext = CreateDbContext();
            var handler = new UpdateResourceCommandHandler(
                new ResourceBookingLock(dbContext, NullLogger<ResourceBookingLock>.Instance),
                new ResourceRepository(dbContext),
                new BookingAvailabilityRepository(dbContext),
                new AvailabilityRuleRepository(dbContext));
            try
            {
                await handler.Handle(
                    new UpdateResourceCommandRequest(
                        _resourceId, resourceTypeId, resource.Name, "Redescribed by the second admin",
                        resource.Capacity, resource.RequiresApproval, resource.TimeZoneId, resource.Status),
                    CancellationToken.None);
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
        }

        var renameTask = Task.Run(RenameAsync);
        var redescribeTask = Task.Run(RedescribeAsync);
        startGate.Release(2);
        var results = await Task.WhenAll(renameTask, redescribeTask);

        // Neither call is rejected by a RowVersion conflict - the lock's strict commit-before-next-read
        // ordering means the second caller's fresh read always already reflects the first caller's write.
        Assert.All(results, succeeded => Assert.True(succeeded));
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
