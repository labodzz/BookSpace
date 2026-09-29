namespace BookSpace.Application.BackgroundJobs;

// The atomic, single-round-trip primitives a distributed lease lock is built from - see
// docs/background-jobs.md ("Job lease lock") for the exact protocol and why each of these must be one
// conditional statement, never a separate read-then-write (two callers could both read "free" and both
// write). None of these throw for the ordinary "someone else holds it" outcome - they signal that
// through their return value, and only an unexpected error (e.g. the database is unreachable) is allowed
// to escape as an exception. Implementations must use the database's own authoritative clock for every
// expiry decision, never an instance's local DateTime.UtcNow, so two instances' clocks are never compared
// directly.
public interface IJobLeaseStore
{
    // Atomically takes ownership of jobName for ownerId when the row does not yet exist, is already
    // expired, or is already held by ownerId itself (a safe re-acquire, not a second owner) - returns the
    // new LeaseExpiresAtUtc on success. Returns null when another, still-live owner holds the lease; this
    // is an expected, ordinary outcome of a concurrent acquire race, not a failure.
    Task<DateTimeOffset?> TryAcquireAsync(string jobName, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken);

    // Extends jobName's lease only if ownerId still matches AND the lease has not already expired -
    // returns the new LeaseExpiresAtUtc on success, or null if this owner no longer holds the lease
    // (expired and possibly already taken over by another instance).
    Task<DateTimeOffset?> TryRenewAsync(string jobName, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken);

    // Releases jobName's lease only if ownerId still matches, so one instance can never release (or
    // otherwise disturb) a lease another instance now owns. Returns true when this call actually released
    // the lease, false when there was nothing to release for this owner (already lost/expired, or never
    // held) - both are normal, loggable outcomes, not errors.
    Task<bool> TryReleaseAsync(string jobName, string ownerId, CancellationToken cancellationToken);
}
