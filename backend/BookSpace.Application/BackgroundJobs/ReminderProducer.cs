using BookSpace.Application.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookSpace.Application.BackgroundJobs;

// See IReminderProducer for the contract and docs/background-jobs.md ("Reminder job") for the full
// design. Pure orchestration - every dependency here is an Application-layer abstraction
// (IBookingReminderReader/IBookingReminderMarker/INotificationOutboxWriter), with the actual EF Core/SQL
// Server specifics living entirely in their Infrastructure implementations, the same layering
// NotificationOutboxJobCycle itself already follows for the dispatch side.
//
// Two distinct mechanisms protect two distinct things here - see docs/background-jobs.md ("Reminder job -
// the ReminderQueuedAtUtc marker") for the full writeup:
//   - INotificationOutboxWriter.EnqueueManyAsync's (TenantId, IdempotencyKey) database unique constraint
//     remains the sole, final authority against a duplicate outbox ROW ever existing - including under a
//     genuinely concurrent/retried write race. Nothing here replaces or weakens that.
//   - Booking.ReminderQueuedAtUtc (staged via IBookingReminderMarker, BEFORE EnqueueManyAsync is called, so
//     its own internal SaveChangesAsync commits both together) is what makes IBookingReminderReader's due
//     query able to exclude already-handled bookings server-side. Without it, the SAME oldest BatchSize
//     bookings would be re-selected as "due" on every poll once they were already enqueued, starving every
//     booking behind them in the backlog - the unique constraint alone only stops the duplicate INSERT, it
//     does nothing to stop the SELECT from finding the same rows again.
// The marker is staged for EVERY candidate in the batch, not only the ones that turn out to be newly
// created: if a candidate's outbox row already existed (EnqueueOutcome.AlreadyExists - e.g. a prior attempt
// enqueued the row but never got as far as persisting the marker), staging it again here is exactly the
// recovery this task requires - whichever SaveChangesAsync call ultimately succeeds (the first attempt, or
// EnqueueManyAsync's own duplicate-key recovery retry) commits every staged marker update alongside it,
// so a booking can never end up with a real, durable reminder outbox row AND a still-null marker.
public sealed class ReminderProducer(
    IBookingReminderReader reader,
    IBookingReminderMarker marker,
    INotificationOutboxWriter writer,
    IOptions<BackgroundJobsOptions> options,
    TimeProvider timeProvider,
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

        var queuedAtUtc = timeProvider.GetUtcNow();
        var requests = candidates.Select(BookingNotificationFactory.Reminder).ToList();

        // Staged before EnqueueManyAsync is ever called - see this class's own doc comment for why every
        // candidate's marker is staged here, unconditionally, regardless of whether its outbox row turns
        // out to be newly created or already existed.
        foreach (var candidate in candidates)
        {
            marker.StageReminderQueued(candidate.BookingId, queuedAtUtc);
        }

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
