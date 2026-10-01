namespace BookSpace.Application.Notifications;

// Exactly the columns a future per-item processor needs to act on one due item, and nothing else - in
// particular, never Status (every row returned is already known to be Pending - that is what "due"
// means) and never IdempotencyKey (irrelevant once the row has already been durably recorded; nothing
// about processing a due item needs to re-derive or compare it). TenantId is included even though this
// read deliberately spans every tenant in one query (see INotificationOutboxReader) - a future processor
// needs the TenantId on hand to know which tenant's context to act in, without a second lookup per item.
public sealed record DueNotificationOutboxItem(
    Guid Id,
    Guid TenantId,
    string NotificationType,
    Guid RecipientUserId,
    string PayloadJson,
    DateTimeOffset AvailableAtUtc,
    DateTimeOffset CreatedAtUtc,
    int AttemptCount);

// Reads due notification-outbox work in bounded batches - see docs/background-jobs.md ("Due-batch
// query"). This is a read-only query abstraction: it never mutates a row, never marks anything as
// claimed/sent, and is safe to call repeatedly without side effects (the same due items would simply be
// returned again on the next call, until something else - a future per-item processor, not this
// interface - changes their state). Deliberately NOT yet wired into BackgroundJobsWorker/
// IBackgroundJobCycle - see docs/background-jobs.md for why and for the future intended call chain.
public interface INotificationOutboxReader
{
    // Returns at most batchSize items (see BackgroundJobsOptions.BatchSize for the configured,
    // validated value callers should normally pass) that are currently due: not yet sent, and whose
    // AvailableAtUtc has already passed, ordered deterministically (oldest AvailableAtUtc first, see the
    // implementation for the full tie-break chain) so repeated calls under a steady backlog make
    // progress through it in a stable order rather than potentially re-fetching the same items forever.
    // Returns an empty list, never null, when nothing is due.
    Task<IReadOnlyList<DueNotificationOutboxItem>> GetDueBatchAsync(int batchSize, CancellationToken cancellationToken);
}
