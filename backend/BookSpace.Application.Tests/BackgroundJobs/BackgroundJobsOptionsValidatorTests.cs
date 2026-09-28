using BookSpace.Application.BackgroundJobs;
using Xunit;

namespace BookSpace.Application.Tests.BackgroundJobs;

public sealed class BackgroundJobsOptionsValidatorTests
{
    private readonly BackgroundJobsOptionsValidator _validator = new();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-30)]
    public void Validate_WithAZeroOrNegativePollInterval_Fails(int pollIntervalSeconds)
    {
        var result = _validator.Validate(name: null, new BackgroundJobsOptions { PollIntervalSeconds = pollIntervalSeconds });

        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    public void Validate_WithAPositivePollInterval_Succeeds(int pollIntervalSeconds)
    {
        var result = _validator.Validate(name: null, new BackgroundJobsOptions { PollIntervalSeconds = pollIntervalSeconds });

        Assert.True(result.Succeeded);
    }
}
