using System.Threading.RateLimiting;
using Xunit;

namespace BookSpace.Api.Tests.RateLimiting;

// Exercises the exact FixedWindowRateLimiterOptions Program.cs configures for the "ai-booking" policy, in
// isolation from the ASP.NET Core host - see LoginRateLimiterTests's own doc comment for why
// WebApplicationFactory's in-memory TestServer makes a true end-to-end 429 test meaningless here (Program.cs
// raises PermitLimit to int.MaxValue in the Testing environment for exactly this reason).
public sealed class AiBookingRateLimiterTests
{
    private static FixedWindowRateLimiter CreateAiBookingLimiter() => new(new FixedWindowRateLimiterOptions
    {
        PermitLimit = 6,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    });

    [Fact]
    public void AttemptAcquire_UpToThePermitLimit_AllSucceed()
    {
        using var limiter = CreateAiBookingLimiter();

        for (var i = 0; i < 6; i++)
        {
            using var lease = limiter.AttemptAcquire();
            Assert.True(lease.IsAcquired);
        }
    }

    [Fact]
    public void AttemptAcquire_BeyondThePermitLimitWithinTheWindow_IsRejected()
    {
        using var limiter = CreateAiBookingLimiter();
        for (var i = 0; i < 6; i++)
        {
            limiter.AttemptAcquire().Dispose();
        }

        using var seventh = limiter.AttemptAcquire();

        Assert.False(seventh.IsAcquired);
    }
}
