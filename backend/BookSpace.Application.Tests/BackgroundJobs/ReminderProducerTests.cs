using BookSpace.Application.BackgroundJobs;
using BookSpace.Application.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BookSpace.Application.Tests.BackgroundJobs;

// Pure orchestration tests - no database. Proves ReminderProducer correctly wires
// IBookingReminderReader -> [IBookingReminderMarker.StageReminderQueued, staged BEFORE the writer call] ->
// BookingNotificationFactory.Reminder -> INotificationOutboxWriter.EnqueueManyAsync, and tallies the
// structured result - using fakes for all three dependencies (see docs/background-jobs.md, "Reminder job -
// the ReminderQueuedAtUtc marker"). The actual atomic-commit/rollback/duplicate-recovery proof needs a real
// DbContext and lives in BookSpace.Infrastructure.Tests (BookingReminderBacklogProgressionTests); these
// tests are only about ReminderProducer's own orchestration order and arguments.
public sealed class ReminderProducerTests
{
    [Fact]
    public async Task ProduceDueRemindersAsync_WhenDisabled_NeverCallsTheReaderMarkerOrWriter()
    {
        var reader = new FakeBookingReminderReader([SampleCandidate()]);
        var marker = new FakeBookingReminderMarker();
        var writer = new FakeNotificationOutboxWriter();
        var producer = CreateProducer(reader, marker, writer, enabled: false);

        var result = await producer.ProduceDueRemindersAsync(CancellationToken.None);

        Assert.Equal(ReminderProducerResult.Empty, result);
        Assert.False(reader.WasCalled);
        Assert.Empty(marker.StagedBookingIds);
        Assert.Empty(writer.EnqueuedBatches);
    }

    [Fact]
    public async Task ProduceDueRemindersAsync_WithNoCandidates_NeverCallsTheMarkerOrWriter()
    {
        var reader = new FakeBookingReminderReader([]);
        var marker = new FakeBookingReminderMarker();
        var writer = new FakeNotificationOutboxWriter();
        var producer = CreateProducer(reader, marker, writer, enabled: true);

        var result = await producer.ProduceDueRemindersAsync(CancellationToken.None);

        Assert.Equal(ReminderProducerResult.Empty, result);
        Assert.Empty(marker.StagedBookingIds);
        Assert.Empty(writer.EnqueuedBatches);
    }

    [Fact]
    public async Task ProduceDueRemindersAsync_PassesTheConfiguredLeadTimeAndBatchSizeToTheReader()
    {
        var reader = new FakeBookingReminderReader([]);
        var producer = CreateProducer(reader, new FakeBookingReminderMarker(), new FakeNotificationOutboxWriter(), enabled: true, leadTimeMinutes: 90, batchSize: 25);

        await producer.ProduceDueRemindersAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(90), reader.LastLeadTime);
        Assert.Equal(25, reader.LastBatchSize);
    }

    [Fact]
    public async Task ProduceDueRemindersAsync_WithCandidates_EnqueuesTheWholeBatchInOneWriterCall()
    {
        var candidates = new[] { SampleCandidate(), SampleCandidate(), SampleCandidate() };
        var reader = new FakeBookingReminderReader(candidates);
        var writer = new FakeNotificationOutboxWriter(outcome: EnqueueOutcome.Created);
        var producer = CreateProducer(reader, new FakeBookingReminderMarker(), writer, enabled: true);

        var result = await producer.ProduceDueRemindersAsync(CancellationToken.None);

        Assert.Equal(3, result.CandidateCount);
        Assert.Equal(3, result.EnqueuedCount);
        Assert.Equal(0, result.DuplicateCount);
        Assert.Single(writer.EnqueuedBatches); // one EnqueueManyAsync call for the whole batch - never one SaveChanges per booking
        Assert.Equal(3, writer.EnqueuedBatches[0].Count);
        Assert.All(writer.EnqueuedBatches[0], request => Assert.Equal(BookingNotificationTypes.Reminder, request.NotificationType));
    }

    [Fact]
    public async Task ProduceDueRemindersAsync_TalliesCreatedAndAlreadyExistsSeparately()
    {
        var candidates = new[] { SampleCandidate(), SampleCandidate() };
        var reader = new FakeBookingReminderReader(candidates);
        var writer = new FakeNotificationOutboxWriter(outcomes:
        [
            new EnqueueNotificationResult(EnqueueOutcome.Created, Guid.NewGuid()),
            new EnqueueNotificationResult(EnqueueOutcome.AlreadyExists, null),
        ]);
        var producer = CreateProducer(reader, new FakeBookingReminderMarker(), writer, enabled: true);

        var result = await producer.ProduceDueRemindersAsync(CancellationToken.None);

        Assert.Equal(2, result.CandidateCount);
        Assert.Equal(1, result.EnqueuedCount);
        Assert.Equal(1, result.DuplicateCount);
    }

    [Fact]
    public async Task ProduceDueRemindersAsync_WithAnAlreadyCancelledToken_NeverCallsTheReaderMarkerOrWriter()
    {
        var reader = new FakeBookingReminderReader([SampleCandidate()]);
        var marker = new FakeBookingReminderMarker();
        var writer = new FakeNotificationOutboxWriter();
        var producer = CreateProducer(reader, marker, writer, enabled: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => producer.ProduceDueRemindersAsync(new CancellationToken(canceled: true)));

        Assert.False(reader.WasCalled);
        Assert.Empty(marker.StagedBookingIds);
        Assert.Empty(writer.EnqueuedBatches);
    }

    // --- The marker: staged for EVERY candidate, before the writer is called, regardless of what the
    // writer's eventual per-item outcome turns out to be ---

    [Fact]
    public async Task ProduceDueRemindersAsync_StagesTheMarkerForEveryCandidateBeforeCallingTheWriter()
    {
        var candidates = new[] { SampleCandidate(), SampleCandidate(), SampleCandidate() };
        var reader = new FakeBookingReminderReader(candidates);
        var marker = new FakeBookingReminderMarker();
        var writer = new FakeNotificationOutboxWriter(outcome: EnqueueOutcome.Created);
        writer.OnEnqueueManyAsync = () => Assert.Equal(3, marker.StagedBookingIds.Count); // already staged by the time the writer runs
        var producer = CreateProducer(reader, marker, writer, enabled: true);

        await producer.ProduceDueRemindersAsync(CancellationToken.None);

        Assert.Equal(candidates.Select(c => c.BookingId).ToHashSet(), marker.StagedBookingIds.ToHashSet());
    }

    // The recovery scenario at this orchestration level: even a candidate whose outbox row turns out to
    // already exist (AlreadyExists) still gets its marker staged - the real atomic "does this actually fix
    // the marker in the database" proof is a LocalDB-level test (BookingReminderBacklogProgressionTests),
    // but ReminderProducer's own job is to stage unconditionally, never only for the "new" subset.
    [Fact]
    public async Task ProduceDueRemindersAsync_StagesTheMarkerEvenForAnAlreadyExistingOutboxRow()
    {
        var candidate = SampleCandidate();
        var reader = new FakeBookingReminderReader([candidate]);
        var marker = new FakeBookingReminderMarker();
        var writer = new FakeNotificationOutboxWriter(outcome: EnqueueOutcome.AlreadyExists);
        var producer = CreateProducer(reader, marker, writer, enabled: true);

        await producer.ProduceDueRemindersAsync(CancellationToken.None);

        Assert.Contains(candidate.BookingId, marker.StagedBookingIds);
    }

    [Fact]
    public async Task ProduceDueRemindersAsync_StagesTheMarkerWithTheCurrentTimeProviderInstant()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T12:00:00Z"));
        var candidate = SampleCandidate();
        var reader = new FakeBookingReminderReader([candidate]);
        var marker = new FakeBookingReminderMarker();
        var producer = CreateProducer(reader, marker, new FakeNotificationOutboxWriter(), enabled: true, timeProvider: timeProvider);

        await producer.ProduceDueRemindersAsync(CancellationToken.None);

        Assert.Equal(timeProvider.GetUtcNow(), marker.LastQueuedAtUtc);
    }

    private static DueBookingReminder SampleCandidate() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        DateTimeOffset.UtcNow.AddMinutes(30), DateTimeOffset.UtcNow.AddMinutes(90), 1);

    private static ReminderProducer CreateProducer(
        FakeBookingReminderReader reader, FakeBookingReminderMarker marker, FakeNotificationOutboxWriter writer,
        bool enabled, int leadTimeMinutes = 60, int batchSize = 50, FakeTimeProvider? timeProvider = null)
    {
        var options = Options.Create(new BackgroundJobsOptions
        {
            Reminder = new ReminderOptions { Enabled = enabled, LeadTimeMinutes = leadTimeMinutes, BatchSize = batchSize },
        });
        return new ReminderProducer(reader, marker, writer, options, timeProvider ?? new FakeTimeProvider(), NullLogger<ReminderProducer>.Instance);
    }

    private sealed class FakeBookingReminderReader(IReadOnlyList<DueBookingReminder> candidates) : IBookingReminderReader
    {
        public bool WasCalled { get; private set; }
        public TimeSpan? LastLeadTime { get; private set; }
        public int? LastBatchSize { get; private set; }

        public Task<IReadOnlyList<DueBookingReminder>> GetDueBookingsAsync(TimeSpan leadTime, int batchSize, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WasCalled = true;
            LastLeadTime = leadTime;
            LastBatchSize = batchSize;
            return Task.FromResult(candidates);
        }
    }

    private sealed class FakeBookingReminderMarker : IBookingReminderMarker
    {
        public List<Guid> StagedBookingIds { get; } = [];
        public DateTimeOffset? LastQueuedAtUtc { get; private set; }

        public void StageReminderQueued(Guid bookingId, DateTimeOffset queuedAtUtc)
        {
            StagedBookingIds.Add(bookingId);
            LastQueuedAtUtc = queuedAtUtc;
        }
    }

    private sealed class FakeNotificationOutboxWriter : INotificationOutboxWriter
    {
        private readonly Queue<EnqueueNotificationResult> _scriptedResults;

        public FakeNotificationOutboxWriter(EnqueueOutcome outcome = EnqueueOutcome.Created)
            : this(outcomes: null)
        {
            _defaultOutcome = outcome;
        }

        public FakeNotificationOutboxWriter(IReadOnlyList<EnqueueNotificationResult>? outcomes)
        {
            _scriptedResults = new Queue<EnqueueNotificationResult>(outcomes ?? []);
        }

        private readonly EnqueueOutcome _defaultOutcome = EnqueueOutcome.Created;

        public List<IReadOnlyList<NotificationOutboxRequest>> EnqueuedBatches { get; } = [];
        public Action? OnEnqueueManyAsync { get; set; }

        public Task<EnqueueNotificationResult> EnqueueAsync(NotificationOutboxRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("ReminderProducer only ever calls EnqueueManyAsync.");

        public Task<IReadOnlyList<EnqueueNotificationResult>> EnqueueManyAsync(
            IReadOnlyList<NotificationOutboxRequest> requests, CancellationToken cancellationToken)
        {
            OnEnqueueManyAsync?.Invoke();
            EnqueuedBatches.Add(requests);
            var results = requests
                .Select(_ => _scriptedResults.Count > 0 ? _scriptedResults.Dequeue() : new EnqueueNotificationResult(_defaultOutcome, Guid.NewGuid()))
                .ToList();
            return Task.FromResult<IReadOnlyList<EnqueueNotificationResult>>(results);
        }
    }
}
