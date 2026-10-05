using BookSpace.Application.BackgroundJobs;
using BookSpace.Application.Notifications;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// Proves the starvation fix against a REAL SQL Server (LocalDB): ReminderProducer, wired to the real
// BookingReminderReader/BookingReminderMarker/NotificationOutboxWriter, actually makes progress through a
// backlog larger than one BatchSize across repeated cycles, atomically commits each cycle's outbox items
// and ReminderQueuedAtUtc markers together, survives a simulated mid-commit failure, recovers a legacy
// row whose outbox item already exists but whose marker is still null, and remains safe under real
// concurrency - see docs/background-jobs.md ("Reminder job - the ReminderQueuedAtUtc marker") for the
// full design this verifies.
public sealed class BookingReminderBacklogProgressionTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceReminderBacklogTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    private Guid _tenantId;
    private Guid _resourceTypeId;
    private Guid _resourceId;
    private Guid _userId;

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureCreatedAsync();

        _tenantId = Guid.NewGuid();
        _resourceTypeId = Guid.NewGuid();
        _resourceId = Guid.NewGuid();
        _userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        dbContext.Tenants.Add(new Tenant { Id = _tenantId, Name = "Backlog Tenant", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = now });
        dbContext.ResourceTypes.Add(new ResourceType { Id = _resourceTypeId, TenantId = _tenantId, Name = "Meeting Room" });
        dbContext.Resources.Add(new Resource
        {
            Id = _resourceId, TenantId = _tenantId, ResourceTypeId = _resourceTypeId, Name = "Backlog Room",
            Capacity = 10, RequiresApproval = false, Status = ResourceStatus.Active, TimeZoneId = "UTC",
        });
        dbContext.Users.Add(new User { Id = _userId, TenantId = _tenantId, FirstName = "Backlog", LastName = "User", Email = $"backlog-{Guid.NewGuid():N}@bookspace.test", PasswordHash = "x", CreatedAtUtc = now });
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureDeletedAsync();
    }

    // --- 1: backlog progression - more candidates than BatchSize, across repeated cycles, nobody starves ---

    [Fact]
    public async Task RepeatedCycles_AgainstABacklogLargerThanOneBatch_EventuallyEnqueuesEveryCandidateExactlyOnce()
    {
        const int totalBookings = 110;
        const int batchSize = 50;
        var bookingIds = new List<Guid>();
        for (var i = 0; i < totalBookings; i++)
        {
            bookingIds.Add(await SeedConfirmedBookingAsync(DateTimeOffset.UtcNow.AddMinutes(5 + i)));
        }

        // Three cycles: 50 + 50 + 10 - each against a FRESH DbContext, mirroring BackgroundJobsWorker
        // creating a fresh per-cycle scope in the real system.
        var firstCycle = await RunOneCycleAsync(batchSize);
        var secondCycle = await RunOneCycleAsync(batchSize);
        var thirdCycle = await RunOneCycleAsync(batchSize);
        var fourthCycle = await RunOneCycleAsync(batchSize); // nothing left - must be a true no-op, not a re-enqueue

        Assert.Equal(50, firstCycle.EnqueuedCount);
        Assert.Equal(50, secondCycle.EnqueuedCount);
        Assert.Equal(10, thirdCycle.EnqueuedCount);
        Assert.Equal(ReminderProducerResult.Empty, fourthCycle);

        await using var verify = CreateDbContext();
        var markedCount = await verify.Bookings.IgnoreQueryFilters().CountAsync(b => bookingIds.Contains(b.Id) && b.ReminderQueuedAtUtc != null);
        var outboxCount = await verify.NotificationOutboxItems.IgnoreQueryFilters()
            .CountAsync(item => item.NotificationType == BookingNotificationTypes.Reminder && bookingIds.Select(id => $"booking:{id}:reminder").Contains(item.IdempotencyKey));
        Assert.Equal(totalBookings, markedCount); // every single booking got its marker - nobody starved
        Assert.Equal(totalBookings, outboxCount); // and exactly one outbox row each - no duplicates
    }

    // --- 2/9: already-marked bookings never occupy a batch slot, and batch ordering is by StartUtc ---

    [Fact]
    public async Task ASecondCycle_NeverReSelectsTheFirstCyclesAlreadyMarkedBookings_AndAdvancesToTheNextOnes()
    {
        var early = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            early.Add(await SeedConfirmedBookingAsync(DateTimeOffset.UtcNow.AddMinutes(5 + i)));
        }

        var first = await RunOneCycleAsync(batchSize: 3);
        Assert.Equal(3, first.EnqueuedCount);

        var later = await SeedConfirmedBookingAsync(DateTimeOffset.UtcNow.AddMinutes(30));

        var second = await RunOneCycleAsync(batchSize: 3);

        Assert.Equal(1, second.CandidateCount); // only the new one - the 3 already-marked never reappear
        Assert.Equal(1, second.EnqueuedCount);

        await using var verify = CreateDbContext();
        var outboxKeys = await verify.NotificationOutboxItems.IgnoreQueryFilters()
            .Where(item => item.NotificationType == BookingNotificationTypes.Reminder)
            .Select(item => item.IdempotencyKey)
            .ToListAsync();
        Assert.Contains($"booking:{later}:reminder", outboxKeys);
        Assert.Equal(4, outboxKeys.Count); // 3 from the first cycle + 1 from the second - never re-created
    }

    // --- 3: atomic enqueue + marker, in exactly one SaveChangesAsync call ---

    [Fact]
    public async Task ASuccessfulCycle_CommitsAllOutboxItemsAndAllMarkersInExactlyOneSaveChangesCall()
    {
        for (var i = 0; i < 5; i++)
        {
            await SeedConfirmedBookingAsync(DateTimeOffset.UtcNow.AddMinutes(5 + i));
        }

        var countingInterceptor = new CountingSaveChangesInterceptor();
        await using var dbContext = CreateDbContext(countingInterceptor);
        var producer = CreateProducer(dbContext);

        var result = await producer.ProduceDueRemindersAsync(CancellationToken.None);

        Assert.Equal(5, result.EnqueuedCount);
        Assert.Equal(1, countingInterceptor.SaveChangesCallCount); // not 5, not 10 - one combined commit
    }

    // --- 4: rollback - a failure injected right before commit leaves NEITHER outbox items NOR markers ---

    [Fact]
    public async Task WhenSaveChangesFailsBeforeCommit_PersistsNeitherTheOutboxItemsNorAnyMarker()
    {
        var bookingIds = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            bookingIds.Add(await SeedConfirmedBookingAsync(DateTimeOffset.UtcNow.AddMinutes(5 + i)));
        }

        var throwingInterceptor = new ThrowingSaveChangesInterceptor { ShouldThrow = true };
        await using var dbContext = CreateDbContext(throwingInterceptor);
        var producer = CreateProducer(dbContext);

        await Assert.ThrowsAsync<InvalidOperationException>(() => producer.ProduceDueRemindersAsync(CancellationToken.None));

        await using var verify = CreateDbContext();
        Assert.Equal(0, await verify.NotificationOutboxItems.IgnoreQueryFilters().CountAsync(item => item.NotificationType == BookingNotificationTypes.Reminder));
        Assert.All(
            await verify.Bookings.IgnoreQueryFilters().Where(b => bookingIds.Contains(b.Id)).ToListAsync(),
            booking => Assert.Null(booking.ReminderQueuedAtUtc));
    }

    // --- 5: duplicate recovery - a legacy row (outbox item exists, marker is still null, e.g. from before
    // this fix shipped, or from an earlier partially-completed attempt) must be repaired without creating a
    // second outbox item, and must stop reappearing as a candidate afterward ---

    [Fact]
    public async Task ASubsequentCycle_RepairsALegacyRowWhoseOutboxItemAlreadyExistsButWhoseMarkerIsStillNull()
    {
        var bookingId = await SeedConfirmedBookingAsync(DateTimeOffset.UtcNow.AddMinutes(30));

        // Simulates the exact legacy state this fix needs to recover from: the outbox row already exists
        // (as the old, pre-fix code would have left it after a duplicate poll), but ReminderQueuedAtUtc was
        // never set.
        await using (var seedContext = CreateDbContext())
        {
            seedContext.NotificationOutboxItems.Add(new NotificationOutboxItem
            {
                Id = Guid.NewGuid(), TenantId = _tenantId, NotificationType = BookingNotificationTypes.Reminder, RecipientUserId = _userId,
                PayloadJson = "{}", IdempotencyKey = $"booking:{bookingId}:reminder", CreatedAtUtc = DateTimeOffset.UtcNow,
                AvailableAtUtc = DateTimeOffset.UtcNow, Status = NotificationOutboxStatus.Pending, AttemptCount = 0,
            });
            await seedContext.SaveChangesAsync();
        }

        var result = await RunOneCycleAsync(batchSize: 50);

        Assert.Equal(1, result.CandidateCount); // still selected - marker was null, so it still looked due
        Assert.Equal(0, result.EnqueuedCount); // but the outbox row already existed
        Assert.Equal(1, result.DuplicateCount);

        await using var verify = CreateDbContext();
        Assert.Equal(1, await verify.NotificationOutboxItems.IgnoreQueryFilters()
            .CountAsync(item => item.IdempotencyKey == $"booking:{bookingId}:reminder")); // still exactly one row, never a second
        Assert.NotNull((await verify.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == bookingId)).ReminderQueuedAtUtc); // marker repaired

        // And the fix actually holds: it never reappears as a candidate again.
        var nextCycle = await RunOneCycleAsync(batchSize: 50);
        Assert.Equal(ReminderProducerResult.Empty, nextCycle);
    }

    // --- 6: concurrency - two genuinely concurrent cycles racing the same backlog leave exactly one
    // outbox item per booking, with the marker set, the unique constraint remaining the real authority ---

    [Fact]
    public async Task TwoConcurrentCycles_RacingTheSameBacklog_LeaveExactlyOneOutboxItemPerBookingWithItsMarkerSet()
    {
        var bookingIds = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            bookingIds.Add(await SeedConfirmedBookingAsync(DateTimeOffset.UtcNow.AddMinutes(5 + i)));
        }

        using var startBarrier = new Barrier(2);
        async Task<ReminderProducerResult> RunRacingCycleAsync()
        {
            await using var dbContext = CreateDbContext();
            var producer = CreateProducer(dbContext);
            var reader = (IBookingReminderReader)new BarrierSynchronizedReader(new BookingReminderReader(dbContext, NullLogger<BookingReminderReader>.Instance), startBarrier);
            var synchronizedProducer = new ReminderProducer(
                reader, new BookingReminderMarker(dbContext), new NotificationOutboxWriter(dbContext, NullLogger<NotificationOutboxWriter>.Instance),
                Options.Create(new BackgroundJobsOptions { Reminder = new ReminderOptions { Enabled = true, LeadTimeMinutes = 60, BatchSize = 50 } }),
                new FakeTimeProvider(DateTimeOffset.UtcNow), NullLogger<ReminderProducer>.Instance);
            return await synchronizedProducer.ProduceDueRemindersAsync(CancellationToken.None);
        }

        var results = await Task.WhenAll(RunRacingCycleAsync(), RunRacingCycleAsync());

        Assert.Equal(10, results.Sum(r => r.EnqueuedCount)); // every booking enqueued by exactly one of the two racers
        Assert.Equal(10, results.Sum(r => r.DuplicateCount) + results.Sum(r => r.EnqueuedCount) - 10); // the loser(s) see AlreadyExists, never a crash

        await using var verify = CreateDbContext();
        foreach (var bookingId in bookingIds)
        {
            Assert.Equal(1, await verify.NotificationOutboxItems.IgnoreQueryFilters().CountAsync(item => item.IdempotencyKey == $"booking:{bookingId}:reminder"));
            Assert.NotNull((await verify.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == bookingId)).ReminderQueuedAtUtc);
        }
    }

    // --- 7: recurring - each occurrence gets its own independent marker ---

    [Fact]
    public async Task EnqueuingOneOccurrencesReminder_NeverMarksAnotherOccurrenceInTheSameSeries()
    {
        var seriesId = Guid.NewGuid();
        await using (var seedContext = CreateDbContext())
        {
            seedContext.RecurringSeries.Add(new RecurringSeries
            {
                Id = seriesId, TenantId = _tenantId, ResourceId = _resourceId, UserId = _userId,
                StartDate = DateOnly.FromDateTime(DateTime.UtcNow), StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0),
                TimeZoneId = "UTC", Frequency = RecurrenceFrequency.Daily, Interval = 1, OccurrenceCount = 2, Quantity = 1,
            });
            await seedContext.SaveChangesAsync();
        }

        var due = await SeedConfirmedBookingAsync(DateTimeOffset.UtcNow.AddMinutes(10), seriesId);
        var notYetDue = await SeedConfirmedBookingAsync(DateTimeOffset.UtcNow.AddHours(5), seriesId);

        await RunOneCycleAsync(batchSize: 50);

        await using var verify = CreateDbContext();
        Assert.NotNull((await verify.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == due)).ReminderQueuedAtUtc);
        Assert.Null((await verify.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == notYetDue)).ReminderQueuedAtUtc);
    }

    // --- 8: cancellation - the marker is never cleared by a later Suppressed outcome ---

    [Fact]
    public async Task AfterAReminderIsEnqueuedAndTheBookingIsThenCancelled_ProcessingItSuppressesItWithoutClearingTheMarker()
    {
        var bookingId = await SeedConfirmedBookingAsync(DateTimeOffset.UtcNow.AddMinutes(30));
        await RunOneCycleAsync(batchSize: 50);

        await using (var cancelContext = CreateDbContext())
        {
            var booking = await cancelContext.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == bookingId);
            booking.Status = BookingStatus.Cancelled;
            await cancelContext.SaveChangesAsync();
        }

        await using var processContext = CreateDbContext();
        var item = await processContext.NotificationOutboxItems.IgnoreQueryFilters().SingleAsync(i => i.IdempotencyKey == $"booking:{bookingId}:reminder");
        var dueItem = new DueNotificationOutboxItem(item.Id, item.TenantId, item.NotificationType, item.RecipientUserId, item.PayloadJson, item.AvailableAtUtc, item.CreatedAtUtc, item.AttemptCount);

        var scopeFactory = BuildProcessorScopeFactory();
        var processor = new NotificationOutboxProcessor(scopeFactory, Options.Create(new BackgroundJobsOptions()), new FakeTimeProvider(DateTimeOffset.UtcNow), NullLogger<NotificationOutboxProcessor>.Instance);

        var outcome = await processor.ProcessItemAsync(dueItem, CancellationToken.None);

        Assert.Equal(NotificationOutboxItemOutcome.Suppressed, outcome);
        await using var verify = CreateDbContext();
        Assert.NotNull((await verify.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == bookingId)).ReminderQueuedAtUtc); // never cleared
    }

    // --- 10 (query/index): a mix of already-marked and not-yet-marked bookings at a somewhat larger
    // volume - the generated-SQL-content proof is in BookingReminderReaderTests
    // (GetDueBookingsAsync_GeneratesASqlQueryThatChecksReminderQueuedAtUtcIsNull); the real execution-plan
    // Index Seek proof against the updated index is in this task's report (sqlcmd/SHOWPLAN XML, the same
    // technique used for the original IX_Bookings_StartUtc verification).

    [Fact]
    public async Task GetDueBookingsAsync_WithAMixOfMarkedAndUnmarkedBookings_ReturnsOnlyTheUnmarkedOnes()
    {
        var unmarked = new List<Guid>();
        for (var i = 0; i < 20; i++)
        {
            unmarked.Add(await SeedConfirmedBookingAsync(DateTimeOffset.UtcNow.AddMinutes(5 + i)));
        }

        for (var i = 0; i < 5; i++)
        {
            await SeedConfirmedBookingAsync(DateTimeOffset.UtcNow.AddMinutes(5 + i), reminderQueuedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1));
        }

        await using var dbContext = CreateDbContext();
        var reader = new BookingReminderReader(dbContext, NullLogger<BookingReminderReader>.Instance);

        var result = await reader.GetDueBookingsAsync(TimeSpan.FromMinutes(60), 50, CancellationToken.None);

        Assert.Equal(20, result.Count);
        Assert.Equal(unmarked.ToHashSet(), result.Select(item => item.BookingId).ToHashSet());
    }

    private async Task<ReminderProducerResult> RunOneCycleAsync(int batchSize)
    {
        await using var dbContext = CreateDbContext();
        var producer = CreateProducer(dbContext, batchSize);
        return await producer.ProduceDueRemindersAsync(CancellationToken.None);
    }

    private static ReminderProducer CreateProducer(BookSpaceDbContext dbContext, int batchSize = 50) => new(
        new BookingReminderReader(dbContext, NullLogger<BookingReminderReader>.Instance),
        new BookingReminderMarker(dbContext),
        new NotificationOutboxWriter(dbContext, NullLogger<NotificationOutboxWriter>.Instance),
        Options.Create(new BackgroundJobsOptions { Reminder = new ReminderOptions { Enabled = true, LeadTimeMinutes = 120, BatchSize = batchSize } }),
        new FakeTimeProvider(DateTimeOffset.UtcNow),
        NullLogger<ReminderProducer>.Instance);

    private IServiceScopeFactory BuildProcessorScopeFactory()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<BookSpaceDbContext>(options => options.UseSqlServer(_connectionString));
        services.AddScoped<ICurrentUserContext>(_ => new FixedCurrentUserContext());
        services.AddScoped<IBookingReminderEligibilityChecker, BookingReminderEligibilityChecker>();
        services.AddScoped<INotificationSender>(sp => new NeverSendsSender(sp.GetRequiredService<IBookingReminderEligibilityChecker>()));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    // Email:Enabled=false already short-circuits SmtpNotificationSender to Permanent before it ever
    // consults eligibility - but this test is specifically about the Suppressed path, which needs the
    // eligibility check to actually run, so this fake plays the same role SmtpNotificationSender's
    // eligibility-consulting branch does, without needing a real SMTP transport in this test.
    private sealed class NeverSendsSender(IBookingReminderEligibilityChecker eligibilityChecker) : INotificationSender
    {
        public async Task<NotificationSendResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken)
        {
            var payload = System.Text.Json.JsonSerializer.Deserialize<BookingNotificationPayload>(message.PayloadJson)!;
            var stillEligible = await eligibilityChecker.IsStillEligibleAsync(message.TenantId, payload.BookingId, cancellationToken);
            return stillEligible ? NotificationSendResult.Success() : NotificationSendResult.Suppressed("No longer eligible.");
        }
    }

    private async Task<Guid> SeedConfirmedBookingAsync(DateTimeOffset startUtc, Guid? seriesId = null, DateTimeOffset? reminderQueuedAtUtc = null)
    {
        var id = Guid.NewGuid();
        await using var dbContext = CreateDbContext();
        dbContext.Bookings.Add(new Booking
        {
            Id = id, TenantId = _tenantId, ResourceId = _resourceId, UserId = _userId, SeriesId = seriesId,
            StartUtc = startUtc, EndUtc = startUtc.AddHours(1), Quantity = 1, Status = BookingStatus.Confirmed,
            ReminderQueuedAtUtc = reminderQueuedAtUtc, CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync();
        return id;
    }

    private BookSpaceDbContext CreateDbContext(params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<BookSpaceDbContext>().UseSqlServer(_connectionString);
        if (interceptors.Length > 0)
        {
            builder.AddInterceptors(interceptors);
        }

        return new BookSpaceDbContext(builder.Options, new FixedCurrentUserContext());
    }

    private sealed class FixedCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId => null;
        public Guid? TenantId => null;
        public IReadOnlyCollection<string> Roles => [];
    }

    // Synchronizes two concurrent ReminderProducer calls so both finish their due-read BEFORE either
    // proceeds to enqueue - without this, one racer would likely finish its whole cycle before the other
    // even started reading, which would never actually exercise the unique-constraint race at all.
    private sealed class BarrierSynchronizedReader(IBookingReminderReader inner, Barrier barrier) : IBookingReminderReader
    {
        public async Task<IReadOnlyList<DueBookingReminder>> GetDueBookingsAsync(TimeSpan leadTime, int batchSize, CancellationToken cancellationToken)
        {
            var result = await inner.GetDueBookingsAsync(leadTime, batchSize, cancellationToken);
            barrier.SignalAndWait(cancellationToken);
            return result;
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

    private sealed class CountingSaveChangesInterceptor : SaveChangesInterceptor
    {
        public int SaveChangesCallCount { get; private set; }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            SaveChangesCallCount++;
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            SaveChangesCallCount++;
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
