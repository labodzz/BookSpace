namespace BookSpace.Domain.Entities;

// A distributed lease lock row for exactly one background job - see docs/background-jobs.md ("Job
// lease lock") for the full acquire/renew/release protocol this backs. JobName is the primary key, not
// just a unique index, so it is physically impossible for two active lease rows to exist for the same
// job. Deliberately NOT ITenantOwned: which application instance is running a job cycle is a global
// infrastructure concern, not tenant-scoped domain data, so this must never be swept up by
// BookSpaceDbContext's tenant query filter (that filter only ever applies to ITenantOwned entities).
public sealed class JobLease
{
    public string JobName { get; set; } = string.Empty;

    // The current holder's stable per-instance identity (see IBackgroundJobInstanceIdentity) - never
    // just a machine/host name, since more than one process can run on the same host.
    public string OwnerId { get; set; } = string.Empty;

    // Authoritative expiry instant, always written from the database server's own UTC clock
    // (SYSUTCDATETIME()), never from an instance's local DateTime.UtcNow - see docs/background-jobs.md
    // ("Clock authority") for why two instances' local clocks must never be compared directly.
    public DateTimeOffset LeaseExpiresAtUtc { get; set; }

    // Set on every successful acquire/renew; not used for any expiry decision (LeaseExpiresAtUtc alone
    // decides that) - purely an observability aid for seeing when a lease last proved its owner was
    // still alive.
    public DateTimeOffset? LastHeartbeatAtUtc { get; set; }
}
