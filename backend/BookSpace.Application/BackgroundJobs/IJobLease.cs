namespace BookSpace.Application.BackgroundJobs;

// A held lease for one job cycle, returned by IJobLeaseCoordinator.TryAcquireAsync. LeaseLostToken is
// cancelled the moment a heartbeat renewal discovers this instance no longer owns the lease (it expired
// and - possibly - was taken over by another instance); callers should link it with their own
// shutdown/cancellation token so in-flight work unwinds promptly instead of continuing to run on a lease
// it no longer holds. Disposing stops the heartbeat loop and attempts one last release; disposing after
// the lease was already lost is safe and never disturbs whichever instance now owns it (see
// IJobLeaseStore.TryReleaseAsync).
public interface IJobLease : IAsyncDisposable
{
    string JobName { get; }
    string OwnerId { get; }
    CancellationToken LeaseLostToken { get; }
}
