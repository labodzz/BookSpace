namespace BookSpace.Application.Notifications;

public enum NotificationOutboxItemOutcome
{
    // The sender reported success - Status is now Sent.
    Sent,

    // A transient failure, with attempts remaining - AvailableAtUtc was pushed forward by the computed
    // backoff delay; Status stays Pending.
    RetryScheduled,

    // Either a permanent failure, or a transient failure that just exhausted MaxNotificationAttempts -
    // Status is now DeadLettered. Terminal: no future due-batch query will ever return this item again.
    DeadLettered,

    // The item no longer existed when processing started (defensive - nothing in this codebase deletes
    // outbox rows today). Logged, not treated as an error.
    Skipped,
}

// Processes due notification-outbox items one at a time, with persisted retry/backoff state and a
// maximum-attempt cap - see docs/background-jobs.md ("Per-item processing and retry") for the full
// design: per-item isolation (one item's failure, expected or not, never aborts the rest of the batch or
// rolls back an earlier item's already-committed success), the AttemptCount/backoff/dead-letter
// semantics, and cancellation behavior. Deliberately not yet invoked by BackgroundJobsWorker/
// IBackgroundJobCycle - no real INotificationSender exists yet to plug in.
public interface INotificationOutboxProcessor
{
    // Processes exactly one item to completion (or until cancellation prevents the attempt from starting
    // or completing - see docs/background-jobs.md, "Cancellation behavior"). Exposed directly (not only
    // via ProcessBatchAsync) so per-item outcomes - success, retry scheduling, dead-lettering, attempt
    // accounting - are each independently testable.
    Task<NotificationOutboxItemOutcome> ProcessItemAsync(DueNotificationOutboxItem item, CancellationToken cancellationToken);

    // Processes a batch in order, one item at a time, stopping (without processing further items) the
    // moment cancellationToken is cancelled - either before starting the next item, or because
    // ProcessItemAsync itself observed cancellation mid-delivery and propagated it. An unexpected
    // exception from one item (anything other than a clean Sent/RetryScheduled/DeadLettered/Skipped
    // outcome or a genuine cancellation) is logged and isolated: the batch continues with the next item.
    Task ProcessBatchAsync(IReadOnlyList<DueNotificationOutboxItem> batch, CancellationToken cancellationToken);
}
