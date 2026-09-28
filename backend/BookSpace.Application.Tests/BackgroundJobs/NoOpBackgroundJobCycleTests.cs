using BookSpace.Application.BackgroundJobs;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookSpace.Application.Tests.BackgroundJobs;

public sealed class NoOpBackgroundJobCycleTests
{
    [Fact]
    public async Task RunCycleAsync_CompletesWithoutThrowing()
    {
        var cycle = new NoOpBackgroundJobCycle(NullLogger<NoOpBackgroundJobCycle>.Instance);

        await cycle.RunCycleAsync(CancellationToken.None);
    }
}
