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
    IOptions<BackgroundJobsOptions> options,
    TimeProvider timeProvider,
    ILogger<BackgroundJobsWorker> logger) : BackgroundService
{
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
    // holding one itself.
    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var cycle = scope.ServiceProvider.GetRequiredService<IBackgroundJobCycle>();

        try
        {
            await cycle.RunCycleAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never an empty catch: a single cycle's failure is logged and the loop keeps going on the
            // next poll interval, rather than a bug in one future job (a reminder query failing) taking
            // down the whole worker permanently for every other job cycle.
            logger.LogError(ex, "Background job cycle failed; it will be retried on the next poll interval.");
        }
    }
}
