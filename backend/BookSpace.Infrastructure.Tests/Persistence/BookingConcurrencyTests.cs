using BookSpace.Application.Bookings;
using BookSpace.Application.Common;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// Proves the "Booking Concurrency" open question (docs/open-questions.md) is now resolved against a
// REAL SQL Server (LocalDB) - see docs/bookings-and-concurrency.md for the full write-up of the chosen
// strategy (a transaction-scoped UPDLOCK+HOLDLOCK on the parent Resource row). SQLite cannot prove this:
// it has no equivalent locking hint and CreateBookingCommandHandler's own lock step is a no-op there
// (see ResourceBookingLock), so this genuinely requires LocalDB, not the fast SQLite-backed API suite.
//
// Two things are proven here, in order:
//  1. NaiveCheckThenInsert_RacedByTwoRequests_CanOverbook - a deliberately reconstructed "check
//     availability, then insert" anti-pattern (NOT the shipped code - CreateBookingCommandHandler never
//     looked like this), synchronized so both requests' read happens before either's write, showing
//     concretely why a lock is required at all: two requests can each see "capacity available" and
//     both insert, exceeding the resource's capacity.
//  2. CreateBookingCommandHandler tests below that prove the ACTUAL shipped handler, using the real
//     ResourceBookingLock, makes that same interleaving impossible.
public sealed class BookingConcurrencyTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceBookingConcurrencyTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    private Guid _tenantId;
    private Guid _resourceTypeId;
    private Guid _userAId;
    private Guid _userBId;
    private Guid _userCId;
    private Guid _userDId;

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext(Guid.NewGuid());
        await dbContext.Database.EnsureCreatedAsync();

        _tenantId = Guid.NewGuid();
        _resourceTypeId = Guid.NewGuid();
        _userAId = Guid.NewGuid();
        _userBId = Guid.NewGuid();
        _userCId = Guid.NewGuid();
        _userDId = Guid.NewGuid();

        var now = DateTimeOffset.UtcNow;
        dbContext.Tenants.Add(new Tenant { Id = _tenantId, Name = "Concurrency Tenant", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = now });
        dbContext.ResourceTypes.Add(new ResourceType { Id = _resourceTypeId, TenantId = _tenantId, Name = "Meeting Room" });
        dbContext.Users.Add(new User { Id = _userAId, TenantId = _tenantId, FirstName = "User", LastName = "A", Email = "a@concurrency.test", PasswordHash = "x", CreatedAtUtc = now });
        dbContext.Users.Add(new User { Id = _userBId, TenantId = _tenantId, FirstName = "User", LastName = "B", Email = "b@concurrency.test", PasswordHash = "x", CreatedAtUtc = now });
        dbContext.Users.Add(new User { Id = _userCId, TenantId = _tenantId, FirstName = "User", LastName = "C", Email = "c@concurrency.test", PasswordHash = "x", CreatedAtUtc = now });
        dbContext.Users.Add(new User { Id = _userDId, TenantId = _tenantId, FirstName = "User", LastName = "D", Email = "d@concurrency.test", PasswordHash = "x", CreatedAtUtc = now });
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext(Guid.NewGuid());
        await dbContext.Database.EnsureDeletedAsync();
    }

    // Reconstructs the exact race the task describes:
    //     Request A: check -> available
    //     Request B: check -> available
    //     Request A: insert
    //     Request B: insert
    // by having both requests read the current active-booking sum, then wait at a Barrier until BOTH
    // have finished reading, before either is allowed to write. This is not flaky/statistical - the
    // Barrier makes the harmful interleaving happen on every run, which is exactly what makes it a
    // convincing demonstration that "collisions are unlikely" is not a safe assumption.
    [Fact]
    public async Task NaiveCheckThenInsert_RacedByTwoRequests_CanOverbook()
    {
        var resourceId = await SeedResourceAsync(capacity: 1);
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var end = start.AddHours(1);
        using var readBarrier = new Barrier(2);

        async Task<bool> NaiveCreateBookingAsync(Guid userId)
        {
            await using var dbContext = CreateDbContext(_tenantId);

            var activeQuantity = await dbContext.Bookings
                .Where(b => b.ResourceId == resourceId && (b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed))
                .ToListAsync();
            var used = activeQuantity.Where(b => b.StartUtc < end && b.EndUtc > start).Sum(b => b.Quantity);

            // Both requests must finish their read before either proceeds to write - this is the
            // "check -> check -> insert -> insert" ordering, forced deterministically.
            readBarrier.SignalAndWait();

            if (used + 1 > 1 /* capacity */)
            {
                return false;
            }

            dbContext.Bookings.Add(new Booking
            {
                Id = Guid.NewGuid(), TenantId = _tenantId, ResourceId = resourceId, UserId = userId,
                StartUtc = start, EndUtc = end, Quantity = 1, Status = BookingStatus.Confirmed, CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            await dbContext.SaveChangesAsync();
            return true;
        }

        var results = await Task.WhenAll(NaiveCreateBookingAsync(_userAId), NaiveCreateBookingAsync(_userBId));

        // Both requests' read happened before either write, so BOTH believed capacity was available and
        // BOTH inserted - the naive approach lets a resource with Capacity=1 end up with 2 bookings.
        Assert.All(results, succeeded => Assert.True(succeeded));
        await using var verifyContext = CreateDbContext(_tenantId);
        var bookingCount = await verifyContext.Bookings.CountAsync(b => b.ResourceId == resourceId);
        Assert.Equal(2, bookingCount); // > capacity (1) - this is the overbooking the real handler must prevent.
    }

    [Fact]
    public async Task CreateBookingCommandHandler_TwoConcurrentRequestsForCapacityOne_ExactlyOneSucceedsAndOneIsRejected()
    {
        var resourceId = await SeedResourceAsync(capacity: 1);
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var end = start.AddHours(1);
        using var startGate = new SemaphoreSlim(0, 2);

        async Task<CreateBookingResult> AttemptAsync(Guid userId)
        {
            await startGate.WaitAsync();
            var handler = CreateHandler(userId, out var dbContextToDispose);
            try
            {
                var response = await handler.Handle(new CreateBookingCommandRequest(resourceId, start, end, Quantity: 1), CancellationToken.None);
                return new CreateBookingResult(Succeeded: true, Response: response, ErrorCode: null);
            }
            catch (ConflictException exception)
            {
                return new CreateBookingResult(Succeeded: false, Response: null, ErrorCode: exception.ErrorCode);
            }
            finally
            {
                await dbContextToDispose.DisposeAsync();
            }
        }

        var taskA = Task.Run(() => AttemptAsync(_userAId));
        var taskB = Task.Run(() => AttemptAsync(_userBId));
        startGate.Release(2); // both requests now race to acquire the resource lock at the same instant
        var results = await Task.WhenAll(taskA, taskB);

        var winners = results.Where(r => r.Succeeded).ToList();
        var losers = results.Where(r => !r.Succeeded).ToList();
        Assert.Single(winners);
        Assert.Single(losers);

        // The loser doesn't get a generic "lock conflict" error - it waited for the winner's transaction
        // to commit, then re-checked capacity under its own lock and correctly saw the slot was now
        // full, exactly the ordinary business rejection it would get arriving a moment later.
        Assert.Equal("Booking.CapacityExceeded", losers[0].ErrorCode);

        await using var verifyContext = CreateDbContext(_tenantId);
        var bookings = await verifyContext.Bookings.Where(b => b.ResourceId == resourceId).ToListAsync();
        Assert.Single(bookings); // exactly the allowed number - no double-booking, no orphan/partial row.
        Assert.Equal(winners[0].Response!.Id, bookings[0].Id);
    }

    [Fact]
    public async Task CreateBookingCommandHandler_FourConcurrentRequestsForCapacityThree_ExactlyThreeSucceed()
    {
        var resourceId = await SeedResourceAsync(capacity: 3);
        var start = DateTimeOffset.UtcNow.AddDays(2);
        var end = start.AddHours(1);
        var userIds = new[] { _userAId, _userBId, _userCId, _userDId };
        using var startGate = new SemaphoreSlim(0, userIds.Length);

        async Task<bool> AttemptAsync(Guid userId)
        {
            await startGate.WaitAsync();
            var handler = CreateHandler(userId, out var dbContextToDispose);
            try
            {
                await handler.Handle(new CreateBookingCommandRequest(resourceId, start, end, Quantity: 1), CancellationToken.None);
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
            finally
            {
                await dbContextToDispose.DisposeAsync();
            }
        }

        var tasks = userIds.Select(userId => Task.Run(() => AttemptAsync(userId))).ToArray();
        startGate.Release(userIds.Length);
        var results = await Task.WhenAll(tasks);

        Assert.Equal(3, results.Count(succeeded => succeeded));
        Assert.Equal(1, results.Count(succeeded => !succeeded));

        await using var verifyContext = CreateDbContext(_tenantId);
        var bookingCount = await verifyContext.Bookings.CountAsync(b => b.ResourceId == resourceId);
        Assert.Equal(3, bookingCount);
    }

    private async Task<Guid> SeedResourceAsync(int capacity)
    {
        var resourceId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(_tenantId);
        var resource = new Resource
        {
            Id = resourceId, TenantId = _tenantId, ResourceTypeId = _resourceTypeId, Name = $"Concurrency Room {resourceId:N}",
            Capacity = capacity, RequiresApproval = false, Status = ResourceStatus.Active, TimeZoneId = "UTC",
        };
        dbContext.Resources.Add(resource);
        dbContext.AvailabilityRules.AddRange(Enum.GetValues<DayOfWeek>().Select(dayOfWeek => new AvailabilityRule
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, ResourceId = resourceId,
            DayOfWeek = dayOfWeek, StartTime = TimeOnly.MinValue, EndTime = TimeOnly.MaxValue,
        }));
        await dbContext.SaveChangesAsync();
        return resourceId;
    }

    // Each simulated request gets its OWN BookSpaceDbContext, exactly like two real concurrent HTTP
    // requests would each get their own scoped DbContext - the lock/transaction correctness being
    // proven here depends on that being true, not on both requests sharing one connection.
    private CreateBookingCommandHandler CreateHandler(Guid userId, out BookSpaceDbContext dbContext)
    {
        dbContext = CreateDbContext(_tenantId, userId);
        return new CreateBookingCommandHandler(
            new ResourceBookingLock(dbContext, NullLogger<ResourceBookingLock>.Instance),
            new ResourceRepository(dbContext),
            new AvailabilityRuleRepository(dbContext),
            new BlackoutPeriodRepository(dbContext),
            new BookingAvailabilityRepository(dbContext),
            new BookingRepository(dbContext),
            new ResourceApproverRepository(dbContext),
            new ApprovalRequestRepository(dbContext),
            new TenantRepository(dbContext),
            new FixedCurrentUserContext(_tenantId, userId));
    }

    private BookSpaceDbContext CreateDbContext(Guid tenantId, Guid? userId = null)
    {
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>().UseSqlServer(_connectionString).Options;
        return new BookSpaceDbContext(options, new FixedCurrentUserContext(tenantId, userId));
    }

    private sealed record CreateBookingResult(bool Succeeded, CreateBookingResponse? Response, string? ErrorCode);

    private sealed class FixedCurrentUserContext(Guid tenantId, Guid? userId = null) : ICurrentUserContext
    {
        public Guid? UserId => userId;
        public Guid? TenantId => tenantId;
        public IReadOnlyCollection<string> Roles => [];
    }
}
