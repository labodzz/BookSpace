namespace BookSpace.Application.Notifications;

// Exactly the columns the reminder producer needs to build a Booking.Reminder NotificationOutboxRequest
// (see BookingNotificationFactory.Reminder) and nothing else - in particular never Status (every row
// returned is already known to be Confirmed - that is what "due" means here) and never any recipient
// contact detail (RecipientUserId/UserId is a stable reference; the eventual sender resolves the live
// email at send time, same as every other booking notification).
public sealed record DueBookingReminder(
    Guid BookingId,
    Guid TenantId,
    Guid UserId,
    Guid ResourceId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    int Quantity);

// Reads Confirmed bookings whose reminder is due in bounded batches - see docs/background-jobs.md
// ("Reminder job") for the full design, including why this is a narrow, documented exception to the
// global tenant query filter (the same justification as INotificationOutboxReader): a background job has
// no HTTP request/JWT claim to scope a single tenant by, and must see every tenant's due reminders in one
// query. Read-only: never mutates a Booking row. Whether a booking's reminder has already been enqueued
// is deliberately NOT filtered here - see BookSpace.Application.BackgroundJobs.ReminderProducer for why
// that is INotificationOutboxWriter.EnqueueManyAsync's job, not this query's.
public interface IBookingReminderReader
{
    // Returns at most batchSize Confirmed bookings (see BackgroundJobsOptions.ReminderOptions.BatchSize)
    // whose StartUtc is still in the future but no further away than leadTime, ordered deterministically
    // (oldest StartUtc first, see the implementation for the full tie-break chain) so a backlog larger
    // than one batch is worked through in a stable order across repeated polls rather than potentially
    // re-selecting the same subset forever. Returns an empty list, never null, when nothing is due.
    Task<IReadOnlyList<DueBookingReminder>> GetDueBookingsAsync(TimeSpan leadTime, int batchSize, CancellationToken cancellationToken);
}
