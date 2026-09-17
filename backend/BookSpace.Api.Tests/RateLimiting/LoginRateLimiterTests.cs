using System.Threading.RateLimiting;
using Xunit;

namespace BookSpace.Api.Tests.RateLimiting;

// Exercises the exact FixedWindowRateLimiterOptions Program.cs configures for the "login" policy, in
// isolation from the ASP.NET Core host - WebApplicationFactory's in-memory TestServer gives every
// request the same RemoteIpAddress (or none), so a true end-to-end 429 test would collapse the whole
// integration suite's own login traffic into one partition and trip the limiter on suite volume, not
// a genuine brute-force signal (this is exactly why Program.cs raises PermitLimit in the Testing
// environment - see its own comment). This tests the limiter's configured behavior directly instead.
public sealed class LoginRateLimiterTests
{
    private static FixedWindowRateLimiter CreateLoginLimiter() => new(new FixedWindowRateLimiterOptions
    {
        PermitLimit = 10,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    });

    [Fact]
    public void AttemptAcquire_UpToThePermitLimit_AllSucceed()
    {
        using var limiter = CreateLoginLimiter();

        for (var i = 0; i < 10; i++)
        {
            using var lease = limiter.AttemptAcquire();
            Assert.True(lease.IsAcquired);
        }
    }

    [Fact]
    public void AttemptAcquire_BeyondThePermitLimitWithinTheWindow_IsRejected()
    {
        using var limiter = CreateLoginLimiter();
        for (var i = 0; i < 10; i++)
        {
            using var lease = limiter.AttemptAcquire();
            Assert.True(lease.IsAcquired);
        }

        using var eleventh = limiter.AttemptAcquire();

        Assert.False(eleventh.IsAcquired);
    }

    [Fact]
    public void AttemptAcquire_WithNoQueueing_RejectsImmediatelyRatherThanWaiting()
    {
        // QueueLimit = 0: a rejected caller gets an immediate 429, never waits in a queue for the next
        // window - appropriate for a login endpoint, where queueing would just delay the caller's
        // feedback rather than protect anything.
        using var limiter = CreateLoginLimiter();
        for (var i = 0; i < 10; i++)
        {
            limiter.AttemptAcquire().Dispose();
        }

        using var rejected = limiter.AttemptAcquire();

        Assert.False(rejected.IsAcquired);
        Assert.Equal(0, limiter.GetStatistics()?.CurrentQueuedCount);
    }
}
