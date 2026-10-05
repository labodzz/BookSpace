namespace BookSpace.Application.Notifications;

// Exactly the columns StaleApprovalProcessor needs to re-locate and re-validate one candidate under the
// resource lock (see IStaleApprovalProcessor), plus what BookingNotificationFactory.ApprovalExpired needs
// to build the eventual notification - nothing else. Never Status on either side (every row returned is
// already known to be Pending on both the ApprovalRequest and its Booking - that is what "due" means
// here), and never ExpiresAtUtc itself (the processor never needs to re-derive or compare it again - see
// StaleApprovalProcessor's own doc comment for why re-checking the cutoff a second time, under the lock,
// is unnecessary).
public sealed record DueStaleApproval(
    Guid ApprovalRequestId,
    Guid BookingId,
    Guid TenantId,
    Guid ResourceId,
    Guid UserId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    int Quantity);

// Reads stale (Pending, past their own ExpiresAtUtc) approval requests in bounded batches - see
// docs/background-jobs.md ("Stale-approval job") for the full design, including why this is a narrow,
// documented exception to the global tenant query filter (the same justification as
// INotificationOutboxReader/IBookingReminderReader): a background job has no HTTP request/JWT claim to
// scope a single tenant by, and must see every tenant's stale approvals in one query. Read-only: never
// mutates a row.
public interface IStaleApprovalReader
{
    // Returns at most batchSize candidates (see BackgroundJobsOptions.StaleApprovalOptions.BatchSize)
    // whose ApprovalRequest is still Pending, whose ExpiresAtUtc has already passed (compared against the
    // DATABASE's own UTC clock, never this application instance's), and whose Booking is still Pending
    // too - ordered deterministically (oldest RequestedAtUtc first, then Id, so a backlog larger than one
    // batch is worked through in a stable order across repeated polls). Returns an empty list, never
    // null, when nothing is due.
    Task<IReadOnlyList<DueStaleApproval>> GetDueApprovalsAsync(int batchSize, CancellationToken cancellationToken);
}
