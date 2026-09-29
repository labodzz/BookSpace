using BookSpace.Application.BackgroundJobs;
using Microsoft.Extensions.Options;
using Xunit;

namespace BookSpace.Application.Tests.BackgroundJobs;

public sealed class BackgroundJobInstanceIdentityTests
{
    [Fact]
    public void OwnerId_ReadMultipleTimes_IsStable()
    {
        var identity = new BackgroundJobInstanceIdentity(Options.Create(new BackgroundJobsOptions()));

        Assert.Equal(identity.OwnerId, identity.OwnerId);
    }

    // Simulates two application instances started at the same moment (e.g. two containers/processes on
    // the same host) - each gets its own identity object, and they must never collide, since OwnerId is
    // the whole basis of the acquire/renew/release protocol distinguishing one instance from another.
    [Fact]
    public void OwnerId_ForTwoSeparateInstances_IsNeverTheSame()
    {
        var options = Options.Create(new BackgroundJobsOptions());

        var first = new BackgroundJobInstanceIdentity(options).OwnerId;
        var second = new BackgroundJobInstanceIdentity(options).OwnerId;

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void OwnerId_WithAConfiguredInstanceName_StartsWithThatName()
    {
        var identity = new BackgroundJobInstanceIdentity(Options.Create(new BackgroundJobsOptions { InstanceName = "worker-1" }));

        Assert.StartsWith("worker-1:", identity.OwnerId);
    }

    [Fact]
    public void OwnerId_WithNoConfiguredInstanceName_FallsBackToTheMachineName()
    {
        var identity = new BackgroundJobInstanceIdentity(Options.Create(new BackgroundJobsOptions()));

        Assert.StartsWith($"{Environment.MachineName}:", identity.OwnerId);
    }

    // Not just the machine name: a machine/container name alone cannot distinguish two processes running
    // on the same host, which is exactly the scenario docs/background-jobs.md calls out as unsafe.
    [Fact]
    public void OwnerId_AlwaysIncludesTheCurrentProcessId()
    {
        var identity = new BackgroundJobInstanceIdentity(Options.Create(new BackgroundJobsOptions()));

        Assert.Contains($":{Environment.ProcessId}:", identity.OwnerId);
    }
}
