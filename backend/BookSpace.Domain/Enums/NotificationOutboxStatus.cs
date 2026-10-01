namespace BookSpace.Domain.Enums;

// Pending is deliberately the zero member (EF's unset-vs-explicitly-set sentinel, same reasoning as
// UserStatus.Active) - every NotificationOutboxWriter insert sets it explicitly regardless, but a future
// insert path that forgets to would still land on the correct default rather than an arbitrary one.
// Sent is the only other value this task needs; a future retry-processor task may add Failed/DeadLettered
// without any migration, since this is stored as a string column (see BookSpaceModelConfiguration).
public enum NotificationOutboxStatus { Pending, Sent }
