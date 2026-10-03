using BookSpace.Application.BackgroundJobs;
using BookSpace.Application.Email;
using BookSpace.Application.Logging;
using BookSpace.Application.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BookSpace.Application.Tests.BackgroundJobs;

// NotificationOutboxJobCycle is the real IBackgroundJobCycle that replaced NoOpBackgroundJobCycle - see
// docs/background-jobs.md ("Notification outbox cycle"). Tested here purely against fakes of
// INotificationOutboxReader/INotificationOutboxProcessor (no database, no real sender) - the retry/
// backoff/dead-letter/transient-vs-permanent machinery those two interfaces front is already proven
// against real SQL Server in BookSpace.Infrastructure.Tests; these tests are only about the cycle's own
// orchestration: the Email:Enabled short-circuit, bounded-batch-per-poll, summary tallying from
// structured processor results, correlation, and cancellation propagation.
public sealed class NotificationOutboxJobCycleTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    // --- A: basic cycle ---

    [Fact]
    public async Task RunCycleAsync_WithThreeDueItems_PassesAllThreeToTheProcessorAndReportsAnAccurateSummary()
    {
        var items = new[] { MakeItem(), MakeItem(), MakeItem() };
        var reader = new FakeReader(items);
        var processor = new FakeProcessor((batch, _) => Task.FromResult<IReadOnlyList<NotificationOutboxBatchItemResult>>(
            batch.Select(item => new NotificationOutboxBatchItemResult(item.Id, NotificationOutboxItemOutcome.Sent)).ToList()));
        var (cycle, fakeLogger) = CreateCycle(reader, processor, batchSize: 50);

        await cycle.RunCycleAsync(CancellationToken.None);

        Assert.Equal(items, processor.LastBatch);
        var summary = GetSummaryRecord(fakeLogger);
        AssertState(summary, "PickedCount", "3");
        AssertState(summary, "SucceededCount", "3");
        AssertState(summary, "TransientFailureCount", "0");
        AssertState(summary, "PermanentFailureCount", "0");
        AssertState(summary, "DeadLetterCount", "0");
        AssertState(summary, "UnexpectedFailureCount", "0");
    }

    // --- B: bounded batch ---

    [Fact]
    public async Task RunCycleAsync_RequestsExactlyTheConfiguredBatchSize_AndNeverCallsTheReaderTwice()
    {
        var returned = Enumerable.Range(0, 5).Select(_ => MakeItem()).ToArray();
        var reader = new FakeReader(returned);
        var processor = new FakeProcessor((batch, _) => Task.FromResult<IReadOnlyList<NotificationOutboxBatchItemResult>>(
            batch.Select(item => new NotificationOutboxBatchItemResult(item.Id, NotificationOutboxItemOutcome.Sent)).ToList()));
        var (cycle, _) = CreateCycle(reader, processor, batchSize: 5);

        await cycle.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, reader.CallCount);
        Assert.Equal(5, reader.LastRequestedBatchSize);
        Assert.Equal(5, processor.LastBatch!.Count);
    }

    // --- C: empty batch ---

    [Fact]
    public async Task RunCycleAsync_WhenNothingIsDue_NeverCallsTheProcessorAndReportsZeroPicked()
    {
        var reader = new FakeReader([]);
        var processor = new FakeProcessor((_, _) => throw new InvalidOperationException("ProcessBatchAsync must not be called for an empty batch."));
        var (cycle, fakeLogger) = CreateCycle(reader, processor, batchSize: 50);

        await cycle.RunCycleAsync(CancellationToken.None);

        Assert.False(processor.WasCalled);
        var summary = GetSummaryRecord(fakeLogger);
        AssertState(summary, "PickedCount", "0");
    }

    // --- D: Email disabled ---

    [Fact]
    public async Task RunCycleAsync_WhenEmailIsDisabled_NeverCallsTheReaderOrTheProcessor()
    {
        var reader = new FakeReader([MakeItem()]);
        var processor = new FakeProcessor((_, _) => throw new InvalidOperationException("ProcessBatchAsync must not be called when Email:Enabled is false."));
        var (cycle, fakeLogger) = CreateCycle(reader, processor, batchSize: 50, emailEnabled: false);

        await cycle.RunCycleAsync(CancellationToken.None);

        Assert.Equal(0, reader.CallCount);
        Assert.False(processor.WasCalled);
        Assert.Contains(fakeLogger.Collector.GetSnapshot(), record => record.Message.Contains("Email:Enabled is false"));
    }

    // --- E: per-item isolation (tallying a mixed-outcome batch correctly) ---

    [Fact]
    public async Task RunCycleAsync_WithAMixOfSuccessAndTransientOutcomes_TalliesEachCorrectly()
    {
        var items = new[] { MakeItem(), MakeItem(), MakeItem() };
        var reader = new FakeReader(items);
        var processor = new FakeProcessor((batch, _) => Task.FromResult<IReadOnlyList<NotificationOutboxBatchItemResult>>(
        [
            new NotificationOutboxBatchItemResult(batch[0].Id, NotificationOutboxItemOutcome.Sent),
            new NotificationOutboxBatchItemResult(batch[1].Id, NotificationOutboxItemOutcome.RetryScheduled),
            new NotificationOutboxBatchItemResult(batch[2].Id, NotificationOutboxItemOutcome.Sent),
        ]));
        var (cycle, fakeLogger) = CreateCycle(reader, processor, batchSize: 50);

        await cycle.RunCycleAsync(CancellationToken.None);

        var summary = GetSummaryRecord(fakeLogger);
        AssertState(summary, "SucceededCount", "2");
        AssertState(summary, "TransientFailureCount", "1");
        AssertState(summary, "PickedCount", "3");
    }

    // --- F: permanent/dead-letter counting ---

    [Fact]
    public async Task RunCycleAsync_WithDeadLetteredItems_CountsThemAsBothPermanentAndDeadLetterAndKeepsGoing()
    {
        var items = new[] { MakeItem(), MakeItem(), MakeItem() };
        var reader = new FakeReader(items);
        var processor = new FakeProcessor((batch, _) => Task.FromResult<IReadOnlyList<NotificationOutboxBatchItemResult>>(
        [
            new NotificationOutboxBatchItemResult(batch[0].Id, NotificationOutboxItemOutcome.DeadLettered),
            new NotificationOutboxBatchItemResult(batch[1].Id, NotificationOutboxItemOutcome.DeadLettered),
            new NotificationOutboxBatchItemResult(batch[2].Id, NotificationOutboxItemOutcome.Sent),
        ]));
        var (cycle, fakeLogger) = CreateCycle(reader, processor, batchSize: 50);

        await cycle.RunCycleAsync(CancellationToken.None);

        var summary = GetSummaryRecord(fakeLogger);
        AssertState(summary, "DeadLetterCount", "2");
        AssertState(summary, "PermanentFailureCount", "2");
        AssertState(summary, "SucceededCount", "1");
    }

    // --- G: unexpected exception for one item does not kill the cycle ---

    [Fact]
    public async Task RunCycleAsync_WhenTheProcessorReportsAnUnexpectedFailureForOneItem_StillCompletesNormally()
    {
        var items = new[] { MakeItem(), MakeItem() };
        var reader = new FakeReader(items);
        var processor = new FakeProcessor((batch, _) => Task.FromResult<IReadOnlyList<NotificationOutboxBatchItemResult>>(
        [
            new NotificationOutboxBatchItemResult(batch[0].Id, Outcome: null), // the processor's own catch already logged this
            new NotificationOutboxBatchItemResult(batch[1].Id, NotificationOutboxItemOutcome.Sent),
        ]));
        var (cycle, fakeLogger) = CreateCycle(reader, processor, batchSize: 50);

        await cycle.RunCycleAsync(CancellationToken.None); // must not throw

        var summary = GetSummaryRecord(fakeLogger);
        AssertState(summary, "UnexpectedFailureCount", "1");
        AssertState(summary, "SucceededCount", "1");
    }

    // --- H: cancellation ---

    [Fact]
    public async Task RunCycleAsync_WithAnAlreadyCancelledToken_NeverCallsTheReaderOrProcessor()
    {
        var reader = new FakeReader([MakeItem()]);
        var processor = new FakeProcessor((_, _) => throw new InvalidOperationException("must not be called"));
        var (cycle, _) = CreateCycle(reader, processor, batchSize: 50);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => cycle.RunCycleAsync(new CancellationToken(canceled: true)));

        Assert.Equal(0, reader.CallCount);
        Assert.False(processor.WasCalled);
    }

    // Simulates either the stopping token being cancelled or the lease being lost mid-cycle - from this
    // cycle's own point of view those are indistinguishable (BackgroundJobsWorker already links both into
    // the one token it passes down - see BackgroundJobsWorkerTests.RunCycleAsync_WhenTheLeaseIsLostMidCycle_CancelsTheCyclesToken
    // for the proof that linking itself works). What this cycle must get right, regardless of which token
    // fired, is: never start the next item, never emit a fake "completed successfully" summary, and let
    // OperationCanceledException propagate so the worker's own shutdown-vs-lease-lost handling still sees
    // a real cancellation.
    [Fact]
    public async Task RunCycleAsync_WhenCancelledMidBatch_PropagatesCancellationAndNeverLogsANormalSummary()
    {
        var items = new[] { MakeItem(), MakeItem(), MakeItem() };
        var reader = new FakeReader(items);
        using var cts = new CancellationTokenSource();
        var processor = new FakeProcessor((batch, _) =>
        {
            // Simulates the processor having completed exactly one item before the lease/stopping token
            // fired - mirrors NotificationOutboxProcessor.ProcessBatchAsync's own real contract: it never
            // throws for an ordinary cancellation, it just stops and returns a prefix of results.
            cts.Cancel();
            return Task.FromResult<IReadOnlyList<NotificationOutboxBatchItemResult>>(
                [new NotificationOutboxBatchItemResult(batch[0].Id, NotificationOutboxItemOutcome.Sent)]);
        });
        var (cycle, fakeLogger) = CreateCycle(reader, processor, batchSize: 50);

        await Assert.ThrowsAsync<OperationCanceledException>(() => cycle.RunCycleAsync(cts.Token));

        Assert.Equal(1, reader.CallCount);
        Assert.DoesNotContain(fakeLogger.Collector.GetSnapshot(), record => record.Message.Contains("cycle completed"));
        Assert.Contains(fakeLogger.Collector.GetSnapshot(), record => record.Message.Contains("interrupted"));
    }

    // --- I: correlation ---

    [Fact]
    public async Task RunCycleAsync_AcrossTwoSeparateRuns_UsesADifferentCorrelationIdEachTime_WithEveryLineOfOneRunSharingItsOwnId()
    {
        var reader = new FakeReader([]);
        var processor = new FakeProcessor((_, _) => throw new InvalidOperationException("must not be called for an empty batch"));
        var (cycle, fakeLogger) = CreateCycle(reader, processor, batchSize: 50);

        await cycle.RunCycleAsync(CancellationToken.None);
        var firstRunRecords = fakeLogger.Collector.GetSnapshot();
        var firstRunIds = firstRunRecords.Select(record => GetScopeValue(record, "CorrelationId")).Distinct().ToList();

        fakeLogger.Collector.Clear();

        await cycle.RunCycleAsync(CancellationToken.None);
        var secondRunRecords = fakeLogger.Collector.GetSnapshot();
        var secondRunIds = secondRunRecords.Select(record => GetScopeValue(record, "CorrelationId")).Distinct().ToList();

        // At least the "cycle started" + the empty-batch summary line were emitted in each run.
        Assert.True(firstRunRecords.Count >= 2);
        Assert.True(secondRunRecords.Count >= 2);

        Assert.Single(firstRunIds);
        Assert.Single(secondRunIds);
        Assert.NotNull(firstRunIds[0]);
        Assert.NotEqual(firstRunIds[0], secondRunIds[0]);
    }

    [Fact]
    public async Task RunCycleAsync_NeverLogsAnyPayloadOrRecipientData()
    {
        var items = new[] { MakeItem() };
        var reader = new FakeReader(items);
        var processor = new FakeProcessor((batch, _) => Task.FromResult<IReadOnlyList<NotificationOutboxBatchItemResult>>(
            [new NotificationOutboxBatchItemResult(batch[0].Id, NotificationOutboxItemOutcome.Sent)]));
        var (cycle, fakeLogger) = CreateCycle(reader, processor, batchSize: 50);

        await cycle.RunCycleAsync(CancellationToken.None);

        foreach (var record in fakeLogger.Collector.GetSnapshot())
        {
            Assert.DoesNotContain("PayloadJson", record.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("not-a-real-payload-marker", record.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    // --- J: summary fields + duration ---

    [Fact]
    public async Task RunCycleAsync_OnNormalCompletion_LogsExactlyOneSummaryLineWithATestableDuration()
    {
        var items = new[] { MakeItem() };
        var reader = new FakeReader(items);
        var timeProvider = new FakeTimeProvider();
        var processor = new FakeProcessor(async (batch, _) =>
        {
            timeProvider.Advance(TimeSpan.FromMilliseconds(250));
            return [new NotificationOutboxBatchItemResult(batch[0].Id, NotificationOutboxItemOutcome.Sent)];
        });
        var (cycle, fakeLogger) = CreateCycle(reader, processor, batchSize: 50, timeProvider: timeProvider);

        await cycle.RunCycleAsync(CancellationToken.None);

        var summaryRecords = fakeLogger.Collector.GetSnapshot().Where(record => record.Message.Contains("cycle completed")).ToList();
        Assert.Single(summaryRecords);
        var elapsedText = GetState(summaryRecords[0], "ElapsedMilliseconds");
        Assert.NotNull(elapsedText);
        var elapsed = double.Parse(elapsedText!);
        Assert.True(elapsed >= 250);
    }

    private static DueNotificationOutboxItem MakeItem() => new(
        Guid.NewGuid(), TenantId, "Booking.Confirmation", Guid.NewGuid(), "not-a-real-payload-marker", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, AttemptCount: 0);

    private static (NotificationOutboxJobCycle Cycle, FakeLogger<NotificationOutboxJobCycle> Logger) CreateCycle(
        FakeReader reader, FakeProcessor processor, int batchSize, bool emailEnabled = true, TimeProvider? timeProvider = null)
    {
        var fakeLogger = new FakeLogger<NotificationOutboxJobCycle>();
        var cycle = new NotificationOutboxJobCycle(
            reader,
            processor,
            Options.Create(new BackgroundJobsOptions { BatchSize = batchSize }),
            Options.Create(new EmailOptions { Enabled = emailEnabled }),
            new FakeInstanceIdentity(),
            new CorrelationIdContext(),
            timeProvider ?? new FakeTimeProvider(),
            fakeLogger);
        return (cycle, fakeLogger);
    }

    private static FakeLogRecord GetSummaryRecord(FakeLogger<NotificationOutboxJobCycle> fakeLogger) =>
        Assert.Single(fakeLogger.Collector.GetSnapshot(), record => record.Message.Contains("cycle completed"));

    private static void AssertState(FakeLogRecord record, string key, string expected) =>
        Assert.Equal(expected, GetState(record, key));

    private static string? GetState(FakeLogRecord record, string key) =>
        record.StructuredState?.FirstOrDefault(pair => pair.Key == key).Value;

    private static string? GetScopeValue(FakeLogRecord record, string key)
    {
        foreach (var scope in record.Scopes)
        {
            if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                {
                    if (pair.Key == key)
                    {
                        return pair.Value?.ToString();
                    }
                }
            }
        }

        return null;
    }

    private sealed class FakeInstanceIdentity : IBackgroundJobInstanceIdentity
    {
        public string OwnerId => "fake-owner";
    }

    private sealed class FakeReader(IReadOnlyList<DueNotificationOutboxItem> itemsToReturn) : INotificationOutboxReader
    {
        public int CallCount { get; private set; }
        public int LastRequestedBatchSize { get; private set; }

        public Task<IReadOnlyList<DueNotificationOutboxItem>> GetDueBatchAsync(int batchSize, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastRequestedBatchSize = batchSize;
            return Task.FromResult(itemsToReturn);
        }
    }

    private sealed class FakeProcessor(
        Func<IReadOnlyList<DueNotificationOutboxItem>, CancellationToken, Task<IReadOnlyList<NotificationOutboxBatchItemResult>>> onProcessBatch)
        : INotificationOutboxProcessor
    {
        public bool WasCalled { get; private set; }
        public IReadOnlyList<DueNotificationOutboxItem>? LastBatch { get; private set; }

        public Task<NotificationOutboxItemOutcome> ProcessItemAsync(DueNotificationOutboxItem item, CancellationToken cancellationToken) =>
            throw new NotSupportedException("NotificationOutboxJobCycle is expected to call ProcessBatchAsync, never ProcessItemAsync directly.");

        public Task<IReadOnlyList<NotificationOutboxBatchItemResult>> ProcessBatchAsync(
            IReadOnlyList<DueNotificationOutboxItem> batch, CancellationToken cancellationToken)
        {
            WasCalled = true;
            LastBatch = batch;
            return onProcessBatch(batch, cancellationToken);
        }
    }
}
