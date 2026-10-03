using BookSpace.Application.BackgroundJobs;
using BookSpace.Application.Notifications;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// Proves NotificationOutboxProcessor's per-item isolation, retry/backoff, dead-letter cap, and
// cancellation behavior against a REAL SQL Server (LocalDB) - see docs/background-jobs.md ("Per-item
// processing and retry"). Each test gets its own fresh scope/DbContext per item exactly like the real
// processor does, via a throwaway ServiceCollection pointed at this test class's own LocalDB.
public sealed class NotificationOutboxProcessorTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceNotificationOutboxProcessorTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    private Guid _tenantId;
    private Guid _userId;

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureCreatedAsync();

        _tenantId = Guid.NewGuid();
        _userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        dbContext.Tenants.Add(new Tenant { Id = _tenantId, Name = "Processor Tenant", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = now });
        dbContext.Users.Add(new User
        {
            Id = _userId, TenantId = _tenantId, FirstName = "Processor", LastName = "User",
            Email = $"processor-{Guid.NewGuid():N}@bookspace.test", PasswordHash = "x", CreatedAtUtc = now,
        });
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureDeletedAsync();
    }

    // --- 1/2: success marks Sent, and a Sent item stops being due ---

    [Fact]
    public async Task ProcessItemAsync_WhenSenderSucceeds_MarksTheItemSent()
    {
        var item = await SeedItemAsync();
        var sender = new FakeNotificationSender();
        sender.Enqueue(NotificationSendResult.Success());
        var (processor, _) = CreateProcessor(sender);

        var outcome = await processor.ProcessItemAsync(item, CancellationToken.None);

        Assert.Equal(NotificationOutboxItemOutcome.Sent, outcome);
        var entity = await LoadEntityAsync(item.Id);
        Assert.Equal(NotificationOutboxStatus.Sent, entity.Status);
        Assert.Equal(1, entity.AttemptCount);
    }

    [Fact]
    public async Task AfterProcessing_ASentItem_IsNoLongerReturnedByTheDueBatchReader()
    {
        var item = await SeedItemAsync();
        var sender = new FakeNotificationSender();
        sender.Enqueue(NotificationSendResult.Success());
        var (processor, _) = CreateProcessor(sender);

        await processor.ProcessItemAsync(item, CancellationToken.None);

        Assert.Empty(await GetDueBatchAsync(50));
    }

    // --- 3/4: transient failure bumps AttemptCount exactly once and schedules the correct NextAttempt ---

    [Fact]
    public async Task ProcessItemAsync_WithATransientFailure_IncrementsAttemptCountExactlyOnce()
    {
        var item = await SeedItemAsync();
        var sender = new FakeNotificationSender();
        sender.Enqueue(NotificationSendResult.Transient("temporary outage"));
        var (processor, _) = CreateProcessor(sender);

        await processor.ProcessItemAsync(item, CancellationToken.None);

        Assert.Equal(1, (await LoadEntityAsync(item.Id)).AttemptCount);
    }

    [Fact]
    public async Task ProcessItemAsync_WithATransientFailure_SchedulesTheCorrectNextAttempt()
    {
        var item = await SeedItemAsync();
        var sender = new FakeNotificationSender();
        sender.Enqueue(NotificationSendResult.Transient("temporary outage"));
        var (processor, timeProvider) = CreateProcessor(sender, initialDelaySeconds: 60, maxDelaySeconds: 3600);
        var startUtc = timeProvider.GetUtcNow();

        var outcome = await processor.ProcessItemAsync(item, CancellationToken.None);

        Assert.Equal(NotificationOutboxItemOutcome.RetryScheduled, outcome);
        var entity = await LoadEntityAsync(item.Id);
        Assert.Equal(startUtc.AddSeconds(60), entity.AvailableAtUtc);
        Assert.Equal(NotificationOutboxStatus.Pending, entity.Status);
        Assert.Equal("temporary outage", entity.LastError);
    }

    // --- 5/6: backoff grows per attempt, and never exceeds the configured maximum ---

    [Fact]
    public async Task ProcessItemAsync_AcrossRepeatedTransientFailures_BackoffGrowsEachTime()
    {
        // Same DTO reused for both calls - ProcessItemAsync only ever uses its Id; the real, current
        // AttemptCount always comes from a fresh reload of the actual row, which is the whole point of
        // per-item isolation (see docs/background-jobs.md).
        var item = await SeedItemAsync();
        var sender = new FakeNotificationSender();
        sender.Enqueue(NotificationSendResult.Transient("failure 1"));
        sender.Enqueue(NotificationSendResult.Transient("failure 2"));
        var (processor, timeProvider) = CreateProcessor(sender, maxAttempts: 5, initialDelaySeconds: 60, maxDelaySeconds: 3600);
        var startUtc = timeProvider.GetUtcNow();

        await processor.ProcessItemAsync(item, CancellationToken.None);
        Assert.Equal(startUtc.AddSeconds(60), (await LoadEntityAsync(item.Id)).AvailableAtUtc);

        await processor.ProcessItemAsync(item, CancellationToken.None);
        Assert.Equal(startUtc.AddSeconds(120), (await LoadEntityAsync(item.Id)).AvailableAtUtc);
    }

    [Fact]
    public async Task ProcessItemAsync_WhenComputedBackoffWouldExceedTheMaximum_ClampsToTheMaximum()
    {
        var item = await SeedItemAsync(attemptCount: 10); // the 11th attempt's uncapped backoff would be 60*2^10 = 61,440s
        var sender = new FakeNotificationSender();
        sender.Enqueue(NotificationSendResult.Transient("still failing"));
        var (processor, timeProvider) = CreateProcessor(sender, maxAttempts: 20, initialDelaySeconds: 60, maxDelaySeconds: 300);
        var startUtc = timeProvider.GetUtcNow();

        await processor.ProcessItemAsync(item, CancellationToken.None);

        Assert.Equal(startUtc.AddSeconds(300), (await LoadEntityAsync(item.Id)).AvailableAtUtc);
    }

    // --- 7/8: not due before NextAttemptAtUtc, due again on/after it - real clock, short real wait ---

    [Fact]
    public async Task TransientFailure_MakesTheItemNotDueUntilItsBackoffElapses_ThenDueAgain()
    {
        var item = await SeedItemAsync();
        var sender = new FakeNotificationSender();
        sender.Enqueue(NotificationSendResult.Transient("temporary outage"));
        // Real TimeProvider.System here (not fake) - this test deliberately crosses into the due-batch
        // reader, which always uses the database's own real clock (see docs/background-jobs.md, "Clock
        // source for the due-batch query"); a fake processor clock would silently desynchronize from it.
        var processor = CreateProcessorWithRealClock(sender, initialDelaySeconds: 2, maxDelaySeconds: 60);

        await processor.ProcessItemAsync(item, CancellationToken.None);

        Assert.Empty(await GetDueBatchAsync(50));

        await Task.Delay(TimeSpan.FromSeconds(3));

        var dueAgain = await GetDueBatchAsync(50);
        Assert.Single(dueAgain);
        Assert.Equal(item.Id, dueAgain[0].Id);
    }

    // --- 9/10: exhausting MaxNotificationAttempts dead-letters, and there is never attempt N+1 ---

    [Fact]
    public async Task ProcessItemAsync_WhenTransientFailureExhaustsMaxAttempts_DeadLettersTheItem()
    {
        var item = await SeedItemAsync(attemptCount: 4); // one more failure reaches MaxNotificationAttempts=5
        var sender = new FakeNotificationSender();
        sender.Enqueue(NotificationSendResult.Transient("still failing"));
        var (processor, _) = CreateProcessor(sender, maxAttempts: 5);

        var outcome = await processor.ProcessItemAsync(item, CancellationToken.None);

        Assert.Equal(NotificationOutboxItemOutcome.DeadLettered, outcome);
        var entity = await LoadEntityAsync(item.Id);
        Assert.Equal(NotificationOutboxStatus.DeadLettered, entity.Status);
        Assert.Equal(5, entity.AttemptCount);
    }

    [Fact]
    public async Task ProcessItemAsync_NeverMakesMoreThanMaxNotificationAttemptsRealDeliveryAttempts()
    {
        var item = await SeedItemAsync();
        var sender = new FakeNotificationSender();
        for (var i = 0; i < 10; i++)
        {
            sender.Enqueue(NotificationSendResult.Transient($"failure {i}"));
        }

        var (processor, _) = CreateProcessor(sender, maxAttempts: 3);

        var outcome = NotificationOutboxItemOutcome.RetryScheduled;
        var callCount = 0;
        while (outcome == NotificationOutboxItemOutcome.RetryScheduled && callCount < 10)
        {
            outcome = await processor.ProcessItemAsync(item, CancellationToken.None);
            callCount++;
        }

        Assert.Equal(NotificationOutboxItemOutcome.DeadLettered, outcome);
        Assert.Equal(3, callCount);
        Assert.Equal(3, sender.Calls.Count); // the sender itself was never invoked a 4th time
    }

    // --- 11/12: a permanent failure dead-letters immediately, and a dead-lettered item is never due ---

    [Fact]
    public async Task ProcessItemAsync_WithAPermanentFailure_DeadLettersImmediatelyRegardlessOfAttemptsRemaining()
    {
        var item = await SeedItemAsync(); // plenty of attempts "remaining" under the default MaxNotificationAttempts
        var sender = new FakeNotificationSender();
        sender.Enqueue(NotificationSendResult.Permanent("invalid recipient"));
        var (processor, _) = CreateProcessor(sender, maxAttempts: 5);

        var outcome = await processor.ProcessItemAsync(item, CancellationToken.None);

        Assert.Equal(NotificationOutboxItemOutcome.DeadLettered, outcome);
        var entity = await LoadEntityAsync(item.Id);
        Assert.Equal(NotificationOutboxStatus.DeadLettered, entity.Status);
        Assert.Equal(1, entity.AttemptCount); // exactly one attempt - a permanent failure never retries
    }

    [Fact]
    public async Task AfterDeadLettering_TheItemIsNeverReturnedByTheDueBatchReader()
    {
        var item = await SeedItemAsync();
        var sender = new FakeNotificationSender();
        sender.Enqueue(NotificationSendResult.Permanent("invalid recipient"));
        var (processor, _) = CreateProcessor(sender);

        await processor.ProcessItemAsync(item, CancellationToken.None);

        Assert.Empty(await GetDueBatchAsync(50));
    }

    // --- 13/14/19: per-item isolation - one item's unexpected failure never aborts the batch or rolls
    // back an earlier item's already-committed success; each item uses its own independent DbContext ---

    [Fact]
    public async Task ProcessBatchAsync_WhenOneItemThrowsUnexpectedly_StillProcessesTheOthersAndKeepsTheEarlierSuccessCommitted()
    {
        var item1 = await SeedItemAsync();
        var item2 = await SeedItemAsync();
        var item3 = await SeedItemAsync();
        var sender = new FakeNotificationSender();
        sender.Enqueue(NotificationSendResult.Success());
        sender.EnqueueThrow(new InvalidOperationException("unexpected bug in sender"));
        sender.Enqueue(NotificationSendResult.Success());
        var (processor, _) = CreateProcessor(sender);

        await processor.ProcessBatchAsync([item1, item2, item3], CancellationToken.None);

        Assert.Equal(NotificationOutboxStatus.Sent, (await LoadEntityAsync(item1.Id)).Status);
        var entity2 = await LoadEntityAsync(item2.Id);
        Assert.Equal(NotificationOutboxStatus.Pending, entity2.Status); // untouched - the exception happened before any save
        Assert.Equal(0, entity2.AttemptCount); // the in-memory bump for the attempt that threw was never persisted
        Assert.Equal(NotificationOutboxStatus.Sent, (await LoadEntityAsync(item3.Id)).Status); // the batch continued
    }

    // --- 15: cancellation already requested before the batch starts prevents every item from starting ---

    [Fact]
    public async Task ProcessBatchAsync_WithAnAlreadyCancelledToken_NeverStartsAnyItem()
    {
        var item1 = await SeedItemAsync();
        var item2 = await SeedItemAsync();
        var sender = new FakeNotificationSender();
        var (processor, _) = CreateProcessor(sender);

        await processor.ProcessBatchAsync([item1, item2], new CancellationToken(canceled: true));

        Assert.Empty(sender.Calls);
        var entity1 = await LoadEntityAsync(item1.Id);
        Assert.Equal(NotificationOutboxStatus.Pending, entity1.Status);
        Assert.Equal(0, entity1.AttemptCount);
    }

    // --- 16: cancellation during the send call itself is never treated as a retry-triggering failure ---

    [Fact]
    public async Task ProcessItemAsync_WhenCancelledDuringTheSendCall_PersistsNoAttemptOrRetryState()
    {
        var item = await SeedItemAsync();
        using var cts = new CancellationTokenSource();
        var sender = new FakeNotificationSender { CancelDuringSend = cts };
        var (processor, _) = CreateProcessor(sender);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ProcessItemAsync(item, cts.Token));

        var entity = await LoadEntityAsync(item.Id);
        Assert.Equal(NotificationOutboxStatus.Pending, entity.Status);
        Assert.Equal(0, entity.AttemptCount);
        Assert.Null(entity.LastAttemptAtUtc);
        Assert.Null(entity.LastError);
    }

    // --- 17: invalid retry configuration failing startup validation is covered in
    // BackgroundJobsOptionsValidatorTests (BookSpace.Application.Tests) - pure options validation needs no
    // database and does not belong in this LocalDB-backed suite.

    // --- 18: an unexpected database error is isolated and never reported as a sender transient failure ---

    [Fact]
    public async Task ProcessBatchAsync_WhenADatabaseErrorOccurs_IsolatesItWithoutEverInvokingOrMisreportingTheSender()
    {
        var item = await SeedItemAsync();
        var sender = new FakeNotificationSender();
        sender.Enqueue(NotificationSendResult.Success()); // would succeed IF the sender were ever reached

        // A connection to a database that was never created - the entity load itself fails with a genuine
        // database error before the sender is ever invoked, proving that failure mode is isolated and
        // distinct from anything the sender could report.
        var services = new ServiceCollection();
        services.AddDbContext<BookSpaceDbContext>(options => options.UseSqlServer(
            $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceForcedDbFailure_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True;Connect Timeout=2"));
        services.AddScoped<ICurrentUserContext>(_ => new FixedCurrentUserContext());
        services.AddSingleton<INotificationSender>(sender);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var processor = new NotificationOutboxProcessor(
            scopeFactory, Options.Create(new BackgroundJobsOptions()), new FakeTimeProvider(DateTimeOffset.UtcNow), NullLogger<NotificationOutboxProcessor>.Instance);

        await processor.ProcessBatchAsync([item], CancellationToken.None); // must not throw out - isolated internally

        Assert.Empty(sender.Calls);

        // The real item, on the real (working) connection, remains completely untouched.
        var entity = await LoadEntityAsync(item.Id);
        Assert.Equal(NotificationOutboxStatus.Pending, entity.Status);
        Assert.Equal(0, entity.AttemptCount);
    }

    // --- 20: every stored instant is UTC ---

    [Fact]
    public async Task ProcessItemAsync_StoresAllTimestampsAsUtc()
    {
        var item = await SeedItemAsync();
        var sender = new FakeNotificationSender();
        sender.Enqueue(NotificationSendResult.Transient("temporary"));
        var (processor, _) = CreateProcessor(sender);

        await processor.ProcessItemAsync(item, CancellationToken.None);

        var entity = await LoadEntityAsync(item.Id);
        Assert.Equal(TimeSpan.Zero, entity.LastAttemptAtUtc!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, entity.AvailableAtUtc.Offset);
    }

    // --- Additional focused coverage: LastError sanitization/truncation and clearing on success ---

    [Fact]
    public async Task ProcessItemAsync_TruncatesAnOverlongErrorMessageToTheMaximumStoredLength()
    {
        var item = await SeedItemAsync();
        var sender = new FakeNotificationSender();
        sender.Enqueue(NotificationSendResult.Transient(new string('x', NotificationOutboxItem.MaxLastErrorLength + 100)));
        var (processor, _) = CreateProcessor(sender);

        await processor.ProcessItemAsync(item, CancellationToken.None);

        Assert.Equal(NotificationOutboxItem.MaxLastErrorLength, (await LoadEntityAsync(item.Id)).LastError!.Length);
    }

    [Fact]
    public async Task ProcessItemAsync_OnSuccessAfterAPriorFailure_ClearsTheLastError()
    {
        var item = await SeedItemAsync();
        var sender = new FakeNotificationSender();
        sender.Enqueue(NotificationSendResult.Transient("first failure"));
        sender.Enqueue(NotificationSendResult.Success());
        var (processor, _) = CreateProcessor(sender);

        await processor.ProcessItemAsync(item, CancellationToken.None);
        Assert.NotNull((await LoadEntityAsync(item.Id)).LastError);

        await processor.ProcessItemAsync(item, CancellationToken.None);
        Assert.Null((await LoadEntityAsync(item.Id)).LastError);
    }

    // --- ProcessBatchAsync: structured results + item-level log scope (docs/background-jobs.md,
    // "Notification outbox cycle") ---

    [Fact]
    public async Task ProcessBatchAsync_ReturnsOneResultPerStartedItem_InTheSameOrderAsTheBatch()
    {
        var item1 = await SeedItemAsync();
        var item2 = await SeedItemAsync();
        var sender = new FakeNotificationSender();
        sender.Enqueue(NotificationSendResult.Success());
        sender.Enqueue(NotificationSendResult.Transient("temporary"));
        var (processor, _) = CreateProcessor(sender);

        var results = await processor.ProcessBatchAsync([item1, item2], CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal((item1.Id, NotificationOutboxItemOutcome.Sent), (results[0].OutboxItemId, results[0].Outcome));
        Assert.Equal((item2.Id, NotificationOutboxItemOutcome.RetryScheduled), (results[1].OutboxItemId, results[1].Outcome));
    }

    [Fact]
    public async Task ProcessBatchAsync_WhenAnItemThrowsUnexpectedly_ReportsThatItemWithANullOutcome()
    {
        var item1 = await SeedItemAsync();
        var item2 = await SeedItemAsync();
        var sender = new FakeNotificationSender();
        sender.EnqueueThrow(new InvalidOperationException("boom"));
        sender.Enqueue(NotificationSendResult.Success());
        var (processor, _) = CreateProcessor(sender);

        var results = await processor.ProcessBatchAsync([item1, item2], CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Null(results[0].Outcome);
        Assert.Equal(NotificationOutboxItemOutcome.Sent, results[1].Outcome);
    }

    [Fact]
    public async Task ProcessBatchAsync_WhenCancelledMidBatch_ReturnsOnlyResultsForItemsThatActuallyStarted()
    {
        var item1 = await SeedItemAsync();
        var item2 = await SeedItemAsync();
        var sender = new FakeNotificationSender();
        var cts = new CancellationTokenSource();
        sender.CancelDuringSend = cts; // cancels the shared token the instant the first item's SendAsync is invoked
        var (processor, _) = CreateProcessor(sender);

        var results = await processor.ProcessBatchAsync([item1, item2], cts.Token);

        Assert.Empty(results); // the first item's own attempt was itself cancelled mid-delivery, so it has no result either
        var entity = await LoadEntityAsync(item2.Id);
        Assert.Equal(0, entity.AttemptCount); // the second item was never started
    }

    [Fact]
    public async Task ProcessBatchAsync_LogsEveryItemLineWithinAScopeCarryingOutboxItemIdAndNotificationType()
    {
        var item = await SeedItemAsync();
        var sender = new FakeNotificationSender();
        sender.Enqueue(NotificationSendResult.Success());
        var fakeLogger = new FakeLogger<NotificationOutboxProcessor>();
        var scopeFactory = BuildScopeFactory(sender);
        var processor = new NotificationOutboxProcessor(
            scopeFactory, Options.Create(new BackgroundJobsOptions()), new FakeTimeProvider(DateTimeOffset.UtcNow), fakeLogger);

        await processor.ProcessBatchAsync([item], CancellationToken.None);

        var records = fakeLogger.Collector.GetSnapshot();
        Assert.NotEmpty(records);
        Assert.All(records, record => Assert.Contains(
            record.Scopes,
            scope => scope is IEnumerable<KeyValuePair<string, object?>> pairs
                && pairs.Any(pair => pair.Key == "OutboxItemId" && Equals(pair.Value, item.Id))));
        Assert.All(records, record => Assert.Contains(
            record.Scopes,
            scope => scope is IEnumerable<KeyValuePair<string, object?>> pairs
                && pairs.Any(pair => pair.Key == "NotificationType" && Equals(pair.Value, item.NotificationType))));
    }

    // Proves the per-item isolation/scope logging this task added never incidentally leaks the one thing
    // a NotificationMessage actually carries that the sender needs but logging never should: the
    // recipient's identity or the payload content - see docs/background-jobs.md ("Logging").
    [Fact]
    public async Task ProcessBatchAsync_NeverLogsTheRecipientIdOrThePayload()
    {
        // RecipientUserId must be a real, already-seeded user (FK-enforced) - _userId is distinctive
        // enough on its own (a fresh random Guid per test run) to prove it never leaks into a log line.
        var itemId = Guid.NewGuid();
        var distinctivePayload = $"{{\"marker\":\"{Guid.NewGuid():N}\"}}";

        await using (var dbContext = CreateDbContext())
        {
            dbContext.NotificationOutboxItems.Add(new NotificationOutboxItem
            {
                Id = itemId,
                TenantId = _tenantId,
                NotificationType = "Booking.Confirmation",
                RecipientUserId = _userId,
                PayloadJson = distinctivePayload,
                IdempotencyKey = $"test:{itemId:N}",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                // A minute in the past, not a bare UtcNow - GetDueBatchAsync compares against the
                // DATABASE's own SYSUTCDATETIME() at query time (see NotificationOutboxReader), not this
                // process's clock, so a value "now" by the app's own clock can race against a server clock
                // that is a few milliseconds behind under load. Same safe margin NotificationOutboxReaderTests
                // already uses for exactly this reason.
                AvailableAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                Status = NotificationOutboxStatus.Pending,
                AttemptCount = 0,
            });
            await dbContext.SaveChangesAsync();
        }

        var item = (await GetDueBatchAsync(50)).Single(candidate => candidate.Id == itemId);
        var sender = new FakeNotificationSender();
        sender.Enqueue(NotificationSendResult.Success());
        var fakeLogger = new FakeLogger<NotificationOutboxProcessor>();
        var scopeFactory = BuildScopeFactory(sender);
        var processor = new NotificationOutboxProcessor(
            scopeFactory, Options.Create(new BackgroundJobsOptions()), new FakeTimeProvider(DateTimeOffset.UtcNow), fakeLogger);

        await processor.ProcessBatchAsync([item], CancellationToken.None);

        foreach (var record in fakeLogger.Collector.GetSnapshot())
        {
            Assert.DoesNotContain(_userId.ToString(), record.Message);
            Assert.DoesNotContain("marker", record.Message);
            Assert.DoesNotContain(distinctivePayload, record.Message);
        }
    }

    // --- Test infrastructure ---

    private async Task<DueNotificationOutboxItem> SeedItemAsync(
        DateTimeOffset? availableAtUtc = null, int attemptCount = 0, NotificationOutboxStatus status = NotificationOutboxStatus.Pending)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var available = availableAtUtc ?? now;

        await using var dbContext = CreateDbContext();
        dbContext.NotificationOutboxItems.Add(new NotificationOutboxItem
        {
            Id = id,
            TenantId = _tenantId,
            NotificationType = "Booking.Confirmation",
            RecipientUserId = _userId,
            PayloadJson = "{}",
            IdempotencyKey = $"test:{id:N}",
            CreatedAtUtc = now,
            AvailableAtUtc = available,
            Status = status,
            AttemptCount = attemptCount,
        });
        await dbContext.SaveChangesAsync();

        return new DueNotificationOutboxItem(id, _tenantId, "Booking.Confirmation", _userId, "{}", available, now, attemptCount);
    }

    private async Task<NotificationOutboxItem> LoadEntityAsync(Guid id)
    {
        await using var dbContext = CreateDbContext();
        return await dbContext.NotificationOutboxItems.IgnoreQueryFilters().AsNoTracking().SingleAsync(candidate => candidate.Id == id);
    }

    private async Task<IReadOnlyList<DueNotificationOutboxItem>> GetDueBatchAsync(int batchSize)
    {
        await using var dbContext = CreateDbContext();
        var reader = new NotificationOutboxReader(dbContext, NullLogger<NotificationOutboxReader>.Instance);
        return await reader.GetDueBatchAsync(batchSize, CancellationToken.None);
    }

    private (NotificationOutboxProcessor Processor, FakeTimeProvider TimeProvider) CreateProcessor(
        INotificationSender sender, int maxAttempts = 5, int initialDelaySeconds = 60, int maxDelaySeconds = 3600)
    {
        var scopeFactory = BuildScopeFactory(sender);
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var options = Options.Create(new BackgroundJobsOptions
        {
            MaxNotificationAttempts = maxAttempts,
            InitialRetryDelaySeconds = initialDelaySeconds,
            MaxRetryDelaySeconds = maxDelaySeconds,
        });

        return (new NotificationOutboxProcessor(scopeFactory, options, timeProvider, NullLogger<NotificationOutboxProcessor>.Instance), timeProvider);
    }

    private NotificationOutboxProcessor CreateProcessorWithRealClock(INotificationSender sender, int initialDelaySeconds, int maxDelaySeconds, int maxAttempts = 5)
    {
        var scopeFactory = BuildScopeFactory(sender);
        var options = Options.Create(new BackgroundJobsOptions
        {
            MaxNotificationAttempts = maxAttempts,
            InitialRetryDelaySeconds = initialDelaySeconds,
            MaxRetryDelaySeconds = maxDelaySeconds,
        });

        return new NotificationOutboxProcessor(scopeFactory, options, TimeProvider.System, NullLogger<NotificationOutboxProcessor>.Instance);
    }

    private IServiceScopeFactory BuildScopeFactory(INotificationSender sender)
    {
        var services = new ServiceCollection();
        services.AddDbContext<BookSpaceDbContext>(options => options.UseSqlServer(_connectionString));
        services.AddScoped<ICurrentUserContext>(_ => new FixedCurrentUserContext());
        services.AddSingleton(sender);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private BookSpaceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>().UseSqlServer(_connectionString).Options;
        return new BookSpaceDbContext(options, new FixedCurrentUserContext());
    }

    // TenantId => null, matching the system-wide background context the real processor/reader run under -
    // see docs/tenant-isolation.md and docs/background-jobs.md ("Tenant behavior").
    private sealed class FixedCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId => null;
        public Guid? TenantId => null;
        public IReadOnlyCollection<string> Roles => [];
    }

    // Controllable fake: scripted responses (and/or thrown exceptions) dequeued in order, defaulting to
    // Success() once exhausted. Records every call for assertions on exactly how many delivery attempts
    // were actually made - the direct proof that there is never attempt MaxNotificationAttempts + 1.
    private sealed class FakeNotificationSender : INotificationSender
    {
        private readonly Queue<Func<NotificationSendResult>> _scriptedResponses = new();

        public List<NotificationMessage> Calls { get; } = [];

        // When set, SendAsync cancels this source itself before checking cancellation - simulates
        // "cancellation arrives exactly during this delivery attempt" deterministically, without relying
        // on real concurrent timing.
        public CancellationTokenSource? CancelDuringSend { get; set; }

        public void Enqueue(NotificationSendResult result) => _scriptedResponses.Enqueue(() => result);

        public void EnqueueThrow(Exception exception) => _scriptedResponses.Enqueue(() => throw exception);

        public Task<NotificationSendResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken)
        {
            Calls.Add(message);
            CancelDuringSend?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();

            var respond = _scriptedResponses.Count > 0 ? _scriptedResponses.Dequeue() : () => NotificationSendResult.Success();
            return Task.FromResult(respond());
        }
    }
}
