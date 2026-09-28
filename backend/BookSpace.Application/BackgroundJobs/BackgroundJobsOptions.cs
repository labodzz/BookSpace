namespace BookSpace.Application.BackgroundJobs;

// Bound from configuration ("BackgroundJobs" section) - see docs/background-jobs.md. Disabled by
// default: this branch only builds the hosted-service foundation (the loop, the per-cycle DI scope,
// cancellation), not any real WP-8 job, so there is nothing yet a default-enabled worker should
// actually be doing in Development, Testing, or an unconfigured deployment.
public sealed class BackgroundJobsOptions
{
    public bool Enabled { get; init; }
    public int PollIntervalSeconds { get; init; } = 30;
}
