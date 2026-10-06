namespace BookSpace.Application.Notifications;

// Exactly the columns NoShowProcessor needs to re-locate and re-validate one candidate under the resource
// lock (see INoShowProcessor), plus what BookingNotificationFactory.NoShow needs to build the eventual
// notification - nothing else. Never Status or CheckedInAtUtc (every row returned is already known to be
// Confirmed with CheckedInAtUtc == null - that is what "due" means here), and never the computed deadline
// itself (the processor re-derives it fresh under the lock from a freshly-read Tenant.NoShowGraceMinutes,
// rather than trusting a value computed before the lock was held - see NoShowProcessor's own doc comment).
public sealed record DueNoShowBooking(
    Guid BookingId,
    Guid TenantId,
    Guid ResourceId,
    Guid UserId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    int Quantity);

// Reads Confirmed bookings nobody checked in, whose no-show deadline (StartUtc + that tenant's own
// NoShowGraceMinutes) has already passed, in bounded batches - see docs/background-jobs.md ("No-show job")
// for the full design, including why this is a narrow, documented exception to the global tenant query
// filter (the same justification as INotificationOutboxReader/IBookingReminderReader/IStaleApprovalReader):
// a background job has no HTTP request/JWT claim to scope a single tenant by, and must see every tenant's
// due no-shows in one query. Read-only: never mutates a row.
public interface INoShowReader
{
    // Returns at most batchSize candidates (see BackgroundJobsOptions.NoShowOptions.BatchSize) whose
    // Booking.Status is still Confirmed, whose CheckedInAtUtc is still null, and whose deadline
    // (Booking.StartUtc + that booking's own Tenant.NoShowGraceMinutes) is STRICTLY in the past compared
    // against the DATABASE's own UTC clock, never this application instance's - ordered deterministically
    // (earliest deadline first, then CreatedAtUtc, then Id, so a backlog larger than one batch is worked
    // through in a stable order across repeated polls). Returns an empty list, never null, when nothing is
    // due.
    Task<IReadOnlyList<DueNoShowBooking>> GetDueNoShowsAsync(int batchSize, CancellationToken cancellationToken);
}
