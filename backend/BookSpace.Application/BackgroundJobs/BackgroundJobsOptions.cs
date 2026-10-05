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

    // See docs/background-jobs.md ("Reminder job") for the full design.
    public ReminderOptions Reminder { get; init; } = new();

    // See docs/background-jobs.md ("Stale-approval job") for the full design.
    public StaleApprovalOptions StaleApproval { get; init; } = new();
}

// Bound from "BackgroundJobs:Reminder" - a nested section of BackgroundJobsOptions, not a sibling
// top-level section, since it only ever makes sense in the context of the one hosted-service job it
// configures.
public sealed class ReminderOptions
{
    // Deliberately its own cap, not BackgroundJobsOptions.MaxBatchSize: the two settings bound genuinely
    // different queries (how many Confirmed bookings the reminder producer reads as candidates per cycle,
    // vs. how many due notification-outbox rows the dispatcher reads per cycle) that happen to share a
    // reasonable numeric ceiling, not the same concern - see docs/background-jobs.md ("Reminder job -
    // configuration") for why this was not collapsed into the shared BackgroundJobs:BatchSize.
    public const int MaxBatchSize = 1000;

    // A sanity ceiling, not a business rule: nothing about the reminder concept requires a cap this high,
    // it exists only so a misconfigured value (an extra zero) cannot schedule reminders for bookings a
    // week or more out, which would just mean the SAME confirmed bookings keep getting re-selected as
    // "due" candidates every poll for days before they're ever actually near their start time.
    public const int MaxLeadTimeMinutes = 10_080; // 7 days

    // Off by default, same reasoning as BackgroundJobsOptions.Enabled/EmailOptions.Enabled: a feature this
    // codebase has not shipped a reminder email template/flow for in every deployment should never start
    // silently emailing people the moment this branch merges - an operator must explicitly opt in.
    public bool Enabled { get; init; }

    // How long before a Confirmed booking's StartUtc its reminder becomes due - see
    // docs/background-jobs.md ("Reminder job - business rule") for the exact due-window definition.
    public int LeadTimeMinutes { get; init; } = 60;

    public int BatchSize { get; init; } = 50;
}

// Bound from "BackgroundJobs:StaleApproval" - a nested section, same reasoning as ReminderOptions.
// Deliberately has NO "MaxPendingMinutes"-style cutoff setting: ApprovalRequest.ExpiresAtUtc is already
// computed and persisted at creation time from Tenant.ApprovalExpiryHours (see
// CreateBookingCommandRequest/CreateRecurringSeriesCommandRequest) - introducing a second, independent
// global cutoff here would create two different, potentially disagreeing answers to "how long is too
// long," one per-tenant and already live, one global and new. See docs/background-jobs.md ("Stale-approval
// job - why there is no MaxPendingMinutes setting") for the full reasoning. Per-tenant stale-approval
// timeout configurability already exists via Tenant.ApprovalExpiryHours - this job only decides whether,
// and how many at a time, to act on it.
public sealed class StaleApprovalOptions
{
    // Same reasoning as ReminderOptions.MaxBatchSize - its own cap because this bounds a genuinely
    // different query (due ApprovalRequests) than BackgroundJobs:BatchSize (due notification-outbox rows).
    public const int MaxBatchSize = 1000;

    // Off by default, same reasoning as every other per-job Enabled flag in this subsystem.
    public bool Enabled { get; init; }

    public int BatchSize { get; init; } = 50;
}
