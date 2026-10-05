namespace BookSpace.Application.BackgroundJobs;

// CandidateCount: how many due candidates IStaleApprovalReader returned this call.
// ExpiredCount: how many were actually transitioned to Expired (ApprovalRequest) / Expired (Booking) and
// had their notification enqueued - all three always land together (see IStaleApprovalProcessor's own
// doc comment on atomicity), so this always equals NotificationEnqueuedCount by construction.
// AlreadyCompletedCount: the item's re-read under the resource lock found it was no longer Pending on
// either side (an approver's Approve/Reject call won the race) - not an error, the expected and correct
// outcome of that race going the other way.
// ConflictCount: the resource lock itself reported a ConflictException (e.g. a SQL Server deadlock/lock-
// timeout, same classification ResourceBookingLock already uses for HTTP callers) - transient, safe to
// retry on the next poll, never treated as a failure.
// FailedCount: a genuinely unexpected exception - logged, isolated, the item is simply still Pending and
// still past its ExpiresAtUtc, so it is naturally retried on the next poll (see IStaleApprovalProcessor's
// own doc comment for why no separate retry/attempt-count state was introduced for this).
// NotificationEnqueuedCount: see ExpiredCount above.
public sealed record StaleApprovalCycleResult(
    int CandidateCount, int ExpiredCount, int AlreadyCompletedCount, int ConflictCount, int FailedCount, int NotificationEnqueuedCount)
{
    public static readonly StaleApprovalCycleResult Empty = new(0, 0, 0, 0, 0, 0);
}

// Finds Pending ApprovalRequests past their own ExpiresAtUtc and, for each, atomically expires it, expires
// its Booking, and enqueues a Booking.ApprovalExpired notification - see docs/background-jobs.md
// ("Stale-approval job") for the full design. Invoked by NotificationOutboxJobCycle as the first step of
// its cycle - see that class for the full ordering and why stale-approval runs before the reminder
// producer and the existing outbox dispatch.
public interface IStaleApprovalProcessor
{
    Task<StaleApprovalCycleResult> ProcessDueApprovalsAsync(CancellationToken cancellationToken);
}
