using BookSpace.Application.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookSpace.Application.BackgroundJobs;

// See IReminderProducer for the contract and docs/background-jobs.md ("Reminder job") for the full
// design. Pure orchestration - every dependency here is an Application-layer abstraction
// (IBookingReminderReader/INotificationOutboxWriter), with the actual EF Core/SQL Server specifics living
// entirely in their Infrastructure implementations, the same layering NotificationOutboxJobCycle itself
// already follows for the dispatch side.
//
// Deliberately relies ENTIRELY on INotificationOutboxWriter.EnqueueManyAsync's (TenantId, IdempotencyKey)
// database constraint for duplicate prevention, rather than having IBookingReminderReader's query try to
// exclude bookings that already have a reminder enqueued: NotificationOutboxItems has no BookingId column
// to join on, only the composed IdempotencyKey string ("booking:{id}:reminder"), and translating that
// composed-key comparison into a reliable, efficient SQL anti-join is exactly the kind of fragile,
// easy-to-get-subtly-wrong cross-type SQL translation docs/background-jobs.md explicitly chose to avoid
// here - the enqueue-time constraint is already a NECESSARY backstop against a concurrent/retried write
// race regardless of whether a read-side pre-filter also exists, so adding one buys, at best, avoiding a
// cheap "AlreadyExists" round trip for a booking that re-appears as a candidate on back-to-back polls
// while it's still inside the lead-time window - never a correctness improvement.
public sealed class ReminderProducer(
    IBookingReminderReader reader,
    INotificationOutboxWriter writer,
    IOptions<BackgroundJobsOptions> options,
    ILogger<ReminderProducer> logger) : IReminderProducer
{
    public async Task<ReminderProducerResult> ProduceDueRemindersAsync(CancellationToken cancellationToken)
    {
        var reminderOptions = options.Value.Reminder;
        if (!reminderOptions.Enabled)
        {
            logger.LogInformation("Reminder producer skipped - BackgroundJobs:Reminder:Enabled is false.");
            return ReminderProducerResult.Empty;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var leadTime = TimeSpan.FromMinutes(reminderOptions.LeadTimeMinutes);
        var candidates = await reader.GetDueBookingsAsync(leadTime, reminderOptions.BatchSize, cancellationToken);

        if (candidates.Count == 0)
        {
            return ReminderProducerResult.Empty;
        }

        var requests = candidates.Select(BookingNotificationFactory.Reminder).ToList();
        var results = await writer.EnqueueManyAsync(requests, cancellationToken);

        var enqueuedCount = results.Count(result => result.Outcome == EnqueueOutcome.Created);
        var duplicateCount = results.Count(result => result.Outcome == EnqueueOutcome.AlreadyExists);

        logger.LogInformation(
            "Reminder producer completed. CandidateCount={CandidateCount} EnqueuedCount={EnqueuedCount} DuplicateCount={DuplicateCount}",
            candidates.Count,
            enqueuedCount,
            duplicateCount);

        return new ReminderProducerResult(candidates.Count, enqueuedCount, duplicateCount);
    }
}
