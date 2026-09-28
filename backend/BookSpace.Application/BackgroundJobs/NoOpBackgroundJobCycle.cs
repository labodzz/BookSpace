using Microsoft.Extensions.Logging;

namespace BookSpace.Application.BackgroundJobs;

// The only IBackgroundJobCycle this branch ships - proves the worker's loop/scope/cancellation
// plumbing actually runs, without touching the database or any external system. Replaced (or
// supplemented, if more than one cycle ever needs to run) by real WP-8 jobs in later branches - see
// docs/background-jobs.md.
public sealed class NoOpBackgroundJobCycle(ILogger<NoOpBackgroundJobCycle> logger) : IBackgroundJobCycle
{
    public Task RunCycleAsync(CancellationToken cancellationToken)
    {
        logger.LogDebug("Background job cycle executed (no-op placeholder - no WP-8 jobs implemented yet).");
        return Task.CompletedTask;
    }
}
