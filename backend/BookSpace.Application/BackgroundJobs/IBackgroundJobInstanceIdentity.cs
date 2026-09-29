namespace BookSpace.Application.BackgroundJobs;

// Stable identity for this running application instance, used as JobLease.OwnerId. Registered as a
// singleton so the SAME OwnerId is used for every acquire/renew/release attempt made during this
// instance's lifetime, and a brand new one is generated the next time the process starts - see
// docs/background-jobs.md ("Owner identity") for why a machine/host name alone is not enough (more than
// one process/container can run on the same host).
public interface IBackgroundJobInstanceIdentity
{
    string OwnerId { get; }
}
