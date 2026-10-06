using BookSpace.Application.Bookings;
using BookSpace.Application.Common;
using BookSpace.Application.Notifications;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// Proves, against real SQL Server (LocalDB), that CheckInBookingCommandHandler and
// CancelBookingCommandHandler now share IResourceBookingLock and can never leave a booking in a
// contradictory state (Cancelled while also bearing a CheckedInAtUtc value, or vice versa) - the exact gap
// identified after the original check-in task shipped: Cancel used to never acquire the lock at all, so a
// check-in and a cancel racing on the same booking could both read Confirmed before either committed and
// both "succeed". See docs/bookings-and-concurrency.md ("Booking check-in - the check-in/cancel race") for
// the full write-up of both documented race outcomes this file proves.
public sealed class CheckInVsCancelConcurrencyTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceCheckInVsCancelTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    private Guid _tenantId;
    private Guid _resourceTypeId;
    private Guid _memberUserId;

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext(Guid.NewGuid());
        await dbContext.Database.EnsureCreatedAsync();

        _tenantId = Guid.NewGuid();
        _resourceTypeId = Guid.NewGuid();
        _memberUserId = Guid.NewGuid();

        var now = DateTimeOffset.UtcNow;
        dbContext.Tenants.Add(new Tenant
        {
            Id = _tenantId, Name = "CheckIn Vs Cancel Tenant", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active,
            CreatedAtUtc = now, CheckInLeadMinutes = 15, NoShowGraceMinutes = 15,
        });
        dbContext.ResourceTypes.Add(new ResourceType { Id = _resourceTypeId, TenantId = _tenantId, Name = "Meeting Room" });
        dbContext.Users.Add(new User
        {
            Id = _memberUserId, TenantId = _tenantId, FirstName = "Member", LastName = "User",
            Email = "member@checkin-vs-cancel.test", PasswordHash = "x", CreatedAtUtc = now,
        });
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext(Guid.NewGuid());
        await dbContext.Database.EnsureDeletedAsync();
    }

    // Documented rule 1: cancel wins -> booking Cancelled, CheckedInAtUtc stays null, a subsequent check-in
    // sees Status != Confirmed and is refused by CheckInEligibilityChecker's existing NotConfirmed case -
    // no new rule was needed on the check-in side for this ordering, since both handlers now genuinely
    // serialize through the same lock.
    [Fact]
    public async Task CancelCommittingFirst_LeavesTheBookingCancelledAndCheckInIsThenRefused()
    {
        var resourceId = await SeedResourceAsync();
        var bookingId = await SeedConfirmedBookingWithinWindowAsync(resourceId);

        await using (var cancelContext = CreateDbContext(_tenantId, _memberUserId))
        {
            var cancelResult = await CreateCancelHandler(cancelContext).Handle(new CancelBookingCommandRequest(bookingId), CancellationToken.None);
            Assert.Equal(BookingStatus.Cancelled, cancelResult.Status);
        }

        await using var checkInContext = CreateDbContext(_tenantId, _memberUserId);
        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            CreateCheckInHandler(checkInContext).Handle(new CheckInBookingCommandRequest(bookingId), CancellationToken.None));
        Assert.Equal("Booking.CheckInNotAllowed", exception.ErrorCode);

        await using var verify = CreateDbContext(_tenantId);
        var finalBooking = await verify.Bookings.SingleAsync(b => b.Id == bookingId);
        Assert.Equal(BookingStatus.Cancelled, finalBooking.Status);
        Assert.Null(finalBooking.CheckedInAtUtc);
    }

    // Documented rule 2: check-in wins -> CheckedInAtUtc is set, booking stays Confirmed, a subsequent
    // member cancel attempt is refused with Booking.AlreadyCheckedIn and CheckedInAtUtc is never cleared.
    [Fact]
    public async Task CheckInCommittingFirst_LeavesCheckedInAtUtcSetAndCancelIsThenRefusedWithAlreadyCheckedIn()
    {
        var resourceId = await SeedResourceAsync();
        var bookingId = await SeedConfirmedBookingWithinWindowAsync(resourceId);

        DateTimeOffset checkedInAtUtc;
        await using (var checkInContext = CreateDbContext(_tenantId, _memberUserId))
        {
            var checkInResult = await CreateCheckInHandler(checkInContext).Handle(new CheckInBookingCommandRequest(bookingId), CancellationToken.None);
            Assert.False(checkInResult.AlreadyCheckedIn);
            checkedInAtUtc = checkInResult.CheckedInAtUtc;
        }

        await using var cancelContext = CreateDbContext(_tenantId, _memberUserId);
        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            CreateCancelHandler(cancelContext).Handle(new CancelBookingCommandRequest(bookingId), CancellationToken.None));
        Assert.Equal("Booking.AlreadyCheckedIn", exception.ErrorCode);

        await using var verify = CreateDbContext(_tenantId);
        var finalBooking = await verify.Bookings.SingleAsync(b => b.Id == bookingId);
        Assert.Equal(BookingStatus.Confirmed, finalBooking.Status); // never cancelled
        Assert.Equal(checkedInAtUtc, finalBooking.CheckedInAtUtc); // never cleared or changed
    }

    // The genuine race: both fired truly concurrently via a shared start gate. Regardless of which one the
    // lock happens to let through first, the booking must end in EXACTLY ONE of the two valid states -
    // never both Cancelled AND bearing a CheckedInAtUtc value - and the loser's own response/exception must
    // agree with whichever state actually won.
    [Fact]
    public async Task ConcurrentCheckInAndCancel_NeverProduceAContradictoryFinalState()
    {
        var resourceId = await SeedResourceAsync();
        var bookingId = await SeedConfirmedBookingWithinWindowAsync(resourceId);

        using var startGate = new SemaphoreSlim(0, 2);

        async Task<(bool Succeeded, string? ErrorCode)> CheckInAsync()
        {
            await startGate.WaitAsync();
            await using var dbContext = CreateDbContext(_tenantId, _memberUserId);
            try
            {
                await CreateCheckInHandler(dbContext).Handle(new CheckInBookingCommandRequest(bookingId), CancellationToken.None);
                return (true, null);
            }
            catch (ConflictException exception)
            {
                return (false, exception.ErrorCode);
            }
        }

        async Task<(bool Succeeded, string? ErrorCode)> CancelAsync()
        {
            await startGate.WaitAsync();
            await using var dbContext = CreateDbContext(_tenantId, _memberUserId);
            try
            {
                await CreateCancelHandler(dbContext).Handle(new CancelBookingCommandRequest(bookingId), CancellationToken.None);
                return (true, null);
            }
            catch (ConflictException exception)
            {
                return (false, exception.ErrorCode);
            }
        }

        var checkInTask = Task.Run(CheckInAsync);
        var cancelTask = Task.Run(CancelAsync);
        startGate.Release(2);
        var (checkInSucceeded, checkInError) = await checkInTask;
        var (cancelSucceeded, cancelError) = await cancelTask;

        await using var verify = CreateDbContext(_tenantId);
        var finalBooking = await verify.Bookings.SingleAsync(b => b.Id == bookingId);

        // Never a hybrid state - the one invariant this whole task exists to close.
        Assert.False(finalBooking.Status == BookingStatus.Cancelled && finalBooking.CheckedInAtUtc is not null);

        if (finalBooking.Status == BookingStatus.Cancelled)
        {
            Assert.True(cancelSucceeded);
            Assert.False(checkInSucceeded);
            Assert.Equal("Booking.CheckInNotAllowed", checkInError);
            Assert.Null(finalBooking.CheckedInAtUtc);
        }
        else
        {
            Assert.Equal(BookingStatus.Confirmed, finalBooking.Status);
            Assert.NotNull(finalBooking.CheckedInAtUtc);
            Assert.True(checkInSucceeded);
            Assert.False(cancelSucceeded);
            Assert.Equal("Booking.AlreadyCheckedIn", cancelError);
        }
    }

    // "Dva paralelna cancel zahtjeva ne proizvode duple notification outbox stavke": two genuinely
    // concurrent cancel requests for the SAME booking - exactly one outbox row, both calls report Cancelled
    // (the second finds it already Cancelled and is a no-op, per Cancel's pre-existing idempotency).
    [Fact]
    public async Task TwoConcurrentCancelRequestsForTheSameBooking_ProduceExactlyOneCancellationOutboxItem()
    {
        var resourceId = await SeedResourceAsync();
        var bookingId = await SeedConfirmedBookingWithinWindowAsync(resourceId);

        using var startGate = new SemaphoreSlim(0, 2);

        async Task<BookingStatus> CancelAsync()
        {
            await startGate.WaitAsync();
            await using var dbContext = CreateDbContext(_tenantId, _memberUserId);
            var result = await CreateCancelHandler(dbContext).Handle(new CancelBookingCommandRequest(bookingId), CancellationToken.None);
            return result.Status;
        }

        var taskA = Task.Run(CancelAsync);
        var taskB = Task.Run(CancelAsync);
        startGate.Release(2);
        var results = await Task.WhenAll(taskA, taskB);

        Assert.All(results, status => Assert.Equal(BookingStatus.Cancelled, status));

        await using var verify = CreateDbContext(_tenantId);
        var outboxCount = await verify.NotificationOutboxItems.IgnoreQueryFilters()
            .CountAsync(i => i.IdempotencyKey == $"booking:{bookingId}:cancellation");
        Assert.Equal(1, outboxCount);
    }

    // "Check-in protiv cancel-rest-of-series": a check-in on a LATER sibling occurrence races against a
    // cancel-remaining-series call targeting the PRIMARY occurrence (which would otherwise cascade-cancel
    // that very sibling). No partial cascade change, no state violating the documented rules, and the
    // outbox stays consistent with whichever final booking statuses actually resulted.
    [Fact]
    public async Task CheckInOnASiblingOccurrence_RacingWithCancelRemainingSeries_NeverProducesAnInconsistentCascade()
    {
        var resourceId = await SeedResourceAsync();
        var seriesId = await SeedSeriesAsync(resourceId);
        var anchor = DateTimeOffset.UtcNow; // primary occurrence starts now - within its own check-in window too
        var primaryId = await SeedConfirmedOccurrenceAsync(resourceId, seriesId, anchor);
        var siblingId = await SeedConfirmedOccurrenceAsync(resourceId, seriesId, anchor.AddMinutes(1));

        using var startGate = new SemaphoreSlim(0, 2);

        async Task CheckInSiblingAsync()
        {
            await startGate.WaitAsync();
            await using var dbContext = CreateDbContext(_tenantId, _memberUserId);
            try
            {
                await CreateCheckInHandler(dbContext).Handle(new CheckInBookingCommandRequest(siblingId), CancellationToken.None);
            }
            catch (ConflictException)
            {
                // Acceptable if the cascade's cancellation of the sibling already won the lock race first -
                // this method's own assertions below are what actually judge correctness, not this catch.
            }
        }

        async Task CancelSeriesAsync()
        {
            await startGate.WaitAsync();
            await using var dbContext = CreateDbContext(_tenantId, _memberUserId);
            await CreateCancelHandler(dbContext).Handle(new CancelBookingCommandRequest(primaryId, CancelRemainingSeries: true), CancellationToken.None);
        }

        var checkInTask = Task.Run(CheckInSiblingAsync);
        var cancelTask = Task.Run(CancelSeriesAsync);
        startGate.Release(2);
        await Task.WhenAll(checkInTask, cancelTask);

        await using var verify = CreateDbContext(_tenantId);
        var primary = await verify.Bookings.SingleAsync(b => b.Id == primaryId);
        var sibling = await verify.Bookings.SingleAsync(b => b.Id == siblingId);

        // The primary is always cancelled - CancelRemainingSeries never touches (or needs to touch)
        // check-in state for the primary itself here.
        Assert.Equal(BookingStatus.Cancelled, primary.Status);

        // The sibling ends in exactly one of two valid states - never a hybrid.
        Assert.False(sibling.Status == BookingStatus.Cancelled && sibling.CheckedInAtUtc is not null);

        var siblingCancellationOutboxCount = await verify.NotificationOutboxItems.IgnoreQueryFilters()
            .CountAsync(i => i.IdempotencyKey == $"booking:{siblingId}:cancellation");

        if (sibling.Status == BookingStatus.Cancelled)
        {
            Assert.Null(sibling.CheckedInAtUtc);
            Assert.Equal(1, siblingCancellationOutboxCount); // cascaded and notified
        }
        else
        {
            Assert.Equal(BookingStatus.Confirmed, sibling.Status);
            Assert.NotNull(sibling.CheckedInAtUtc);
            Assert.Equal(0, siblingCancellationOutboxCount); // correctly excluded from the cascade, never notified
        }
    }

    // Test matrix item 5 (the CheckedInAtUtc/status half - Cancel's own booking-status+outbox rollback is
    // already proven pre-existing by NotificationOutboxCascadeAtomicityTests, still green after this
    // change): a failure injected at the exact point SaveChangesAsync would run during check-in must leave
    // NEITHER Status NOR CheckedInAtUtc persisted - read back from a completely separate connection.
    [Fact]
    public async Task CheckIn_WhenSaveChangesFailsBeforeCommit_PersistsNeitherTheCheckInNorAnyStatusChange()
    {
        var resourceId = await SeedResourceAsync();
        var bookingId = await SeedConfirmedBookingWithinWindowAsync(resourceId);

        var throwingInterceptor = new ThrowingSaveChangesInterceptor { ShouldThrow = true };
        await using var dbContext = CreateDbContext(_tenantId, _memberUserId, throwingInterceptor);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateCheckInHandler(dbContext).Handle(new CheckInBookingCommandRequest(bookingId), CancellationToken.None));

        await using var verify = CreateDbContext(_tenantId);
        var finalBooking = await verify.Bookings.SingleAsync(b => b.Id == bookingId);
        Assert.Equal(BookingStatus.Confirmed, finalBooking.Status);
        Assert.Null(finalBooking.CheckedInAtUtc);
    }

    // Test matrix item 6: an already-cancelled token must propagate as a real cancellation, never get
    // translated into a ConflictException/NotFoundException business outcome.
    [Fact]
    public async Task Cancel_WithAnAlreadyCancelledToken_PropagatesCancellationRatherThanABusinessConflict()
    {
        var resourceId = await SeedResourceAsync();
        var bookingId = await SeedConfirmedBookingWithinWindowAsync(resourceId);

        await using var dbContext = CreateDbContext(_tenantId, _memberUserId);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateCancelHandler(dbContext).Handle(new CancelBookingCommandRequest(bookingId), cts.Token));
    }

    private async Task<Guid> SeedResourceAsync()
    {
        var resourceId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(_tenantId);
        dbContext.Resources.Add(new Resource
        {
            Id = resourceId, TenantId = _tenantId, ResourceTypeId = _resourceTypeId, Name = $"CheckIn Vs Cancel Room {resourceId:N}",
            Capacity = 4, RequiresApproval = false, Status = ResourceStatus.Active, TimeZoneId = "UTC",
        });
        await dbContext.SaveChangesAsync();
        return resourceId;
    }

    private async Task<Guid> SeedSeriesAsync(Guid resourceId)
    {
        var seriesId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(_tenantId);
        dbContext.RecurringSeries.Add(new RecurringSeries
        {
            Id = seriesId, TenantId = _tenantId, ResourceId = resourceId, UserId = _memberUserId,
            StartDate = DateOnly.FromDateTime(DateTime.UtcNow), StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0),
            TimeZoneId = "UTC", Frequency = RecurrenceFrequency.Daily, Interval = 1, OccurrenceCount = 2, Quantity = 1,
        });
        await dbContext.SaveChangesAsync();
        return seriesId;
    }

    private async Task<Guid> SeedConfirmedBookingWithinWindowAsync(Guid resourceId)
    {
        await using var dbContext = CreateDbContext(_tenantId);
        var start = DateTimeOffset.UtcNow; // window opens -15m/closes +15m from start - "now" is inside it
        var booking = new Booking
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, ResourceId = resourceId, UserId = _memberUserId,
            StartUtc = start, EndUtc = start.AddHours(1), Quantity = 1, Status = BookingStatus.Confirmed, CreatedAtUtc = start.AddDays(-1),
        };
        dbContext.Bookings.Add(booking);
        await dbContext.SaveChangesAsync();
        return booking.Id;
    }

    private async Task<Guid> SeedConfirmedOccurrenceAsync(Guid resourceId, Guid seriesId, DateTimeOffset start)
    {
        await using var dbContext = CreateDbContext(_tenantId);
        var booking = new Booking
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, ResourceId = resourceId, UserId = _memberUserId, SeriesId = seriesId,
            StartUtc = start, EndUtc = start.AddHours(1), Quantity = 1, Status = BookingStatus.Confirmed, CreatedAtUtc = start.AddDays(-1),
        };
        dbContext.Bookings.Add(booking);
        await dbContext.SaveChangesAsync();
        return booking.Id;
    }

    private CheckInBookingCommandHandler CreateCheckInHandler(BookSpaceDbContext dbContext) => new(
        new ResourceBookingLock(dbContext, NullLogger<ResourceBookingLock>.Instance),
        new BookingRepository(dbContext),
        new TenantRepository(dbContext),
        new ResourceApproverRepository(dbContext),
        new FixedCurrentUserContext(_tenantId, _memberUserId));

    private CancelBookingCommandHandler CreateCancelHandler(BookSpaceDbContext dbContext) => new(
        new ResourceBookingLock(dbContext, NullLogger<ResourceBookingLock>.Instance),
        new BookingRepository(dbContext),
        new FixedCurrentUserContext(_tenantId, _memberUserId),
        new NotificationOutboxWriter(dbContext, NullLogger<NotificationOutboxWriter>.Instance));

    private BookSpaceDbContext CreateDbContext(Guid tenantId, Guid? userId = null, params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<BookSpaceDbContext>().UseSqlServer(_connectionString);
        if (interceptors.Length > 0)
        {
            builder.AddInterceptors(interceptors);
        }

        return new BookSpaceDbContext(builder.Options, new FixedCurrentUserContext(tenantId, userId));
    }

    private sealed class FixedCurrentUserContext(Guid tenantId, Guid? userId = null) : ICurrentUserContext
    {
        public Guid? UserId => userId;
        public Guid? TenantId => tenantId;
        public IReadOnlyCollection<string> Roles => [];
    }

    private sealed class ThrowingSaveChangesInterceptor : SaveChangesInterceptor
    {
        public bool ShouldThrow { get; set; }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) =>
            ShouldThrow ? throw new InvalidOperationException("Simulated failure injected before commit.") : base.SavingChanges(eventData, result);

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            ShouldThrow
                ? throw new InvalidOperationException("Simulated failure injected before commit.")
                : base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
