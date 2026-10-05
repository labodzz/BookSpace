namespace BookSpace.Application.Notifications;

// Stages a booking's ReminderQueuedAtUtc update on the caller's own scoped DbContext - see
// docs/background-jobs.md ("Reminder job - the ReminderQueuedAtUtc marker") for the full design.
// Deliberately synchronous and deliberately does NOT call SaveChangesAsync itself: the whole point is that
// ReminderProducer stages this BEFORE calling INotificationOutboxWriter.EnqueueManyAsync, so that writer's
// own internal SaveChangesAsync call commits the marker update and the new outbox row(s) together, in the
// same way the booking-lifecycle-notification work already stages a Booking's status change before calling
// EnqueueManyAsync. Calling this alone, without a subsequent SaveChangesAsync on the same DbContext (via
// EnqueueManyAsync or otherwise), persists nothing.
public interface IBookingReminderMarker
{
    void StageReminderQueued(Guid bookingId, DateTimeOffset queuedAtUtc);
}
