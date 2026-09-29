using BookSpace.Application.BackgroundJobs;
using Microsoft.Extensions.Options;

namespace BookSpace.Api.BackgroundJobs;

// The .NET hosted-service half of the WP-8 background jobs foundation - see
// docs/background-jobs.md for why this stays a singleton that only ever hands out a fresh
// IServiceScope per cycle rather than holding a scoped dependency (a DbContext, a repository)
// itself. IBackgroundJobCycle (BookSpace.Application.BackgroundJobs) is where real WP-8 jobs will
// plug in - this branch only wires up the loop, not any actual job.
public sealed class BackgroundJobsWorker(
    IServiceScopeFactory scopeFactory,
    IJobLeaseCoordinator leaseCoordinator,
    IOptions<BackgroundJobsOptions> options,
    TimeProvider timeProvider,
    ILogger<BackgroundJobsWorker> logger) : BackgroundService
{
    // Stable across this process's entire lifetime - only one real cycle exists today, so one constant
    // name is enough. A future branch that adds more than one distinct job would give each its own name
    // instead of sharing this one. See docs/background-jobs.md ("Job lease lock"). Public so tests can
    // reference the exact same value rather than duplicating the literal.
    public const string PrimaryCycleJobName = "background-jobs-primary-cycle";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Background jobs are disabled (BackgroundJobs:Enabled=false) - the worker will not run any cycles.");
            return;
        }

        var pollInterval = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
        logger.LogInformation("Background jobs worker started - polling every {PollIntervalSeconds}s.", options.Value.PollIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunCycleAsync(stoppingToken);

            try
            {
                await Task.Delay(pollInterval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        logger.LogInformation("Background jobs worker stopped.");
    }

    // A fresh scope per cycle - never held across cycles, and never a field on this singleton - is
    // what lets a future real IBackgroundJobCycle depend on scoped services without this worker ever
    // holding one itself. Only one application instance may run a cycle for PrimaryCycleJobName at a
    // time - see docs/background-jobs.md ("Job lease lock") - so every cycle attempt first tries to
    // acquire that job's lease and skips the cycle entirely when another instance already holds it.
    private async Task RunCycleAsync(CancellationToken stoppingToken)
    {
        await using var lease = await leaseCoordinator.TryAcquireAsync(PrimaryCycleJobName, stoppingToken);
        if (lease is null)
        {
            logger.LogInformation("Background job cycle skipped this poll - {JobName} lease is held by another instance.", PrimaryCycleJobName);
            return;
        }

        // Cancelled either by application shutdown (stoppingToken) or by the lease's own heartbeat
        // discovering this instance no longer owns it (lease.LeaseLostToken) - whichever comes first, the
        // running cycle gets a chance to unwind rather than keep working under a lease it no longer holds.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lease.LeaseLostToken);

        await using var scope = scopeFactory.CreateAsyncScope();
        var cycle = scope.ServiceProvider.GetRequiredService<IBackgroundJobCycle>();

        try
        {
            await cycle.RunCycleAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Real application shutdown - propagate exactly as before this branch existed, so
            // ExecuteAsync's own loop/StopAsync handling still applies unchanged.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Not a shutdown - the lease was lost mid-cycle. Already logged as a warning by the heartbeat
            // loop itself; this is just the cycle observing that same signal and unwinding, not a failure
            // of the cycle's own logic, so it is worth a line but not an error.
            logger.LogInformation(
                "Background job cycle for {JobName} was interrupted because its lease was lost mid-cycle; it will retry acquiring on the next poll interval.",
                PrimaryCycleJobName);
        }
        catch (Exception ex)
        {
            // Never an empty catch: a single cycle's failure is logged and the loop keeps going on the
            // next poll interval, rather than a bug in one future job (a reminder query failing) taking
            // down the whole worker permanently for every other job cycle.
            logger.LogError(ex, "Background job cycle failed; it will be retried on the next poll interval.");
        }
    }
}
