using BookSpace.Application.BackgroundJobs;
using BookSpace.Application.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BookSpace.Application.Tests.BackgroundJobs;

// Pure orchestration tests - no database. Proves ReminderProducer correctly wires
// IBookingReminderReader -> BookingNotificationFactory.Reminder -> INotificationOutboxWriter.EnqueueManyAsync
// and tallies the structured result, using fakes for both dependencies (see docs/background-jobs.md,
// "Reminder job").
public sealed class ReminderProducerTests
{
    [Fact]
    public async Task ProduceDueRemindersAsync_WhenDisabled_NeverCallsTheReaderOrTheWriter()
    {
        var reader = new FakeBookingReminderReader([SampleCandidate()]);
        var writer = new FakeNotificationOutboxWriter();
        var producer = CreateProducer(reader, writer, enabled: false);

        var result = await producer.ProduceDueRemindersAsync(CancellationToken.None);

        Assert.Equal(ReminderProducerResult.Empty, result);
        Assert.False(reader.WasCalled);
        Assert.Empty(writer.EnqueuedBatches);
    }

    [Fact]
    public async Task ProduceDueRemindersAsync_WithNoCandidates_ReturnsEmptyWithoutCallingTheWriter()
    {
        var reader = new FakeBookingReminderReader([]);
        var writer = new FakeNotificationOutboxWriter();
        var producer = CreateProducer(reader, writer, enabled: true);

        var result = await producer.ProduceDueRemindersAsync(CancellationToken.None);

        Assert.Equal(ReminderProducerResult.Empty, result);
        Assert.Empty(writer.EnqueuedBatches);
    }

    [Fact]
    public async Task ProduceDueRemindersAsync_PassesTheConfiguredLeadTimeAndBatchSizeToTheReader()
    {
        var reader = new FakeBookingReminderReader([]);
        var writer = new FakeNotificationOutboxWriter();
        var producer = CreateProducer(reader, writer, enabled: true, leadTimeMinutes: 90, batchSize: 25);

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
        var producer = CreateProducer(reader, writer, enabled: true);

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
        var producer = CreateProducer(reader, writer, enabled: true);

        var result = await producer.ProduceDueRemindersAsync(CancellationToken.None);

        Assert.Equal(2, result.CandidateCount);
        Assert.Equal(1, result.EnqueuedCount);
        Assert.Equal(1, result.DuplicateCount);
    }

    [Fact]
    public async Task ProduceDueRemindersAsync_WithAnAlreadyCancelledToken_NeverCallsTheReaderOrTheWriter()
    {
        var reader = new FakeBookingReminderReader([SampleCandidate()]);
        var writer = new FakeNotificationOutboxWriter();
        var producer = CreateProducer(reader, writer, enabled: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => producer.ProduceDueRemindersAsync(new CancellationToken(canceled: true)));

        Assert.False(reader.WasCalled);
        Assert.Empty(writer.EnqueuedBatches);
    }

    private static DueBookingReminder SampleCandidate() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        DateTimeOffset.UtcNow.AddMinutes(30), DateTimeOffset.UtcNow.AddMinutes(90), 1);

    private static ReminderProducer CreateProducer(
        FakeBookingReminderReader reader, FakeNotificationOutboxWriter writer, bool enabled, int leadTimeMinutes = 60, int batchSize = 50)
    {
        var options = Options.Create(new BackgroundJobsOptions
        {
            Reminder = new ReminderOptions { Enabled = enabled, LeadTimeMinutes = leadTimeMinutes, BatchSize = batchSize },
        });
        return new ReminderProducer(reader, writer, options, NullLogger<ReminderProducer>.Instance);
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

        public Task<EnqueueNotificationResult> EnqueueAsync(NotificationOutboxRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("ReminderProducer only ever calls EnqueueManyAsync.");

        public Task<IReadOnlyList<EnqueueNotificationResult>> EnqueueManyAsync(
            IReadOnlyList<NotificationOutboxRequest> requests, CancellationToken cancellationToken)
        {
            EnqueuedBatches.Add(requests);
            var results = requests
                .Select(_ => _scriptedResults.Count > 0 ? _scriptedResults.Dequeue() : new EnqueueNotificationResult(_defaultOutcome, Guid.NewGuid()))
                .ToList();
            return Task.FromResult<IReadOnlyList<EnqueueNotificationResult>>(results);
        }
    }
}
