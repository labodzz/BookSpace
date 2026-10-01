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

        return ValidateOptionsResult.Success;
    }
}
