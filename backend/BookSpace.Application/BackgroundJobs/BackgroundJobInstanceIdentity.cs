using Microsoft.Extensions.Options;

namespace BookSpace.Application.BackgroundJobs;

// Computed once in the constructor and never again - registered as a singleton (see
// DependencyInjection.AddApplication), so this instance (and therefore OwnerId) lives exactly as long as
// the process does. The machine/instance name alone is deliberately not enough: two processes/containers
// can share it, so Environment.ProcessId and a fresh Guid are always appended too, making OwnerId unique
// even across two instances started on the same host at the same moment.
public sealed class BackgroundJobInstanceIdentity : IBackgroundJobInstanceIdentity
{
    public BackgroundJobInstanceIdentity(IOptions<BackgroundJobsOptions> options)
    {
        var namePart = string.IsNullOrWhiteSpace(options.Value.InstanceName)
            ? Environment.MachineName
            : options.Value.InstanceName;
        OwnerId = $"{namePart}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    }

    public string OwnerId { get; }
}
