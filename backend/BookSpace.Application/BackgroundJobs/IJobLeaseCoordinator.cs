namespace BookSpace.Application.BackgroundJobs;

// Wraps IJobLeaseStore with the heartbeat loop and owner identity a real caller needs - see
// docs/background-jobs.md ("Job lease lock"). Safe to hold as a singleton (see JobLeaseCoordinator):
// exactly like BackgroundJobsWorker itself, it never holds a scoped dependency directly, only a fresh
// IServiceScope per database operation.
public interface IJobLeaseCoordinator
{
    // Returns null immediately (no exception) when jobName is currently held by another, still-live
    // owner - this is the ordinary "lost the race" outcome, not a failure. On success, the returned
    // IJobLease already has its heartbeat running; the caller is responsible for disposing it (typically
    // in a finally block) once its cycle is done.
    Task<IJobLease?> TryAcquireAsync(string jobName, CancellationToken cancellationToken);
}
