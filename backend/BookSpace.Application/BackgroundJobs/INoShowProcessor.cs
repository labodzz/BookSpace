namespace BookSpace.Application.BackgroundJobs;

// CandidateCount: how many due candidates INoShowReader returned this call.
// MarkedNoShowCount: how many were actually transitioned to NoShow and had their notification enqueued -
// both always land together (see INoShowProcessor's own doc comment on atomicity), so this always equals
// NotificationEnqueuedCount by construction.
// AlreadyCompletedCount: the item's re-read under the resource lock found Status no longer Confirmed AND
// CheckedInAtUtc still null - some OTHER terminal transition (most commonly Cancelled) already won the
// race. Not an error, the expected and correct outcome of that race going the other way.
// CheckedInCount: the item's re-read under the resource lock found CheckedInAtUtc already set - the
// check-in race went the other way. Counted separately from AlreadyCompletedCount since this is the
// specific, expected outcome this job's whole design exists to respect (see docs/bookings-and-concurrency.md,
// "No-show processing").
// NotYetDueCount: the item's re-read found Status still Confirmed and CheckedInAtUtc still null, but the
// freshly-recomputed deadline (using a freshly-read Tenant.NoShowGraceMinutes, not the value implied by the
// pre-lock candidate) is no longer in the past - possible if an operator changed the tenant's grace period
// between the due query and this item's turn in the batch. Not an error: simply skipped, naturally
// reconsidered on the next poll if it becomes due again.
// ConflictCount: the resource lock itself reported a ConflictException (e.g. a SQL Server deadlock/lock-
// timeout, same classification ResourceBookingLock already uses for HTTP callers) - transient, safe to
// retry on the next poll, never treated as a failure.
// FailedCount: a genuinely unexpected exception - logged, isolated, the item is simply still due afterward
// (unless something else resolved it), so it is naturally retried on the next poll - no separate retry/
// attempt-count state is introduced for this, same reasoning as IStaleApprovalProcessor.
// NotificationEnqueuedCount: see MarkedNoShowCount above.
public sealed record NoShowCycleResult(
    int CandidateCount,
    int MarkedNoShowCount,
    int AlreadyCompletedCount,
    int CheckedInCount,
    int NotYetDueCount,
    int ConflictCount,
    int FailedCount,
    int NotificationEnqueuedCount)
{
    public static readonly NoShowCycleResult Empty = new(0, 0, 0, 0, 0, 0, 0, 0);
}

// Finds Confirmed bookings nobody checked in whose deadline (StartUtc + Tenant.NoShowGraceMinutes) has
// passed and, for each, atomically marks it NoShow and enqueues a Booking.NoShow notification - see
// docs/background-jobs.md ("No-show job") for the full design. Invoked by NotificationOutboxJobCycle
// immediately after the stale-approval step and before the reminder producer - see that class for the full
// ordering rationale.
public interface INoShowProcessor
{
    Task<NoShowCycleResult> ProcessDueNoShowsAsync(CancellationToken cancellationToken);
}
