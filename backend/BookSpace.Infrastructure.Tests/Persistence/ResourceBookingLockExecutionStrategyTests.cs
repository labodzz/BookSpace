using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// Regression test for a real production bug: BookSpaceDbContext is configured (see
// DependencyInjection.AddInfrastructure) with EnableRetryOnFailure, which puts a retrying execution
// strategy in front of every database operation. That strategy refuses to let a caller open its own
// transaction directly (BeginTransactionAsync) and run queries inside it - EF Core throws
// InvalidOperationException ("The configured execution strategy 'SqlServerRetryingExecutionStrategy' does
// not support user-initiated transactions...") the moment a query runs inside such a transaction.
// ResourceBookingLock did exactly that, so every call through it - UpdateResourceCommandHandler,
// CreateBookingCommandHandler, ApproveBookingCommandHandler - failed with a 500 in any environment where
// EnableRetryOnFailure was actually active.
//
// BookingConcurrencyTests (same folder) never caught this: its DbContext is built with a plain
// UseSqlServer(connectionString), deliberately without EnableRetryOnFailure, so it never exercised the
// retrying execution strategy at all. This test exists specifically to close that gap by mirroring
// AddInfrastructure's real configuration.
public sealed class ResourceBookingLockExecutionStrategyTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceResourceLockRetryTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    private Guid _tenantId;
    private Guid _resourceTypeId;
    private Guid _resourceId;

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContextWithRetryOnFailure();
        await dbContext.Database.EnsureCreatedAsync();

        _tenantId = Guid.NewGuid();
        _resourceTypeId = Guid.NewGuid();
        _resourceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        dbContext.Tenants.Add(new Tenant { Id = _tenantId, Name = "Retry Strategy Tenant", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = now });
        dbContext.ResourceTypes.Add(new ResourceType { Id = _resourceTypeId, TenantId = _tenantId, Name = "Meeting Room" });
        dbContext.Resources.Add(new Resource
        {
            Id = _resourceId, TenantId = _tenantId, ResourceTypeId = _resourceTypeId, Name = "Retry Strategy Room",
            Capacity = 1, RequiresApproval = false, Status = ResourceStatus.Active, TimeZoneId = "UTC",
        });
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContextWithRetryOnFailure();
        await dbContext.Database.EnsureDeletedAsync();
    }

    // Reproduces exactly the shape that crashed in production: a query (not just a non-query statement)
    // run via `operation` *inside* RunExclusiveAsync's transaction, against a DbContext configured the
    // same way the real application configures it. Before the fix, this threw InvalidOperationException
    // the moment the inner SingleOrDefaultAsync tried to iterate its results.
    [Fact]
    public async Task RunExclusiveAsync_OnADbContextConfiguredWithEnableRetryOnFailure_RunsAQueryInsideTheLockWithoutThrowing()
    {
        await using var dbContext = CreateDbContextWithRetryOnFailure();
        var resourceLock = new ResourceBookingLock(dbContext, NullLogger<ResourceBookingLock>.Instance);

        var resourceName = await resourceLock.RunExclusiveAsync(
            _resourceId,
            async ct =>
            {
                var resource = await dbContext.Resources.SingleAsync(r => r.Id == _resourceId, ct);
                return resource.Name;
            },
            CancellationToken.None);

        Assert.Equal("Retry Strategy Room", resourceName);
    }

    // The lock must still actually serialize two concurrent callers for the same resource under the
    // retry-enabled configuration, not just avoid throwing - the fix must not have accidentally weakened
    // the mutual-exclusion guarantee it exists to provide.
    [Fact]
    public async Task RunExclusiveAsync_OnADbContextConfiguredWithEnableRetryOnFailure_StillSerializesConcurrentCallersForTheSameResource()
    {
        const int concurrentCallers = 5;
        using var startGate = new SemaphoreSlim(0, concurrentCallers);
        var concurrentInsideLock = 0;
        var maxObservedConcurrency = 0;
        var gate = new object();

        async Task<int> AttemptAsync()
        {
            await using var dbContext = CreateDbContextWithRetryOnFailure();
            var resourceLock = new ResourceBookingLock(dbContext, NullLogger<ResourceBookingLock>.Instance);

            await startGate.WaitAsync();
            return await resourceLock.RunExclusiveAsync(
                _resourceId,
                async ct =>
                {
                    lock (gate)
                    {
                        concurrentInsideLock++;
                        maxObservedConcurrency = Math.Max(maxObservedConcurrency, concurrentInsideLock);
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(100), ct); // widen the window a real race would need
                    var resource = await dbContext.Resources.SingleAsync(r => r.Id == _resourceId, ct);

                    lock (gate)
                    {
                        concurrentInsideLock--;
                    }

                    return resource.Capacity;
                },
                CancellationToken.None);
        }

        var tasks = Enumerable.Range(0, concurrentCallers).Select(_ => Task.Run(AttemptAsync)).ToArray();
        startGate.Release(concurrentCallers);
        var results = await Task.WhenAll(tasks);

        Assert.All(results, capacity => Assert.Equal(1, capacity));
        Assert.Equal(1, maxObservedConcurrency); // never more than one caller inside the locked section at once
    }

    private BookSpaceDbContext CreateDbContextWithRetryOnFailure()
    {
        // Mirrors BookSpace.Infrastructure.DependencyInjection.AddInfrastructure exactly - this is the one
        // detail BookingConcurrencyTests' own CreateDbContext deliberately omits.
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>()
            .UseSqlServer(_connectionString, sqlOptions => sqlOptions.EnableRetryOnFailure(
                maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null))
            .Options;
        return new BookSpaceDbContext(options, new FixedCurrentUserContext(_tenantId));
    }

    private sealed class FixedCurrentUserContext(Guid tenantId) : ICurrentUserContext
    {
        public Guid? UserId => null;
        public Guid? TenantId => tenantId;
        public IReadOnlyCollection<string> Roles => [];
    }
}
