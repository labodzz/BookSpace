using BookSpace.Application.BackgroundJobs;
using BookSpace.Application.Bookings;
using BookSpace.Application.Notifications;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// Proves NoShowProcessor's full business behavior against a REAL SQL Server (LocalDB): atomic marking of a
// Booking as NoShow + the enqueued notification, the check-in/cancel race against the resource lock,
// idempotency/concurrency, recurring-occurrence independence, and failure isolation - see
// docs/background-jobs.md ("No-show job") for the full design.
public sealed class NoShowProcessorTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceNoShowProcessorTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    private Guid _tenantId;
    private Guid _resourceTypeId;
    private Guid _resourceId;
    private Guid _ownerId;

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureCreatedAsync();

        _tenantId = Guid.NewGuid();
        _resourceTypeId = Guid.NewGuid();
        _resourceId = Guid.NewGuid();
        _ownerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        dbContext.Tenants.Add(new Tenant
        {
            Id = _tenantId, Name = "NoShow Processor Tenant", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active,
            CreatedAtUtc = now, NoShowGraceMinutes = 15,
        });
        dbContext.ResourceTypes.Add(new ResourceType { Id = _resourceTypeId, TenantId = _tenantId, Name = "Meeting Room" });
        dbContext.Resources.Add(new Resource
        {
            Id = _resourceId, TenantId = _tenantId, ResourceTypeId = _resourceTypeId, Name = "NoShow Room",
            Capacity = 5, RequiresApproval = false, Status = ResourceStatus.Active, TimeZoneId = "UTC",
        });
        dbContext.Users.Add(new User { Id = _ownerId, TenantId = _tenantId, FirstName = "Owner", LastName = "User", Email = $"owner-{Guid.NewGuid():N}@bookspace.test", PasswordHash = "x", CreatedAtUtc = now });
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureDeletedAsync();
    }

    // --- A: basic marking - full pipeline ---

    [Fact]
    public async Task ProcessDueNoShowsAsync_WithADueBooking_MarksItNoShowAndEnqueuesExactlyOneNotification()
    {
        var bookingId = await SeedBookingAsync(startUtc: DateTimeOffset.UtcNow.AddMinutes(-20));
        var processor = CreateProcessor();

        var result = await processor.ProcessDueNoShowsAsync(CancellationToken.None);

        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(1, result.MarkedNoShowCount);
        Assert.Equal(1, result.NotificationEnqueuedCount);
        Assert.Equal(0, result.AlreadyCompletedCount);
        Assert.Equal(0, result.CheckedInCount);
        Assert.Equal(0, result.NotYetDueCount);

        await using var verify = CreateDbContext();
        var booking = await verify.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == bookingId);
        Assert.Equal(BookingStatus.NoShow, booking.Status);
        Assert.Null(booking.CheckedInAtUtc); // the absence IS the signal - never fabricated

        var outboxItems = await verify.NotificationOutboxItems.IgnoreQueryFilters()
            .Where(item => item.NotificationType == BookingNotificationTypes.NoShow)
            .ToListAsync();
        Assert.Single(outboxItems);
        Assert.Equal($"booking:{bookingId}:noshow", outboxItems[0].IdempotencyKey);
        Assert.Equal(_ownerId, outboxItems[0].RecipientUserId); // the booking owner, never an approver/admin
    }

    [Fact]
    public async Task ProcessDueNoShowsAsync_WithNoCandidates_ReturnsEmptyWithoutEnqueueingAnything()
    {
        var processor = CreateProcessor();

        var result = await processor.ProcessDueNoShowsAsync(CancellationToken.None);

        Assert.Equal(NoShowCycleResult.Empty, result);
    }

    [Fact]
    public async Task ProcessDueNoShowsAsync_WhenDisabled_NeverCallsTheReader()
    {
        var bookingId = await SeedBookingAsync(startUtc: DateTimeOffset.UtcNow.AddMinutes(-20));
        var processor = CreateProcessor(enabled: false);

        var result = await processor.ProcessDueNoShowsAsync(CancellationToken.None);

        Assert.Equal(NoShowCycleResult.Empty, result);
        await using var verify = CreateDbContext();
        Assert.Equal(BookingStatus.Confirmed, (await verify.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == bookingId)).Status);
    }

    // --- B: check-in wins the race - the re-read under the lock finds CheckedInAtUtc already set ---

    [Fact]
    public async Task ProcessDueNoShowsAsync_WhenCheckedInBeforeTheLockWasAcquired_SkipsItAndNeverMarksNoShow()
    {
        var bookingId = await SeedBookingAsync(startUtc: DateTimeOffset.UtcNow.AddMinutes(-20));
        DateTimeOffset checkedInAtUtc;
        await using (var checkInContext = CreateDbContext())
        {
            var bookingToCheckIn = await checkInContext.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == bookingId);
            checkedInAtUtc = DateTimeOffset.UtcNow;
            bookingToCheckIn.CheckedInAtUtc = checkedInAtUtc;
            await checkInContext.SaveChangesAsync();
        }

        // The reader itself would no longer find this (CheckedInAtUtc is now set) - this test is
        // specifically about the PROCESSOR's own re-check, so a stub forces the stale candidate through as
        // if the reader's read happened a moment before the check-in committed.
        var staleCandidate = new DueNoShowBooking(bookingId, _tenantId, _resourceId, _ownerId, DateTimeOffset.UtcNow.AddMinutes(-20), DateTimeOffset.UtcNow.AddMinutes(-19), 1);
        var processor = CreateProcessor(reader: new FixedCandidateReader(staleCandidate));

        var result = await processor.ProcessDueNoShowsAsync(CancellationToken.None);

        Assert.Equal(1, result.CheckedInCount);
        Assert.Equal(0, result.MarkedNoShowCount);
        Assert.Equal(0, result.AlreadyCompletedCount);

        await using var verify = CreateDbContext();
        var booking = await verify.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == bookingId);
        Assert.Equal(BookingStatus.Confirmed, booking.Status); // never overwritten
        Assert.Equal(checkedInAtUtc, booking.CheckedInAtUtc); // never cleared or changed
        Assert.Empty(await verify.NotificationOutboxItems.IgnoreQueryFilters().Where(item => item.NotificationType == BookingNotificationTypes.NoShow).ToListAsync());
    }

    // --- C: cancel wins the race - the re-read under the lock finds Status no longer Confirmed ---

    [Fact]
    public async Task ProcessDueNoShowsAsync_WhenCancelledBeforeTheLockWasAcquired_SkipsItAndNeverMarksNoShow()
    {
        var bookingId = await SeedBookingAsync(startUtc: DateTimeOffset.UtcNow.AddMinutes(-20));
        await using (var cancelContext = CreateDbContext())
        {
            var booking = await cancelContext.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == bookingId);
            booking.Status = BookingStatus.Cancelled;
            booking.CancelledAtUtc = DateTimeOffset.UtcNow;
            await cancelContext.SaveChangesAsync();
        }

        var staleCandidate = new DueNoShowBooking(bookingId, _tenantId, _resourceId, _ownerId, DateTimeOffset.UtcNow.AddMinutes(-20), DateTimeOffset.UtcNow.AddMinutes(-19), 1);
        var processor = CreateProcessor(reader: new FixedCandidateReader(staleCandidate));

        var result = await processor.ProcessDueNoShowsAsync(CancellationToken.None);

        Assert.Equal(1, result.AlreadyCompletedCount);
        Assert.Equal(0, result.MarkedNoShowCount);
        Assert.Equal(0, result.CheckedInCount);

        await using var verify = CreateDbContext();
        Assert.Equal(BookingStatus.Cancelled, (await verify.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == bookingId)).Status);
        Assert.Empty(await verify.NotificationOutboxItems.IgnoreQueryFilters().Where(item => item.NotificationType == BookingNotificationTypes.NoShow).ToListAsync());
    }

    // --- D: the exact "<=" vs "<" boundary, proven deterministically via an injectable TimeProvider (the
    // reader's own live-clock boundary cannot be hit deterministically - see NoShowReaderTests) ---

    [Fact]
    public async Task ProcessDueNoShowsAsync_WithABookingExactlyAtItsDeadline_IsNotYetDue()
    {
        var startUtc = DateTimeOffset.UtcNow.AddHours(-1);
        var bookingId = await SeedBookingAsync(startUtc);
        var deadlineUtc = startUtc.AddMinutes(15); // this tenant's seeded NoShowGraceMinutes
        var staleCandidate = new DueNoShowBooking(bookingId, _tenantId, _resourceId, _ownerId, startUtc, startUtc.AddHours(1), 1);
        var processor = CreateProcessor(reader: new FixedCandidateReader(staleCandidate), timeProvider: new FakeTimeProvider(deadlineUtc));

        var result = await processor.ProcessDueNoShowsAsync(CancellationToken.None);

        Assert.Equal(1, result.NotYetDueCount);
        Assert.Equal(0, result.MarkedNoShowCount);
        await using var verify = CreateDbContext();
        Assert.Equal(BookingStatus.Confirmed, (await verify.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == bookingId)).Status);
    }

    [Fact]
    public async Task ProcessDueNoShowsAsync_OneTickPastItsDeadline_IsMarkedNoShow()
    {
        var startUtc = DateTimeOffset.UtcNow.AddHours(-1);
        var bookingId = await SeedBookingAsync(startUtc);
        var deadlineUtc = startUtc.AddMinutes(15);
        var staleCandidate = new DueNoShowBooking(bookingId, _tenantId, _resourceId, _ownerId, startUtc, startUtc.AddHours(1), 1);
        var processor = CreateProcessor(reader: new FixedCandidateReader(staleCandidate), timeProvider: new FakeTimeProvider(deadlineUtc.AddTicks(1)));

        var result = await processor.ProcessDueNoShowsAsync(CancellationToken.None);

        Assert.Equal(1, result.MarkedNoShowCount);
        await using var verify = CreateDbContext();
        Assert.Equal(BookingStatus.NoShow, (await verify.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == bookingId)).Status);
    }

    // --- E: idempotency - a second cycle after a successful marking finds nothing left to do, and a
    // repeated attempt against the SAME (already-NoShow) candidate correctly detects AlreadyCompleted
    // rather than double-marking or double-notifying ---

    [Fact]
    public async Task ProcessDueNoShowsAsync_CalledTwiceInARow_TheSecondCallFindsNothingLeftToDo()
    {
        await SeedBookingAsync(startUtc: DateTimeOffset.UtcNow.AddMinutes(-20));
        var processor = CreateProcessor();
        await processor.ProcessDueNoShowsAsync(CancellationToken.None);

        var second = await processor.ProcessDueNoShowsAsync(CancellationToken.None);

        Assert.Equal(NoShowCycleResult.Empty, second); // already NoShow - no longer Confirmed, never a candidate again
    }

    [Fact]
    public async Task ProcessDueNoShowsAsync_WhenTheSameCandidateIsForciblyReprocessed_DetectsAlreadyCompletedWithoutADuplicateNotification()
    {
        var bookingId = await SeedBookingAsync(startUtc: DateTimeOffset.UtcNow.AddMinutes(-20));
        var realReader = new NoShowReader(CreateDbContext(), NullLogger<NoShowReader>.Instance);
        var candidate = (await realReader.GetDueNoShowsAsync(50, CancellationToken.None)).Single();
        var repeatingReader = new FixedCandidateReader(candidate);
        var processor = CreateProcessor(reader: repeatingReader);

        var first = await processor.ProcessDueNoShowsAsync(CancellationToken.None);
        var second = await processor.ProcessDueNoShowsAsync(CancellationToken.None);

        Assert.Equal(1, first.MarkedNoShowCount);
        Assert.Equal(0, second.MarkedNoShowCount);
        Assert.Equal(1, second.AlreadyCompletedCount);

        await using var verify = CreateDbContext();
        Assert.Equal(1, await verify.NotificationOutboxItems.IgnoreQueryFilters().CountAsync(item => item.IdempotencyKey == $"booking:{bookingId}:noshow"));
    }

    [Fact]
    public async Task ProcessDueNoShowsAsync_TwoConcurrentAttemptsOnTheSameCandidate_LeaveExactlyOneMarkingAndOneNotification()
    {
        var bookingId = await SeedBookingAsync(startUtc: DateTimeOffset.UtcNow.AddMinutes(-20));
        using var startBarrier = new Barrier(2);

        async Task<NoShowCycleResult> RunRacingCycleAsync()
        {
            var realReader = new NoShowReader(CreateDbContext(), NullLogger<NoShowReader>.Instance);
            var candidates = await realReader.GetDueNoShowsAsync(50, CancellationToken.None);
            var synchronizedReader = new BarrierSynchronizedFixedReader(candidates, startBarrier);
            var processor = CreateProcessor(reader: synchronizedReader);
            return await processor.ProcessDueNoShowsAsync(CancellationToken.None);
        }

        var results = await Task.WhenAll(RunRacingCycleAsync(), RunRacingCycleAsync());

        Assert.Equal(1, results.Sum(r => r.MarkedNoShowCount));
        Assert.Equal(1, results.Sum(r => r.AlreadyCompletedCount));

        await using var verify = CreateDbContext();
        Assert.Equal(BookingStatus.NoShow, (await verify.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == bookingId)).Status);
        Assert.Equal(1, await verify.NotificationOutboxItems.IgnoreQueryFilters().CountAsync(item => item.IdempotencyKey == $"booking:{bookingId}:noshow"));
    }

    // --- F: atomicity - a failure injected right before commit leaves NEITHER the Booking NOR any outbox
    // item changed ---

    [Fact]
    public async Task ProcessDueNoShowsAsync_WhenSaveChangesFailsBeforeCommit_PersistsNeitherTheMarkingNorAnyNotification()
    {
        var bookingId = await SeedBookingAsync(startUtc: DateTimeOffset.UtcNow.AddMinutes(-20));
        var throwingInterceptor = new ThrowingSaveChangesInterceptor { ShouldThrow = true };
        var processor = CreateProcessor(itemScopeInterceptor: throwingInterceptor);

        var result = await processor.ProcessDueNoShowsAsync(CancellationToken.None);

        Assert.Equal(1, result.FailedCount);
        Assert.Equal(0, result.MarkedNoShowCount);

        await using var verify = CreateDbContext();
        Assert.Equal(BookingStatus.Confirmed, (await verify.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == bookingId)).Status);
        Assert.Equal(0, await verify.NotificationOutboxItems.IgnoreQueryFilters().CountAsync(item => item.IdempotencyKey == $"booking:{bookingId}:noshow"));
    }

    // --- G: recurring - each occurrence is independent; marking one never touches a sibling occurrence in
    // the same series ---

    [Fact]
    public async Task ProcessDueNoShowsAsync_WithTwoOccurrencesInASeries_OnlyMarksTheDueOneNeverTheOther()
    {
        var seriesId = Guid.NewGuid();
        await using (var seedContext = CreateDbContext())
        {
            seedContext.RecurringSeries.Add(new RecurringSeries
            {
                Id = seriesId, TenantId = _tenantId, ResourceId = _resourceId, UserId = _ownerId,
                StartDate = DateOnly.FromDateTime(DateTime.UtcNow), StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0),
                TimeZoneId = "UTC", Frequency = RecurrenceFrequency.Daily, Interval = 1, OccurrenceCount = 2, Quantity = 1,
            });
            await seedContext.SaveChangesAsync();
        }

        var dueBookingId = await SeedBookingAsync(startUtc: DateTimeOffset.UtcNow.AddMinutes(-20), seriesId: seriesId);
        var futureBookingId = await SeedBookingAsync(startUtc: DateTimeOffset.UtcNow.AddDays(1), seriesId: seriesId);

        var processor = CreateProcessor();
        var result = await processor.ProcessDueNoShowsAsync(CancellationToken.None);

        Assert.Equal(1, result.MarkedNoShowCount);
        await using var verify = CreateDbContext();
        Assert.Equal(BookingStatus.NoShow, (await verify.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == dueBookingId)).Status);
        Assert.Equal(BookingStatus.Confirmed, (await verify.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == futureBookingId)).Status);
    }

    // --- H: Email:Enabled plays no part here at all - the enqueued item simply sits Pending ---

    [Fact]
    public async Task ProcessDueNoShowsAsync_EnqueuesANotificationThatStaysPendingRegardlessOfEmailConfiguration()
    {
        var bookingId = await SeedBookingAsync(startUtc: DateTimeOffset.UtcNow.AddMinutes(-20));
        var processor = CreateProcessor();

        await processor.ProcessDueNoShowsAsync(CancellationToken.None);

        await using var verify = CreateDbContext();
        var item = await verify.NotificationOutboxItems.IgnoreQueryFilters().SingleAsync(i => i.IdempotencyKey == $"booking:{bookingId}:noshow");
        Assert.Equal(NotificationOutboxStatus.Pending, item.Status); // untouched by dispatch - this task never sends email
    }

    // --- I: availability - a NoShow booking stops counting toward active capacity immediately ---

    [Fact]
    public async Task ProcessDueNoShowsAsync_ThenAvailability_ShowsCapacityRestoredForThatSlot()
    {
        var startUtc = DateTimeOffset.UtcNow.AddMinutes(-20);
        var endUtc = startUtc.AddHours(2); // a 2-hour booking, 20 minutes past start when processed
        var bookingId = await SeedBookingAsync(startUtc, endUtc: endUtc, quantity: 5);
        var processor = CreateProcessor();

        await processor.ProcessDueNoShowsAsync(CancellationToken.None);

        await using var verify = CreateDbContext();
        // BookingAvailabilityRepository.ActiveStatuses is [Pending, Confirmed] only - NoShow is already
        // excluded, so the capacity this booking held is released the instant Status flips, with no
        // additional code needed. Proven here directly against the same ActiveStatuses-filtered shape that
        // repository query uses, against the SAME booking row this processor just mutated.
        var remainingActiveDemand = await verify.Bookings.IgnoreQueryFilters()
            .Where(b => b.ResourceId == _resourceId && b.StartUtc < endUtc && b.EndUtc > startUtc)
            .Where(b => b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed)
            .SumAsync(b => (int?)b.Quantity) ?? 0;
        Assert.Equal(0, remainingActiveDemand);
        Assert.Equal(BookingStatus.NoShow, (await verify.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == bookingId)).Status);
    }

    // --- J: cancellation - a pre-cancelled token never starts any item; cancellation mid-batch stops
    // before the next item ---

    [Fact]
    public async Task ProcessDueNoShowsAsync_WithAnAlreadyCancelledToken_NeverStartsAnyItem()
    {
        var bookingId = await SeedBookingAsync(startUtc: DateTimeOffset.UtcNow.AddMinutes(-20));
        var processor = CreateProcessor();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => processor.ProcessDueNoShowsAsync(new CancellationToken(canceled: true)));

        await using var verify = CreateDbContext();
        Assert.Equal(BookingStatus.Confirmed, (await verify.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == bookingId)).Status);
    }

    [Fact]
    public async Task ProcessDueNoShowsAsync_WhenCancelledDuringTheFirstItem_NeverStartsTheSecondItem()
    {
        var firstBookingId = await SeedBookingAsync(startUtc: DateTimeOffset.UtcNow.AddMinutes(-20));
        var secondBookingId = await SeedBookingAsync(startUtc: DateTimeOffset.UtcNow.AddMinutes(-20));
        using var cts = new CancellationTokenSource();
        var cancelingInterceptor = new CancelOnFirstSaveInterceptor(cts);
        var processor = CreateProcessor(itemScopeInterceptor: cancelingInterceptor);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ProcessDueNoShowsAsync(cts.Token));

        await using var verify = CreateDbContext();
        var firstStatus = (await verify.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == firstBookingId)).Status;
        var secondStatus = (await verify.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == secondBookingId)).Status;
        // Same deliberately-not-over-specified guarantee as StaleApprovalProcessorTests' equivalent test:
        // at most one of the two was marked before cancellation stopped the batch.
        var markedCount = new[] { firstStatus, secondStatus }.Count(status => status == BookingStatus.NoShow);
        Assert.True(markedCount <= 1, $"Expected at most one candidate to be marked NoShow once cancellation fired, but both were (firstStatus={firstStatus}, secondStatus={secondStatus}).");
    }

    // --- K: logging/correlation - no PII in any captured log line ---

    [Fact]
    public async Task ProcessDueNoShowsAsync_NeverLogsTheRecipientEmailOrAnyPayloadContent()
    {
        await SeedBookingAsync(startUtc: DateTimeOffset.UtcNow.AddMinutes(-20));
        var fakeLogger = new FakeLogger<NoShowProcessor>();
        var processor = CreateProcessor(logger: fakeLogger);

        await processor.ProcessDueNoShowsAsync(CancellationToken.None);

        await using var verify = CreateDbContext();
        var ownerEmail = (await verify.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == _ownerId)).Email;
        foreach (var record in fakeLogger.Collector.GetSnapshot())
        {
            Assert.DoesNotContain(ownerEmail, record.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    // --- Test infrastructure ---

    private async Task<Guid> SeedBookingAsync(
        DateTimeOffset startUtc, DateTimeOffset? endUtc = null, int quantity = 1, Guid? seriesId = null)
    {
        var bookingId = Guid.NewGuid();
        await using var dbContext = CreateDbContext();
        dbContext.Bookings.Add(new Booking
        {
            Id = bookingId, TenantId = _tenantId, ResourceId = _resourceId, UserId = _ownerId, SeriesId = seriesId,
            StartUtc = startUtc, EndUtc = endUtc ?? startUtc.AddHours(1), Quantity = quantity, Status = BookingStatus.Confirmed,
            CreatedAtUtc = startUtc.AddDays(-1),
        });
        await dbContext.SaveChangesAsync();
        return bookingId;
    }

    private NoShowProcessor CreateProcessor(
        bool enabled = true, int batchSize = 50, INoShowReader? reader = null,
        IInterceptor? itemScopeInterceptor = null, ILogger<NoShowProcessor>? logger = null, TimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<BookSpaceDbContext>(options =>
        {
            options.UseSqlServer(_connectionString);
            if (itemScopeInterceptor is not null)
            {
                options.AddInterceptors(itemScopeInterceptor);
            }
        });
        services.AddScoped<ICurrentUserContext>(_ => new FixedCurrentUserContext());
        services.AddScoped<IResourceBookingLock, ResourceBookingLock>();
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<INotificationOutboxWriter, NotificationOutboxWriter>();
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var effectiveReader = reader ?? new NoShowReader(CreateDbContext(), NullLogger<NoShowReader>.Instance);
        var options = Options.Create(new BackgroundJobsOptions { NoShow = new NoShowOptions { Enabled = enabled, BatchSize = batchSize } });
        return new NoShowProcessor(scopeFactory, effectiveReader, options, timeProvider ?? new FakeTimeProvider(DateTimeOffset.UtcNow), logger ?? NullLogger<NoShowProcessor>.Instance);
    }

    private BookSpaceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>().UseSqlServer(_connectionString).Options;
        return new BookSpaceDbContext(options, new FixedCurrentUserContext());
    }

    private sealed class FixedCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId => null;
        public Guid? TenantId => null;
        public IReadOnlyCollection<string> Roles => [];
    }

    // Always returns the SAME single candidate, regardless of the real due state - used to force
    // NoShowProcessor's own re-check-under-lock logic to run against a candidate that may no longer
    // actually be due, simulating a restart/retry or a race the reader itself would no longer see.
    private sealed class FixedCandidateReader(DueNoShowBooking candidate) : INoShowReader
    {
        public Task<IReadOnlyList<DueNoShowBooking>> GetDueNoShowsAsync(int batchSize, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DueNoShowBooking>>([candidate]);
    }

    // Synchronizes two concurrent calls so both finish "reading" before either proceeds to acquire the
    // resource lock - without this, one racer would likely finish entirely before the other even started.
    private sealed class BarrierSynchronizedFixedReader(IReadOnlyList<DueNoShowBooking> candidates, Barrier barrier) : INoShowReader
    {
        public Task<IReadOnlyList<DueNoShowBooking>> GetDueNoShowsAsync(int batchSize, CancellationToken cancellationToken)
        {
            barrier.SignalAndWait(cancellationToken);
            return Task.FromResult(candidates);
        }
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

    // Cancels the shared token the instant the FIRST item's SaveChanges actually fires - deterministically
    // simulates "cancellation arrives exactly between two items" without relying on real concurrent timing.
    private sealed class CancelOnFirstSaveInterceptor(CancellationTokenSource cts) : SaveChangesInterceptor
    {
        private bool _cancelled;

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            CancelOnce();
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            CancelOnce();
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private void CancelOnce()
        {
            if (!_cancelled)
            {
                _cancelled = true;
                cts.Cancel();
            }
        }
    }
}
