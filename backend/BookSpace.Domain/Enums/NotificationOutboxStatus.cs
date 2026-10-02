namespace BookSpace.Domain.Enums;

// Pending is deliberately the zero member (EF's unset-vs-explicitly-set sentinel, same reasoning as
// UserStatus.Active) - every NotificationOutboxWriter insert sets it explicitly regardless, but a future
// insert path that forgets to would still land on the correct default rather than an arbitrary one.
// DeadLettered (added by the per-item processor task - see docs/background-jobs.md, "Dead-letter state")
// needed no migration to introduce: it is stored as a string column (see BookSpaceModelConfiguration), so
// a new enum member is purely an application-code change. The due-batch query filters to Pending only,
// so both Sent and DeadLettered items stop being due the moment they reach either state.
public enum NotificationOutboxStatus { Pending, Sent, DeadLettered }
