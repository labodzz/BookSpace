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

// Proves the fix for a race between UpdateResourceCommandHandler's capacity-reduction check and
// CreateBookingCommandHandler's own capacity check, both targeting the same resource - see
// docs/bookings-and-concurrency.md. Before this fix, UpdateResourceCommandHandler never acquired
// IResourceBookingLock: it read active bookings, validated, and saved Capacity relying solely on
// Resource.RowVersion for conflict detection. RowVersion cannot catch this race because
// CreateBookingCommandHandler's insert never writes to the Resources row at all (only Bookings) - so a
// stale capacity reduction could commit with no detected conflict even though a concurrent booking
// committed in between made it invalid. Requires real SQL Server (LocalDB): SQLite has no UPDLOCK/
// HOLDLOCK support, so IResourceBookingLock is a no-op there and this race cannot be proven against it.
public sealed class UpdateResourceCapacityConcurrencyTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceCapacityConcurrencyTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    private Guid _tenantId;
    private Guid _resourceTypeId;
    private Guid _existingBookingOwnerId;
    private Guid _newBookingOwnerId;

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext(Guid.NewGuid());
        await dbContext.Database.EnsureCreatedAsync();

        _tenantId = Guid.NewGuid();
        _resourceTypeId = Guid.NewGuid();
        _existingBookingOwnerId = Guid.NewGuid();
        _newBookingOwnerId = Guid.NewGuid();

        var now = DateTimeOffset.UtcNow;
        dbContext.Tenants.Add(new Tenant { Id = _tenantId, Name = "Capacity Concurrency Tenant", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = now });
        dbContext.ResourceTypes.Add(new ResourceType { Id = _resourceTypeId, TenantId = _tenantId, Name = "Meeting Room" });
        dbContext.Users.Add(new User { Id = _existingBookingOwnerId, TenantId = _tenantId, FirstName = "Existing", LastName = "Owner", Email = "existing@capacity.test", PasswordHash = "x", CreatedAtUtc = now });
        dbContext.Users.Add(new User { Id = _newBookingOwnerId, TenantId = _tenantId, FirstName = "New", LastName = "Owner", Email = "new@capacity.test", PasswordHash = "x", CreatedAtUtc = now });
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext(Guid.NewGuid());
        await dbContext.Database.EnsureDeletedAsync();
    }

    // The exact race from the report: Capacity=10, one Confirmed booking already uses 5. Concurrently,
    // an admin tries to shrink Capacity to 6 (valid against the 5 they can currently see) while another
    // user books 3 more (valid against Capacity=10 with 5 already used: 5+3=8<=10). Whichever request
    // wins the resource lock, the other must re-validate against the now-current state - so the final
    // state must never have Capacity below the actual committed peak demand, regardless of which side
    // won. This is the core invariant the fix protects; which specific side "wins" is legitimately
    // nondeterministic and is not asserted.
    [Fact]
    public async Task ConcurrentCapacityReductionAndBookingCreation_NeverLeavesCapacityBelowCommittedDemand()
    {
        var resourceId = await SeedResourceAsync(capacity: 10);
        var existingWindowStart = DateTimeOffset.UtcNow.AddDays(1);
        var existingWindowEnd = existingWindowStart.AddHours(1);
        await SeedConfirmedBookingAsync(resourceId, _existingBookingOwnerId, existingWindowStart, existingWindowEnd, quantity: 5);

        using var startGate = new SemaphoreSlim(0, 2);

        async Task<bool> ReduceCapacityAsync()
        {
            await startGate.WaitAsync();
            await using var dbContext = CreateDbContext(_tenantId, _existingBookingOwnerId);
            var handler = new UpdateResourceCommandHandler(
                new ResourceBookingLock(dbContext, NullLogger<ResourceBookingLock>.Instance),
                new ResourceRepository(dbContext),
                new BookingAvailabilityRepository(dbContext));
            var resource = await dbContext.Resources.SingleAsync(r => r.Id == resourceId);
            try
            {
                await handler.Handle(
                    new UpdateResourceCommandRequest(
                        resourceId, _resourceTypeId, resource.Name, resource.Description, 6, resource.RequiresApproval, resource.TimeZoneId, resource.Status),
                    CancellationToken.None);
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
        }

        async Task<bool> CreateOverlappingBookingAsync()
        {
            await startGate.WaitAsync();
            await using var dbContext = CreateDbContext(_tenantId, _newBookingOwnerId);
            var handler = new CreateBookingCommandHandler(
                new ResourceBookingLock(dbContext, NullLogger<ResourceBookingLock>.Instance),
                new ResourceRepository(dbContext),
                new AvailabilityRuleRepository(dbContext),
                new BlackoutPeriodRepository(dbContext),
                new BookingAvailabilityRepository(dbContext),
                new BookingRepository(dbContext),
                new ResourceApproverRepository(dbContext),
                new ApprovalRequestRepository(dbContext),
                new TenantRepository(dbContext),
                new FixedCurrentUserContext(_tenantId, _newBookingOwnerId));
            try
            {
                await handler.Handle(
                    new CreateBookingCommandRequest(resourceId, existingWindowStart, existingWindowEnd, Quantity: 3), CancellationToken.None);
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
        }

        var reduceTask = Task.Run(ReduceCapacityAsync);
        var createTask = Task.Run(CreateOverlappingBookingAsync);
        startGate.Release(2);
        await Task.WhenAll(reduceTask, createTask);

        await using var verifyContext = CreateDbContext(_tenantId);
        var finalResource = await verifyContext.Resources.SingleAsync(r => r.Id == resourceId);
        var committedDemand = await verifyContext.Bookings
            .Where(b => b.ResourceId == resourceId && (b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed))
            .SumAsync(b => b.Quantity);

        // The one invariant that must never be violated, regardless of which request won the race.
        Assert.True(
            finalResource.Capacity >= committedDemand,
            $"Capacity {finalResource.Capacity} is below committed demand {committedDemand} - the race was not prevented.");
    }

    [Fact]
    public async Task ReduceCapacity_WhenExistingDemandFits_Succeeds()
    {
        var resourceId = await SeedResourceAsync(capacity: 10);
        var start = DateTimeOffset.UtcNow.AddDays(1);
        await SeedConfirmedBookingAsync(resourceId, _existingBookingOwnerId, start, start.AddHours(1), quantity: 5);

        var result = await UpdateCapacityAsync(resourceId, newCapacity: 5);

        Assert.Equal(5, result.Capacity);
    }

    [Fact]
    public async Task ReduceCapacity_WhenExistingDemandExceedsProposedCapacity_ThrowsConflictException()
    {
        var resourceId = await SeedResourceAsync(capacity: 10);
        var start = DateTimeOffset.UtcNow.AddDays(1);
        await SeedConfirmedBookingAsync(resourceId, _existingBookingOwnerId, start, start.AddHours(1), quantity: 5);

        await Assert.ThrowsAsync<ConflictException>(() => UpdateCapacityAsync(resourceId, newCapacity: 4));
    }

    [Fact]
    public async Task CreateBooking_AgainstAResourceUnaffectedByAnyConcurrentUpdate_StillSucceeds()
    {
        var resourceId = await SeedResourceAsync(capacity: 10);
        var start = DateTimeOffset.UtcNow.AddDays(1);
        await using var dbContext = CreateDbContext(_tenantId, _newBookingOwnerId);
        var handler = new CreateBookingCommandHandler(
            new ResourceBookingLock(dbContext, NullLogger<ResourceBookingLock>.Instance),
            new ResourceRepository(dbContext),
            new AvailabilityRuleRepository(dbContext),
            new BlackoutPeriodRepository(dbContext),
            new BookingAvailabilityRepository(dbContext),
            new BookingRepository(dbContext),
            new ResourceApproverRepository(dbContext),
            new ApprovalRequestRepository(dbContext),
            new TenantRepository(dbContext),
            new FixedCurrentUserContext(_tenantId, _newBookingOwnerId));

        var response = await handler.Handle(new CreateBookingCommandRequest(resourceId, start, start.AddHours(1), Quantity: 2), CancellationToken.None);

        Assert.Equal(BookingStatus.Confirmed, response.Status);
    }

    private async Task<UpdateResourceResponse> UpdateCapacityAsync(Guid resourceId, int newCapacity)
    {
        await using var dbContext = CreateDbContext(_tenantId, _existingBookingOwnerId);
        var handler = new UpdateResourceCommandHandler(
            new ResourceBookingLock(dbContext, NullLogger<ResourceBookingLock>.Instance),
            new ResourceRepository(dbContext),
            new BookingAvailabilityRepository(dbContext));
        var resource = await dbContext.Resources.SingleAsync(r => r.Id == resourceId);

        return await handler.Handle(
            new UpdateResourceCommandRequest(
                resourceId, _resourceTypeId, resource.Name, resource.Description, newCapacity, resource.RequiresApproval, resource.TimeZoneId, resource.Status),
            CancellationToken.None);
    }

    private async Task<Guid> SeedResourceAsync(int capacity)
    {
        var resourceId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(_tenantId);
        dbContext.Resources.Add(new Resource
        {
            Id = resourceId, TenantId = _tenantId, ResourceTypeId = _resourceTypeId, Name = $"Capacity Room {resourceId:N}",
            Capacity = capacity, RequiresApproval = false, Status = ResourceStatus.Active, TimeZoneId = "UTC",
        });
        dbContext.AvailabilityRules.AddRange(Enum.GetValues<DayOfWeek>().Select(dayOfWeek => new AvailabilityRule
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, ResourceId = resourceId,
            DayOfWeek = dayOfWeek, StartTime = TimeOnly.MinValue, EndTime = TimeOnly.MaxValue,
        }));
        await dbContext.SaveChangesAsync();
        return resourceId;
    }

    private async Task SeedConfirmedBookingAsync(Guid resourceId, Guid userId, DateTimeOffset start, DateTimeOffset end, int quantity)
    {
        await using var dbContext = CreateDbContext(_tenantId);
        dbContext.Bookings.Add(new Booking
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, ResourceId = resourceId, UserId = userId,
            StartUtc = start, EndUtc = end, Quantity = quantity, Status = BookingStatus.Confirmed, CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync();
    }

    private BookSpaceDbContext CreateDbContext(Guid tenantId, Guid? userId = null)
    {
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>().UseSqlServer(_connectionString).Options;
        return new BookSpaceDbContext(options, new FixedCurrentUserContext(tenantId, userId));
    }

    private sealed class FixedCurrentUserContext(Guid tenantId, Guid? userId = null) : ICurrentUserContext
    {
        public Guid? UserId => userId;
        public Guid? TenantId => tenantId;
        public IReadOnlyCollection<string> Roles => [];
    }
}
