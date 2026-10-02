namespace BookSpace.Application.BackgroundJobs;

// Bound from configuration ("BackgroundJobs" section) - see docs/background-jobs.md. Disabled by
// default: this branch only builds the hosted-service foundation (the loop, the per-cycle DI scope,
// cancellation), not any real WP-8 job, so there is nothing yet a default-enabled worker should
// actually be doing in Development, Testing, or an unconfigured deployment.
public sealed class BackgroundJobsOptions
{
    // Upper bound BackgroundJobsOptionsValidator enforces for BatchSize - a misconfigured value (a typo
    // adding a stray zero, say) can never make a single poll try to pull an unbounded number of rows into
    // memory. 1000 is generous for the per-poll volume this system is expected to need while still being
    // small enough that materializing a full batch of DueNotificationOutboxItem projections is cheap -
    // see docs/background-jobs.md ("BatchSize configuration") for the reasoning.
    public const int MaxBatchSize = 1000;

    // Sanity caps BackgroundJobsOptionsValidator enforces on the retry settings below - see
    // docs/background-jobs.md ("Retry configuration").
    public const int MaxAllowedNotificationAttempts = 50;
    public const int MaxAllowedRetryDelaySeconds = 86_400; // 24 hours

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

    // Maximum number of due notification-outbox items NotificationOutboxReader.GetDueBatchAsync returns
    // in one call - see docs/background-jobs.md ("Due-batch query"). Bounded (BackgroundJobsOptionsValidator
    // enforces 1..MaxBatchSize) so a backlog of thousands of due items is always processed in fixed-size
    // slices, never loaded all at once.
    public int BatchSize { get; init; } = 50;

    // Maximum number of actual delivery attempts NotificationOutboxProcessor will make for one outbox
    // item before dead-lettering it - see docs/background-jobs.md ("AttemptCount semantics"). An attempt
    // that fails transiently on try number MaxNotificationAttempts dead-letters the item immediately;
    // there is never a try number MaxNotificationAttempts + 1.
    public int MaxNotificationAttempts { get; init; } = 5;

    // Delay before the first retry after a transient failure - see docs/background-jobs.md ("Exponential
    // backoff"). Doubles on each subsequent transient failure (NotificationRetryBackoff), up to
    // MaxRetryDelaySeconds.
    public int InitialRetryDelaySeconds { get; init; } = 60;

    // Upper bound the exponential backoff delay is clamped to, regardless of how many attempts have
    // failed - without this, a long-failing item's next attempt could end up scheduled absurdly far in
    // the future.
    public int MaxRetryDelaySeconds { get; init; } = 3600;
}
