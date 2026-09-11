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

// Proves ApproveBookingCommandHandler's use of IResourceBookingLock against real SQL Server (LocalDB) -
// required, since SQLite has no UPDLOCK/HOLDLOCK support and the lock is a no-op there. See
// docs/recurring-bookings-and-approvals.md.
//
// Note on what this test class does NOT (and does not need to) prove: "another booking takes the slot
// while the first is Pending" cannot actually happen in this codebase's design, because Pending already
// fully consumes capacity identically to Confirmed from the moment of creation (see
// docs/bookings-and-concurrency.md) - a second booking racing to be created for the same window would
// already be rejected by CreateBookingCommandHandler's own capacity check, whether or not the first is
// later approved. What genuinely requires real concurrency to prove is that concurrent DECISIONS on the
// resource's Bookings correctly serialize and never corrupt the capacity invariant or double-process a
// single decision - that is what these tests exercise.
public sealed class ApprovalConcurrencyTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceApprovalConcurrencyTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    private Guid _tenantId;
    private Guid _resourceTypeId;
    private Guid _memberUserId;
    private Guid _approverUserId;
    private Guid _secondBookingOwnerId;

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext(Guid.NewGuid());
        await dbContext.Database.EnsureCreatedAsync();

        _tenantId = Guid.NewGuid();
        _resourceTypeId = Guid.NewGuid();
        _memberUserId = Guid.NewGuid();
        _approverUserId = Guid.NewGuid();
        _secondBookingOwnerId = Guid.NewGuid();

        var now = DateTimeOffset.UtcNow;
        dbContext.Tenants.Add(new Tenant { Id = _tenantId, Name = "Approval Concurrency Tenant", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = now });
        dbContext.ResourceTypes.Add(new ResourceType { Id = _resourceTypeId, TenantId = _tenantId, Name = "Meeting Room" });
        dbContext.Users.AddRange(
            new User { Id = _memberUserId, TenantId = _tenantId, FirstName = "Member", LastName = "User", Email = "member@approval-concurrency.test", PasswordHash = "x", CreatedAtUtc = now },
            new User { Id = _approverUserId, TenantId = _tenantId, FirstName = "Approver", LastName = "User", Email = "approver@approval-concurrency.test", PasswordHash = "x", CreatedAtUtc = now },
            new User { Id = _secondBookingOwnerId, TenantId = _tenantId, FirstName = "Second", LastName = "Owner", Email = "second@approval-concurrency.test", PasswordHash = "x", CreatedAtUtc = now });
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext(Guid.NewGuid());
        await dbContext.Database.EnsureDeletedAsync();
    }

    [Fact]
    public async Task TwoConcurrentApproveRequestsForTheSameBooking_ExactlyOneSucceedsAndTheOtherIsRejectedCleanly()
    {
        var resourceId = await SeedResourceAsync(capacity: 4, requiresApproval: true);
        var (bookingId, approvalRequestId) = await SeedPendingBookingAsync(resourceId, _memberUserId, quantity: 1);
        await SeedResourceApproverAsync(resourceId, _approverUserId);

        using var startGate = new SemaphoreSlim(0, 2);

        async Task<bool> ApproveAsync()
        {
            await startGate.WaitAsync();
            await using var dbContext = CreateDbContext(_tenantId, _approverUserId);
            var handler = CreateApproveHandler(dbContext, _approverUserId);
            try
            {
                await handler.Handle(new ApproveBookingCommandRequest(bookingId, null), CancellationToken.None);
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
        }

        var taskA = Task.Run(ApproveAsync);
        var taskB = Task.Run(ApproveAsync);
        startGate.Release(2);
        var results = await Task.WhenAll(taskA, taskB);

        Assert.Single(results, succeeded => succeeded);
        Assert.Single(results, succeeded => !succeeded);

        await using var verifyContext = CreateDbContext(_tenantId);
        var finalBooking = await verifyContext.Bookings.SingleAsync(b => b.Id == bookingId);
        Assert.Equal(BookingStatus.Confirmed, finalBooking.Status); // decided exactly once, never left ambiguous
        var finalApproval = await verifyContext.ApprovalRequests.SingleAsync(a => a.Id == approvalRequestId);
        Assert.Equal(ApprovalStatus.Approved, finalApproval.Status);
    }

    [Fact]
    public async Task ApprovingAPendingBooking_ConcurrentlyWithACreateRequestForTheSameFullyBookedWindow_BothSerializeCorrectly()
    {
        // Capacity=1, already fully committed to the one Pending booking. A concurrent create attempt
        // for the same window can never fit regardless of whether the Pending booking gets approved
        // first, second, or races exactly alongside it - Pending already counts as full demand. This
        // proves the two operations serialize through the same lock without deadlocking or corrupting
        // the invariant, not that either can "win" a slot the other legitimately holds.
        var resourceId = await SeedResourceAsync(capacity: 1, requiresApproval: true);
        var (bookingId, _) = await SeedPendingBookingAsync(resourceId, _memberUserId, quantity: 1);
        await SeedResourceApproverAsync(resourceId, _approverUserId);
        var (windowStart, windowEnd) = await GetBookingWindowAsync(bookingId);

        using var startGate = new SemaphoreSlim(0, 2);

        async Task<bool> ApproveAsync()
        {
            await startGate.WaitAsync();
            await using var dbContext = CreateDbContext(_tenantId, _approverUserId);
            var handler = CreateApproveHandler(dbContext, _approverUserId);
            try
            {
                await handler.Handle(new ApproveBookingCommandRequest(bookingId, null), CancellationToken.None);
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
            await using var dbContext = CreateDbContext(_tenantId, _secondBookingOwnerId);
            var handler = CreateCreateHandler(dbContext, _secondBookingOwnerId);
            try
            {
                await handler.Handle(new CreateBookingCommandRequest(resourceId, windowStart, windowEnd, Quantity: 1), CancellationToken.None);
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
        }

        var approveTask = Task.Run(ApproveAsync);
        var createTask = Task.Run(CreateOverlappingBookingAsync);
        startGate.Release(2);
        await Task.WhenAll(approveTask, createTask);

        // The Pending booking's approval must succeed (nothing legitimately invalidated it), and the
        // new concurrent booking attempt must be rejected (no capacity for a second unit) - regardless
        // of which one the lock happened to run first.
        Assert.True(await approveTask);
        Assert.False(await createTask);

        await using var verifyContext = CreateDbContext(_tenantId);
        var committedDemand = await verifyContext.Bookings
            .Where(b => b.ResourceId == resourceId && (b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed))
            .SumAsync(b => b.Quantity);
        Assert.Equal(1, committedDemand); // exactly the allowed capacity - no double-booking, no orphan row
    }

    // RejectBookingCommandHandler deliberately does NOT acquire IResourceBookingLock (docs/recurring-
    // bookings-and-approvals.md §7 - "moving OUT of Pending can only reduce demand, never invalidate the
    // capacity invariant"), which is true for capacity. This test checks a DIFFERENT invariant that
    // reasoning doesn't cover: can the SAME booking be decided twice by two different, simultaneous
    // decisions (one Approve, one Reject) racing each other? Booking also has no RowVersion column (by
    // design - see docs/optimistic-concurrency.md - Cancel's idempotency was the reason given). Approve
    // is lock-protected; Reject is not - so unlike the Approve-vs-Approve test above, these two do not
    // both funnel through the same serialization point.
    [Fact]
    public async Task ConcurrentApproveAndRejectOnTheSameBooking_ExactlyOneDecisionWinsCleanly()
    {
        var resourceId = await SeedResourceAsync(capacity: 4, requiresApproval: true);
        var (bookingId, approvalRequestId) = await SeedPendingBookingAsync(resourceId, _memberUserId, quantity: 1);
        await SeedResourceApproverAsync(resourceId, _approverUserId);

        using var startGate = new SemaphoreSlim(0, 2);

        async Task<bool> ApproveAsync()
        {
            await startGate.WaitAsync();
            await using var dbContext = CreateDbContext(_tenantId, _approverUserId);
            var handler = CreateApproveHandler(dbContext, _approverUserId);
            try
            {
                await handler.Handle(new ApproveBookingCommandRequest(bookingId, null), CancellationToken.None);
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
        }

        async Task<bool> RejectAsync()
        {
            await startGate.WaitAsync();
            await using var dbContext = CreateDbContext(_tenantId, _approverUserId);
            var handler = CreateRejectHandler(dbContext, _approverUserId);
            try
            {
                await handler.Handle(new RejectBookingCommandRequest(bookingId, null), CancellationToken.None);
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
        }

        var approveTask = Task.Run(ApproveAsync);
        var rejectTask = Task.Run(RejectAsync);
        startGate.Release(2);
        var results = await Task.WhenAll(approveTask, rejectTask);

        // The invariant this test checks: a Pending booking must be decided exactly once, never both
        // ways and never left ambiguous, regardless of which decision a caller happened to send first.
        Assert.Single(results, succeeded => succeeded);
        Assert.Single(results, succeeded => !succeeded);

        await using var verifyContext = CreateDbContext(_tenantId);
        var finalBooking = await verifyContext.Bookings.SingleAsync(b => b.Id == bookingId);
        var finalApproval = await verifyContext.ApprovalRequests.SingleAsync(a => a.Id == approvalRequestId);
        Assert.NotEqual(BookingStatus.Pending, finalBooking.Status);
        // Booking and ApprovalRequest must always agree on which decision actually won.
        Assert.Equal(
            finalBooking.Status == BookingStatus.Confirmed ? ApprovalStatus.Approved : ApprovalStatus.Rejected,
            finalApproval.Status);
    }

    private RejectBookingCommandHandler CreateRejectHandler(BookSpaceDbContext dbContext, Guid userId) => new(
        new ResourceBookingLock(dbContext, NullLogger<ResourceBookingLock>.Instance),
        new BookingRepository(dbContext),
        new ApprovalRequestRepository(dbContext),
        new ResourceApproverRepository(dbContext),
        new FixedCurrentUserContext(_tenantId, userId));

    private async Task<Guid> SeedResourceAsync(int capacity, bool requiresApproval)
    {
        var resourceId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(_tenantId);
        dbContext.Resources.Add(new Resource
        {
            Id = resourceId, TenantId = _tenantId, ResourceTypeId = _resourceTypeId, Name = $"Approval Room {resourceId:N}",
            Capacity = capacity, RequiresApproval = requiresApproval, Status = ResourceStatus.Active, TimeZoneId = "UTC",
        });
        dbContext.AvailabilityRules.AddRange(Enum.GetValues<DayOfWeek>().Select(dayOfWeek => new AvailabilityRule
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, ResourceId = resourceId,
            DayOfWeek = dayOfWeek, StartTime = TimeOnly.MinValue, EndTime = TimeOnly.MaxValue,
        }));
        await dbContext.SaveChangesAsync();
        return resourceId;
    }

    private async Task SeedResourceApproverAsync(Guid resourceId, Guid approverUserId)
    {
        await using var dbContext = CreateDbContext(_tenantId);
        dbContext.ResourceApprovers.Add(new ResourceApprover { Id = Guid.NewGuid(), TenantId = _tenantId, ResourceId = resourceId, UserId = approverUserId });
        await dbContext.SaveChangesAsync();
    }

    private async Task<(Guid BookingId, Guid ApprovalRequestId)> SeedPendingBookingAsync(Guid resourceId, Guid ownerUserId, int quantity)
    {
        await using var dbContext = CreateDbContext(_tenantId);
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var booking = new Booking
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, ResourceId = resourceId, UserId = ownerUserId,
            StartUtc = start, EndUtc = start.AddHours(1), Quantity = quantity, Status = BookingStatus.Pending, CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        var approvalRequest = new ApprovalRequest
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, BookingId = booking.Id, ApproverId = null,
            Status = ApprovalStatus.Pending, RequestedAtUtc = DateTimeOffset.UtcNow, ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(48),
        };
        dbContext.Bookings.Add(booking);
        dbContext.ApprovalRequests.Add(approvalRequest);
        await dbContext.SaveChangesAsync();
        return (booking.Id, approvalRequest.Id);
    }

    private async Task<(DateTimeOffset Start, DateTimeOffset End)> GetBookingWindowAsync(Guid bookingId)
    {
        await using var dbContext = CreateDbContext(_tenantId);
        var booking = await dbContext.Bookings.SingleAsync(b => b.Id == bookingId);
        return (booking.StartUtc, booking.EndUtc);
    }

    // FixedCurrentUserContext is stateless given (tenantId, userId), so constructing a second equal
    // instance here (independent from whichever one the DbContext itself was built with) is harmless -
    // simpler than trying to recover it from the DbContext afterward.
    private ApproveBookingCommandHandler CreateApproveHandler(BookSpaceDbContext dbContext, Guid userId) => new(
        new ResourceBookingLock(dbContext, NullLogger<ResourceBookingLock>.Instance),
        new BookingRepository(dbContext),
        new ResourceRepository(dbContext),
        new AvailabilityRuleRepository(dbContext),
        new BlackoutPeriodRepository(dbContext),
        new BookingAvailabilityRepository(dbContext),
        new ApprovalRequestRepository(dbContext),
        new ResourceApproverRepository(dbContext),
        new FixedCurrentUserContext(_tenantId, userId));

    private CreateBookingCommandHandler CreateCreateHandler(BookSpaceDbContext dbContext, Guid userId) => new(
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
