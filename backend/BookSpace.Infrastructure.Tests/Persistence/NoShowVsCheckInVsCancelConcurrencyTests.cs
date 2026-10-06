using BookSpace.Application.BackgroundJobs;
using BookSpace.Application.Bookings;
using BookSpace.Application.Common;
using BookSpace.Application.Notifications;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// Proves, against real SQL Server (LocalDB), that NoShowProcessor shares IResourceBookingLock with
// CheckInBookingCommandHandler and CancelBookingCommandHandler (the same lock-sharing protocol the
// check-in/cancel race fix established - see docs/bookings-and-concurrency.md, "The check-in/cancel race -
// now closed") and can never leave a booking in a contradictory state. This is the genuine, cross-component
// three-way race the no-show task itself calls out: an HTTP check-in or cancel request racing against the
// background no-show processor for the SAME booking.
public sealed class NoShowVsCheckInVsCancelConcurrencyTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceNoShowVsCheckInVsCancelTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

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
            Id = _tenantId, Name = "NoShow Vs CheckIn Vs Cancel Tenant", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active,
            CreatedAtUtc = now, CheckInLeadMinutes = 15, NoShowGraceMinutes = 15,
        });
        dbContext.ResourceTypes.Add(new ResourceType { Id = _resourceTypeId, TenantId = _tenantId, Name = "Meeting Room" });
        dbContext.Users.Add(new User
        {
            Id = _memberUserId, TenantId = _tenantId, FirstName = "Member", LastName = "User",
            Email = "member@noshow-vs-checkin-vs-cancel.test", PasswordHash = "x", CreatedAtUtc = now,
        });
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext(Guid.NewGuid());
        await dbContext.Database.EnsureDeletedAsync();
    }

    // Documented rule: check-in wins -> the no-show processor's re-read under the lock sees CheckedInAtUtc
    // already set and skips the booking entirely - never marks it NoShow, never enqueues a notification.
    //
    // The booking is seeded STILL WITHIN its check-in window (not yet past the shared deadline) so the real
    // check-in call can genuinely succeed - by design, check-in's closing boundary and no-show's own due
    // boundary are the exact SAME instant (StartUtc + NoShowGraceMinutes), so a booking actually past that
    // deadline would correctly refuse check-in too (see CheckInCommittingAfterTheWindowClosed_IsRefused
    // below for that already-correct behavior). The no-show processor is driven by a FixedCandidateReader
    // forcing THIS exact booking through, simulating the processor's due-query having found it a moment
    // before check-in committed - the same technique NoShowProcessorTests uses to isolate the processor's
    // own re-check from the reader's natural due-filtering.
    [Fact]
    public async Task CheckInCommittingFirst_LeavesTheNoShowProcessorSkippingTheBooking()
    {
        var resourceId = await SeedResourceAsync();
        var bookingId = await SeedConfirmedBookingWithinCheckInWindowAsync(resourceId);

        DateTimeOffset checkedInAtUtc;
        await using (var checkInContext = CreateDbContext(_tenantId, _memberUserId))
        {
            var checkInResult = await CreateCheckInHandler(checkInContext).Handle(new CheckInBookingCommandRequest(bookingId), CancellationToken.None);
            Assert.False(checkInResult.AlreadyCheckedIn);
            checkedInAtUtc = checkInResult.CheckedInAtUtc;
        }

        var staleCandidate = new DueNoShowBooking(bookingId, _tenantId, resourceId, _memberUserId, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(55), 1);
        var result = await CreateNoShowProcessor(new FixedCandidateReader(staleCandidate)).ProcessDueNoShowsAsync(CancellationToken.None);

        Assert.Equal(1, result.CheckedInCount);
        Assert.Equal(0, result.MarkedNoShowCount);

        await using var verify = CreateDbContext(_tenantId);
        var finalBooking = await verify.Bookings.SingleAsync(b => b.Id == bookingId);
        Assert.Equal(BookingStatus.Confirmed, finalBooking.Status);
        Assert.Equal(checkedInAtUtc, finalBooking.CheckedInAtUtc);
        Assert.Empty(await verify.NotificationOutboxItems.IgnoreQueryFilters().Where(i => i.NotificationType == BookingNotificationTypes.NoShow).ToListAsync());
    }

    // Documented rule: no-show wins -> Status becomes NoShow; a subsequent check-in attempt re-reads under
    // its own lock acquisition, sees Status != Confirmed, and is refused by the SAME existing
    // Booking.CheckInNotAllowed conflict the check-in/cancel race fix already relies on - no new rule was
    // needed on the check-in side for this ordering either.
    [Fact]
    public async Task NoShowProcessorCommittingFirst_LeavesCheckInRefused()
    {
        var resourceId = await SeedResourceAsync();
        var bookingId = await SeedOverdueConfirmedBookingAsync(resourceId);

        var noShowResult = await CreateNoShowProcessor().ProcessDueNoShowsAsync(CancellationToken.None);
        Assert.Equal(1, noShowResult.MarkedNoShowCount);

        await using var checkInContext = CreateDbContext(_tenantId, _memberUserId);
        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            CreateCheckInHandler(checkInContext).Handle(new CheckInBookingCommandRequest(bookingId), CancellationToken.None));
        Assert.Equal("Booking.CheckInNotAllowed", exception.ErrorCode);

        await using var verify = CreateDbContext(_tenantId);
        var finalBooking = await verify.Bookings.SingleAsync(b => b.Id == bookingId);
        Assert.Equal(BookingStatus.NoShow, finalBooking.Status);
        Assert.Null(finalBooking.CheckedInAtUtc);
    }

    // Documented rule: cancel wins -> Status becomes Cancelled; the no-show processor's re-read under the
    // lock sees Status != Confirmed and skips the booking - never marks it NoShow, never enqueues a
    // notification, never overwrites the cancellation. A FixedCandidateReader forces the booking through
    // for the same reason as the check-in test above: cancel has no time-window restriction of its own, but
    // using the REAL reader here would simply find zero candidates (a cancelled booking is never Confirmed),
    // which would prove the READER's own filtering (already covered by NoShowReaderTests), not the
    // PROCESSOR's re-check this test is actually about.
    [Fact]
    public async Task CancelCommittingFirst_LeavesTheNoShowProcessorSkippingTheBooking()
    {
        var resourceId = await SeedResourceAsync();
        var bookingId = await SeedConfirmedBookingWithinCheckInWindowAsync(resourceId);

        await using (var cancelContext = CreateDbContext(_tenantId, _memberUserId))
        {
            var cancelResult = await CreateCancelHandler(cancelContext).Handle(new CancelBookingCommandRequest(bookingId), CancellationToken.None);
            Assert.Equal(BookingStatus.Cancelled, cancelResult.Status);
        }

        var staleCandidate = new DueNoShowBooking(bookingId, _tenantId, resourceId, _memberUserId, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(55), 1);
        var result = await CreateNoShowProcessor(new FixedCandidateReader(staleCandidate)).ProcessDueNoShowsAsync(CancellationToken.None);

        Assert.Equal(1, result.AlreadyCompletedCount);
        Assert.Equal(0, result.MarkedNoShowCount);

        await using var verify = CreateDbContext(_tenantId);
        Assert.Equal(BookingStatus.Cancelled, (await verify.Bookings.SingleAsync(b => b.Id == bookingId)).Status);
        Assert.Empty(await verify.NotificationOutboxItems.IgnoreQueryFilters().Where(i => i.NotificationType == BookingNotificationTypes.NoShow).ToListAsync());
    }

    // No-show wins -> a subsequent member cancel attempt re-reads under its own lock acquisition and finds
    // Status no longer in CancellableStatuses (NoShow isn't Pending/Confirmed) - refused with the
    // pre-existing Booking.CancellationNotAllowed conflict, never Booking.AlreadyCheckedIn (CheckedInAtUtc
    // is null here - this is a genuinely different rejection reason than the checked-in case).
    [Fact]
    public async Task NoShowProcessorCommittingFirst_LeavesCancelRefusedWithCancellationNotAllowed()
    {
        var resourceId = await SeedResourceAsync();
        var bookingId = await SeedOverdueConfirmedBookingAsync(resourceId);

        var noShowResult = await CreateNoShowProcessor().ProcessDueNoShowsAsync(CancellationToken.None);
        Assert.Equal(1, noShowResult.MarkedNoShowCount);

        await using var cancelContext = CreateDbContext(_tenantId, _memberUserId);
        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            CreateCancelHandler(cancelContext).Handle(new CancelBookingCommandRequest(bookingId), CancellationToken.None));
        Assert.Equal("Booking.CancellationNotAllowed", exception.ErrorCode);
    }

    // The genuine race: check-in and the no-show processor fired truly concurrently via a shared start
    // gate. Regardless of which one the lock lets through first, the booking must end in exactly one of the
    // two valid states - never NoShow while also bearing a CheckedInAtUtc value.
    [Fact]
    public async Task ConcurrentCheckInAndNoShowProcessing_NeverProduceAContradictoryFinalState()
    {
        var resourceId = await SeedResourceAsync();
        var bookingId = await SeedOverdueConfirmedBookingAsync(resourceId);

        using var startGate = new SemaphoreSlim(0, 2);

        async Task<bool> CheckInAsync()
        {
            await startGate.WaitAsync();
            await using var dbContext = CreateDbContext(_tenantId, _memberUserId);
            try
            {
                await CreateCheckInHandler(dbContext).Handle(new CheckInBookingCommandRequest(bookingId), CancellationToken.None);
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
        }

        async Task<int> NoShowAsync()
        {
            await startGate.WaitAsync();
            var result = await CreateNoShowProcessor().ProcessDueNoShowsAsync(CancellationToken.None);
            return result.MarkedNoShowCount;
        }

        var checkInTask = Task.Run(CheckInAsync);
        var noShowTask = Task.Run(NoShowAsync);
        startGate.Release(2);
        var checkInSucceeded = await checkInTask;
        var markedCount = await noShowTask;

        await using var verify = CreateDbContext(_tenantId);
        var finalBooking = await verify.Bookings.SingleAsync(b => b.Id == bookingId);

        // Never a hybrid state - the one invariant this whole task exists to close.
        Assert.False(finalBooking.Status == BookingStatus.NoShow && finalBooking.CheckedInAtUtc is not null);

        if (checkInSucceeded)
        {
            Assert.Equal(BookingStatus.Confirmed, finalBooking.Status);
            Assert.NotNull(finalBooking.CheckedInAtUtc);
            Assert.Equal(0, markedCount);
        }
        else
        {
            Assert.Equal(BookingStatus.NoShow, finalBooking.Status);
            Assert.Null(finalBooking.CheckedInAtUtc);
            Assert.Equal(1, markedCount);
        }
    }

    // Same genuine race, this time cancel vs the no-show processor - never Cancelled while also having been
    // marked NoShow (structurally only one Status value can win, but this proves the OUTCOME pairing is
    // always internally consistent: whichever wins, the loser's own notification was never enqueued).
    [Fact]
    public async Task ConcurrentCancelAndNoShowProcessing_NeverProduceAContradictoryFinalState()
    {
        var resourceId = await SeedResourceAsync();
        var bookingId = await SeedOverdueConfirmedBookingAsync(resourceId);

        using var startGate = new SemaphoreSlim(0, 2);

        async Task<bool> CancelAsync()
        {
            await startGate.WaitAsync();
            await using var dbContext = CreateDbContext(_tenantId, _memberUserId);
            try
            {
                var result = await CreateCancelHandler(dbContext).Handle(new CancelBookingCommandRequest(bookingId), CancellationToken.None);
                return result.Status == BookingStatus.Cancelled;
            }
            catch (ConflictException)
            {
                // Expected when the no-show processor wins the race first: Status is no longer in
                // CancellableStatuses, so Cancel correctly refuses rather than "succeeding" into a no-op.
                return false;
            }
        }

        async Task<int> NoShowAsync()
        {
            await startGate.WaitAsync();
            var result = await CreateNoShowProcessor().ProcessDueNoShowsAsync(CancellationToken.None);
            return result.MarkedNoShowCount;
        }

        var cancelTask = Task.Run(CancelAsync);
        var noShowTask = Task.Run(NoShowAsync);
        startGate.Release(2);
        await cancelTask;
        var markedCount = await noShowTask;

        await using var verify = CreateDbContext(_tenantId);
        var finalBooking = await verify.Bookings.SingleAsync(b => b.Id == bookingId);
        Assert.True(finalBooking.Status is BookingStatus.Cancelled or BookingStatus.NoShow);

        var noShowOutboxCount = await verify.NotificationOutboxItems.IgnoreQueryFilters()
            .CountAsync(i => i.NotificationType == BookingNotificationTypes.NoShow && i.RecipientUserId == _memberUserId);
        var cancellationOutboxCount = await verify.NotificationOutboxItems.IgnoreQueryFilters()
            .CountAsync(i => i.NotificationType == BookingNotificationTypes.Cancellation && i.RecipientUserId == _memberUserId);

        if (finalBooking.Status == BookingStatus.Cancelled)
        {
            Assert.Equal(0, markedCount);
            Assert.Equal(0, noShowOutboxCount);
            Assert.Equal(1, cancellationOutboxCount);
        }
        else
        {
            Assert.Equal(1, markedCount);
            Assert.Equal(1, noShowOutboxCount);
            Assert.Equal(0, cancellationOutboxCount);
        }
    }

    private async Task<Guid> SeedResourceAsync()
    {
        var resourceId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(_tenantId);
        dbContext.Resources.Add(new Resource
        {
            Id = resourceId, TenantId = _tenantId, ResourceTypeId = _resourceTypeId, Name = $"NoShow Vs CheckIn Vs Cancel Room {resourceId:N}",
            Capacity = 4, RequiresApproval = false, Status = ResourceStatus.Active, TimeZoneId = "UTC",
        });
        await dbContext.SaveChangesAsync();
        return resourceId;
    }

    // Started far enough in the past that it is genuinely due for the no-show processor's own live-clock
    // reader (20 minutes past start > the tenant's 15-minute grace) - i.e. this is the realistic state a
    // booking is actually in by the time the no-show job would naturally discover it. By design this is
    // ALSO already past the check-in window's own close (the same deadline), so a real check-in call
    // against a booking seeded this way is correctly refused - see SeedConfirmedBookingWithinCheckInWindowAsync
    // for the complementary "still checkinable" seed used by the check-in/cancel-wins tests.
    private async Task<Guid> SeedOverdueConfirmedBookingAsync(Guid resourceId)
    {
        await using var dbContext = CreateDbContext(_tenantId);
        var start = DateTimeOffset.UtcNow.AddMinutes(-20); // 20 minutes past start > 15-minute grace
        var booking = new Booking
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, ResourceId = resourceId, UserId = _memberUserId,
            StartUtc = start, EndUtc = start.AddHours(1), Quantity = 1, Status = BookingStatus.Confirmed, CreatedAtUtc = start.AddDays(-1),
        };
        dbContext.Bookings.Add(booking);
        await dbContext.SaveChangesAsync();
        return booking.Id;
    }

    // Started only 5 minutes ago - comfortably inside both the check-in window (opens -15m/closes +15m from
    // start) and cancel's own no-window rule, so a real check-in or cancel call against it genuinely
    // succeeds. Never due for the no-show reader's OWN live-clock query - tests that need the no-show
    // processor to consider this exact booking anyway force it through via FixedCandidateReader.
    private async Task<Guid> SeedConfirmedBookingWithinCheckInWindowAsync(Guid resourceId)
    {
        await using var dbContext = CreateDbContext(_tenantId);
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        var booking = new Booking
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, ResourceId = resourceId, UserId = _memberUserId,
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

    // A real NoShowProcessor wired against THIS test's own connection string, so it operates against the
    // SAME database the check-in/cancel handlers above do - mirrors NoShowProcessorTests.CreateProcessor's
    // own DI shape. Defaults to the REAL NoShowReader (genuine due-discovery); tests that need to force a
    // specific, possibly-not-currently-due booking through the processor's own re-check pass a
    // FixedCandidateReader instead - see that class below.
    private NoShowProcessor CreateNoShowProcessor(INoShowReader? reader = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<BookSpaceDbContext>(options => options.UseSqlServer(_connectionString));
        services.AddScoped<ICurrentUserContext>(_ => new FixedCurrentUserContext(_tenantId, _memberUserId));
        services.AddScoped<IResourceBookingLock, ResourceBookingLock>();
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<INotificationOutboxWriter, NotificationOutboxWriter>();
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var effectiveReader = reader ?? new NoShowReader(CreateDbContext(_tenantId), NullLogger<NoShowReader>.Instance);
        var options = Options.Create(new BackgroundJobsOptions { NoShow = new NoShowOptions { Enabled = true, BatchSize = 50 } });
        return new NoShowProcessor(scopeFactory, effectiveReader, options, new FakeTimeProvider(DateTimeOffset.UtcNow), NullLogger<NoShowProcessor>.Instance);
    }

    // Always returns the SAME single candidate, regardless of the real due state - forces
    // NoShowProcessor's own re-check-under-lock logic to run against a specific booking that the real
    // reader's live-clock query would not (yet, or no longer) consider due, isolating the PROCESSOR's
    // re-validation from the READER's own due-filtering (already covered by NoShowReaderTests).
    private sealed class FixedCandidateReader(DueNoShowBooking candidate) : INoShowReader
    {
        public Task<IReadOnlyList<DueNoShowBooking>> GetDueNoShowsAsync(int batchSize, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DueNoShowBooking>>([candidate]);
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
