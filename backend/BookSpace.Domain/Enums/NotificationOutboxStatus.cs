namespace BookSpace.Domain.Enums;

// Pending is deliberately the zero member (EF's unset-vs-explicitly-set sentinel, same reasoning as
// UserStatus.Active) - every NotificationOutboxWriter insert sets it explicitly regardless, but a future
// insert path that forgets to would still land on the correct default rather than an arbitrary one.
// DeadLettered (added by the per-item processor task - see docs/background-jobs.md, "Dead-letter state")
// and Suppressed (added by the reminder job - see docs/background-jobs.md, "Cancellation race for
// reminders") needed no migration to introduce: the column is a string (see
// BookSpaceModelConfiguration), so a new enum member is purely an application-code change. The due-batch
// query filters to Pending only, so Sent/DeadLettered/Suppressed items all stop being due the moment they
// reach any one of those terminal states.
//
// Suppressed is distinct from DeadLettered on purpose: DeadLettered means delivery was attempted and
// failed (or kept failing until the attempt cap was reached) - something went wrong. Suppressed means
// delivery was deliberately never attempted because the event the notification was about is no longer
// true by the time a send was about to happen (e.g. a reminder for a booking that was cancelled after the
// reminder was enqueued but before it was sent) - nothing went wrong, sending would simply now be
// incorrect. Collapsing the two would make that distinction invisible to anyone reading NotificationOutboxItem rows.
public enum NotificationOutboxStatus { Pending, Sent, DeadLettered, Suppressed }
