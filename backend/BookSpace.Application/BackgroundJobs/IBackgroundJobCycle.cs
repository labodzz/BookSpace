namespace BookSpace.Application.BackgroundJobs;

// One unit of periodic background work, resolved from a fresh scope per cycle by
// BackgroundJobsWorker (BookSpace.Api) - see docs/background-jobs.md. Implementations are free to
// depend on scoped services (a repository, a DbContext) because they are only ever resolved inside
// that per-cycle scope, never held by the singleton worker itself. NotificationOutboxJobCycle is the
// one implementation registered today - other future WP-8 jobs (reminders, no-show handling, stale
// approvals, ...) are each expected to be their own implementation of this interface.
public interface IBackgroundJobCycle
{
    Task RunCycleAsync(CancellationToken cancellationToken);
}
