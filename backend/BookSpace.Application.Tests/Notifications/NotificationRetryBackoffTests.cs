using BookSpace.Application.Notifications;
using Xunit;

namespace BookSpace.Application.Tests.Notifications;

public sealed class NotificationRetryBackoffTests
{
    [Theory]
    [InlineData(1, 60)]
    [InlineData(2, 120)]
    [InlineData(3, 240)]
    [InlineData(4, 480)]
    public void ComputeDelay_DoublesWithEachAttempt_WhenBelowTheMaximum(int attemptNumber, int expectedSeconds)
    {
        var delay = NotificationRetryBackoff.ComputeDelay(attemptNumber, initialRetryDelaySeconds: 60, maxRetryDelaySeconds: 3600);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public void ComputeDelay_NeverExceedsTheMaximumDelay()
    {
        // Attempt 10 would be 60 * 2^9 = 30720s uncapped - far past the 3600s maximum.
        var delay = NotificationRetryBackoff.ComputeDelay(10, initialRetryDelaySeconds: 60, maxRetryDelaySeconds: 3600);

        Assert.Equal(TimeSpan.FromSeconds(3600), delay);
    }

    [Fact]
    public void ComputeDelay_WithAVeryLargeAttemptNumber_DoesNotOverflowAndStillClampsToTheMaximum()
    {
        var delay = NotificationRetryBackoff.ComputeDelay(1_000_000, initialRetryDelaySeconds: 60, maxRetryDelaySeconds: 3600);

        Assert.Equal(TimeSpan.FromSeconds(3600), delay);
    }

    [Fact]
    public void ComputeDelay_IsDeterministic_SameInputsAlwaysProduceTheSameDelay()
    {
        var first = NotificationRetryBackoff.ComputeDelay(3, 60, 3600);
        var second = NotificationRetryBackoff.ComputeDelay(3, 60, 3600);

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ComputeDelay_WithAnAttemptNumberBelowOne_Throws(int invalidAttemptNumber)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NotificationRetryBackoff.ComputeDelay(invalidAttemptNumber, 60, 3600));
    }
}
