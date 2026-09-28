using Microsoft.Extensions.Options;

namespace BookSpace.Application.BackgroundJobs;

// Registered against ValidateOnStart() (see Program.cs) so a misconfigured PollIntervalSeconds fails
// startup immediately and loudly, rather than letting BackgroundJobsWorker discover a zero/negative
// interval only once it tries to poll - see docs/background-jobs.md.
public sealed class BackgroundJobsOptionsValidator : IValidateOptions<BackgroundJobsOptions>
{
    public ValidateOptionsResult Validate(string? name, BackgroundJobsOptions options) =>
        options.PollIntervalSeconds > 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("BackgroundJobs:PollIntervalSeconds must be a positive number of seconds.");
}
