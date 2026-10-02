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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WithAZeroOrNegativeLeaseDuration_Fails(int leaseDurationSeconds)
    {
        var result = _validator.Validate(name: null, new BackgroundJobsOptions { LeaseDurationSeconds = leaseDurationSeconds });

        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WithAZeroOrNegativeHeartbeatInterval_Fails(int heartbeatIntervalSeconds)
    {
        var result = _validator.Validate(name: null, new BackgroundJobsOptions { HeartbeatIntervalSeconds = heartbeatIntervalSeconds });

        Assert.True(result.Failed);
    }

    // Equal counts as "not strictly less than" - a heartbeat that only just keeps pace with the lease
    // duration leaves no margin for a single delayed renewal at all.
    [Theory]
    [InlineData(60, 60)]
    [InlineData(60, 90)]
    public void Validate_WhenHeartbeatIntervalIsNotStrictlyLessThanLeaseDuration_Fails(int leaseDurationSeconds, int heartbeatIntervalSeconds)
    {
        var result = _validator.Validate(name: null, new BackgroundJobsOptions
        {
            LeaseDurationSeconds = leaseDurationSeconds,
            HeartbeatIntervalSeconds = heartbeatIntervalSeconds,
        });

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_WithTheDefaultLeaseAndHeartbeatValues_Succeeds()
    {
        var result = _validator.Validate(name: null, new BackgroundJobsOptions());

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WithAZeroOrNegativeBatchSize_Fails(int batchSize)
    {
        var result = _validator.Validate(name: null, new BackgroundJobsOptions { BatchSize = batchSize });

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_WithABatchSizeAboveTheMaximum_Fails()
    {
        var result = _validator.Validate(name: null, new BackgroundJobsOptions { BatchSize = BackgroundJobsOptions.MaxBatchSize + 1 });

        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    public void Validate_WithAnOrdinaryPositiveBatchSize_Succeeds(int batchSize)
    {
        var result = _validator.Validate(name: null, new BackgroundJobsOptions { BatchSize = batchSize });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_WithABatchSizeExactlyAtTheMaximum_Succeeds()
    {
        var result = _validator.Validate(name: null, new BackgroundJobsOptions { BatchSize = BackgroundJobsOptions.MaxBatchSize });

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WithAZeroOrNegativeMaxNotificationAttempts_Fails(int maxNotificationAttempts)
    {
        var result = _validator.Validate(name: null, new BackgroundJobsOptions { MaxNotificationAttempts = maxNotificationAttempts });

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_WithMaxNotificationAttemptsAboveTheAllowedMaximum_Fails()
    {
        var result = _validator.Validate(
            name: null, new BackgroundJobsOptions { MaxNotificationAttempts = BackgroundJobsOptions.MaxAllowedNotificationAttempts + 1 });

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_WithMaxNotificationAttemptsOfExactlyOne_Succeeds()
    {
        var result = _validator.Validate(name: null, new BackgroundJobsOptions { MaxNotificationAttempts = 1 });

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WithAZeroOrNegativeInitialRetryDelay_Fails(int initialRetryDelaySeconds)
    {
        var result = _validator.Validate(name: null, new BackgroundJobsOptions { InitialRetryDelaySeconds = initialRetryDelaySeconds });

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_WhenMaxRetryDelayIsLessThanInitialRetryDelay_Fails()
    {
        var result = _validator.Validate(
            name: null, new BackgroundJobsOptions { InitialRetryDelaySeconds = 120, MaxRetryDelaySeconds = 60 });

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_WhenMaxRetryDelayEqualsInitialRetryDelay_Succeeds()
    {
        var result = _validator.Validate(
            name: null, new BackgroundJobsOptions { InitialRetryDelaySeconds = 60, MaxRetryDelaySeconds = 60 });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_WithMaxRetryDelayAboveTheAllowedMaximum_Fails()
    {
        var result = _validator.Validate(
            name: null, new BackgroundJobsOptions { MaxRetryDelaySeconds = BackgroundJobsOptions.MaxAllowedRetryDelaySeconds + 1 });

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_WithTheDefaultRetrySettings_Succeeds()
    {
        var result = _validator.Validate(name: null, new BackgroundJobsOptions());

        Assert.True(result.Succeeded);
    }
}
