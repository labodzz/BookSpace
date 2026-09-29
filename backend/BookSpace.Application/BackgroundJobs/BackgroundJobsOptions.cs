namespace BookSpace.Application.BackgroundJobs;

// Bound from configuration ("BackgroundJobs" section) - see docs/background-jobs.md. Disabled by
// default: this branch only builds the hosted-service foundation (the loop, the per-cycle DI scope,
// cancellation), not any real WP-8 job, so there is nothing yet a default-enabled worker should
// actually be doing in Development, Testing, or an unconfigured deployment.
public sealed class BackgroundJobsOptions
{
    public bool Enabled { get; init; }
    public int PollIntervalSeconds { get; init; } = 30;

    // How long an acquired job lease stays valid before it is considered abandoned and eligible for
    // another instance to take over - see docs/background-jobs.md ("Job lease lock").
    public int LeaseDurationSeconds { get; init; } = 60;

    // How often the current owner renews (extends) its lease while a cycle is running. Must be
    // meaningfully shorter than LeaseDurationSeconds (enforced by BackgroundJobsOptionsValidator) so one
    // slow/delayed heartbeat does not immediately cost the instance its lease.
    public int HeartbeatIntervalSeconds { get; init; } = 20;

    // Optional human-readable label folded into this instance's OwnerId (see
    // IBackgroundJobInstanceIdentity). Falls back to Environment.MachineName when unset; either way a
    // process id and a per-startup GUID are always appended too, since more than one process can share a
    // machine/container name.
    public string? InstanceName { get; init; }
}
