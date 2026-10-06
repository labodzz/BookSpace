using Microsoft.Extensions.Options;

namespace BookSpace.Application.BackgroundJobs;

// Registered against ValidateOnStart() (see Program.cs) so a misconfigured PollIntervalSeconds fails
// startup immediately and loudly, rather than letting BackgroundJobsWorker discover a zero/negative
// interval only once it tries to poll - see docs/background-jobs.md.
public sealed class BackgroundJobsOptionsValidator : IValidateOptions<BackgroundJobsOptions>
{
    public ValidateOptionsResult Validate(string? name, BackgroundJobsOptions options)
    {
        if (options.PollIntervalSeconds <= 0)
        {
            return ValidateOptionsResult.Fail("BackgroundJobs:PollIntervalSeconds must be a positive number of seconds.");
        }

        if (options.LeaseDurationSeconds <= 0)
        {
            return ValidateOptionsResult.Fail("BackgroundJobs:LeaseDurationSeconds must be a positive number of seconds.");
        }

        if (options.HeartbeatIntervalSeconds <= 0)
        {
            return ValidateOptionsResult.Fail("BackgroundJobs:HeartbeatIntervalSeconds must be a positive number of seconds.");
        }

        // Strictly less than, not less-or-equal: a heartbeat that only just keeps pace with the lease
        // duration leaves no room at all for one slow/delayed renewal before the lease is stolen out from
        // under its still-alive owner. See docs/background-jobs.md for the recommended margin.
        if (options.HeartbeatIntervalSeconds >= options.LeaseDurationSeconds)
        {
            return ValidateOptionsResult.Fail(
                "BackgroundJobs:HeartbeatIntervalSeconds must be less than BackgroundJobs:LeaseDurationSeconds, "
                + "so a single delayed heartbeat does not immediately cost the instance its lease.");
        }

        if (options.BatchSize <= 0)
        {
            return ValidateOptionsResult.Fail("BackgroundJobs:BatchSize must be a positive number of items.");
        }

        if (options.BatchSize > BackgroundJobsOptions.MaxBatchSize)
        {
            return ValidateOptionsResult.Fail(
                $"BackgroundJobs:BatchSize must not exceed {BackgroundJobsOptions.MaxBatchSize} - "
                + "a misconfigured value should never let one poll pull an unbounded number of rows into memory.");
        }

        if (options.MaxNotificationAttempts < 1)
        {
            return ValidateOptionsResult.Fail("BackgroundJobs:MaxNotificationAttempts must be at least 1.");
        }

        if (options.MaxNotificationAttempts > BackgroundJobsOptions.MaxAllowedNotificationAttempts)
        {
            return ValidateOptionsResult.Fail(
                $"BackgroundJobs:MaxNotificationAttempts must not exceed {BackgroundJobsOptions.MaxAllowedNotificationAttempts}.");
        }

        if (options.InitialRetryDelaySeconds <= 0)
        {
            return ValidateOptionsResult.Fail("BackgroundJobs:InitialRetryDelaySeconds must be a positive number of seconds.");
        }

        if (options.MaxRetryDelaySeconds < options.InitialRetryDelaySeconds)
        {
            return ValidateOptionsResult.Fail(
                "BackgroundJobs:MaxRetryDelaySeconds must be greater than or equal to BackgroundJobs:InitialRetryDelaySeconds.");
        }

        // Bounds the exponential backoff's clamp itself, independent of NotificationRetryBackoff's own
        // overflow-safe (double/Math.Pow) computation - a misconfigured multi-year delay is a
        // configuration mistake worth rejecting outright, not just something the math merely survives.
        if (options.MaxRetryDelaySeconds > BackgroundJobsOptions.MaxAllowedRetryDelaySeconds)
        {
            return ValidateOptionsResult.Fail(
                $"BackgroundJobs:MaxRetryDelaySeconds must not exceed {BackgroundJobsOptions.MaxAllowedRetryDelaySeconds} seconds.");
        }

        // Reminder:Enabled=false (the default) needs none of these checked - exactly the same "disabled
        // needs no further configuration" shape as the top-level BackgroundJobs:Enabled/Email:Enabled.
        if (options.Reminder.Enabled)
        {
            if (options.Reminder.LeadTimeMinutes <= 0)
            {
                return ValidateOptionsResult.Fail("BackgroundJobs:Reminder:LeadTimeMinutes must be a positive number of minutes.");
            }

            if (options.Reminder.LeadTimeMinutes > ReminderOptions.MaxLeadTimeMinutes)
            {
                return ValidateOptionsResult.Fail(
                    $"BackgroundJobs:Reminder:LeadTimeMinutes must not exceed {ReminderOptions.MaxLeadTimeMinutes} minutes.");
            }

            if (options.Reminder.BatchSize <= 0)
            {
                return ValidateOptionsResult.Fail("BackgroundJobs:Reminder:BatchSize must be a positive number of items.");
            }

            if (options.Reminder.BatchSize > ReminderOptions.MaxBatchSize)
            {
                return ValidateOptionsResult.Fail(
                    $"BackgroundJobs:Reminder:BatchSize must not exceed {ReminderOptions.MaxBatchSize} - "
                    + "a misconfigured value should never let one poll pull an unbounded number of candidate bookings into memory.");
            }
        }

        // StaleApproval:Enabled=false (the default) needs none of these checked.
        if (options.StaleApproval.Enabled)
        {
            if (options.StaleApproval.BatchSize <= 0)
            {
                return ValidateOptionsResult.Fail("BackgroundJobs:StaleApproval:BatchSize must be a positive number of items.");
            }

            if (options.StaleApproval.BatchSize > StaleApprovalOptions.MaxBatchSize)
            {
                return ValidateOptionsResult.Fail(
                    $"BackgroundJobs:StaleApproval:BatchSize must not exceed {StaleApprovalOptions.MaxBatchSize} - "
                    + "a misconfigured value should never let one poll pull an unbounded number of candidate approvals into memory.");
            }
        }

        // NoShow:Enabled=false (the default) needs none of these checked.
        if (options.NoShow.Enabled)
        {
            if (options.NoShow.BatchSize <= 0)
            {
                return ValidateOptionsResult.Fail("BackgroundJobs:NoShow:BatchSize must be a positive number of items.");
            }

            if (options.NoShow.BatchSize > NoShowOptions.MaxBatchSize)
            {
                return ValidateOptionsResult.Fail(
                    $"BackgroundJobs:NoShow:BatchSize must not exceed {NoShowOptions.MaxBatchSize} - "
                    + "a misconfigured value should never let one poll pull an unbounded number of candidate bookings into memory.");
            }
        }

        return ValidateOptionsResult.Success;
    }
}
