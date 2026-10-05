namespace BookSpace.Application.BackgroundJobs;

// CandidateCount: how many Confirmed, due bookings IBookingReminderReader returned this call.
// EnqueuedCount: how many of those genuinely created a new NotificationOutboxItem (EnqueueOutcome.Created).
// DuplicateCount: how many were already enqueued on an earlier cycle (EnqueueOutcome.AlreadyExists) - not
// an error, just the normal, expected steady state for a booking that stays within the lead-time window
// across more than one poll (see ReminderProducer's own doc comment for why the candidate query does not
// try to pre-filter these out itself).
public sealed record ReminderProducerResult(int CandidateCount, int EnqueuedCount, int DuplicateCount)
{
    public static readonly ReminderProducerResult Empty = new(0, 0, 0);
}

// Finds Confirmed bookings whose reminder is due and enqueues one Booking.Reminder notification-outbox
// item per booking - see docs/background-jobs.md ("Reminder job") for the full design. Invoked by
// NotificationOutboxJobCycle as the first step of its cycle, before the existing due-batch dispatch - see
// that class for why a reminder enqueued this same cycle can be picked up and sent immediately rather than
// waiting for the next poll.
public interface IReminderProducer
{
    Task<ReminderProducerResult> ProduceDueRemindersAsync(CancellationToken cancellationToken);
}
