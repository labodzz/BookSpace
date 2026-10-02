namespace BookSpace.Application.Notifications;

// Pure, deterministic exponential backoff for a failed notification delivery attempt - see
// docs/background-jobs.md ("Exponential backoff"). No jitter: an uncontrolled System.Random here would
// make retry timing impossible to assert deterministically in tests, which this task explicitly treats as
// unacceptable. A future task is free to add jitter as long as it stays injectable/controlled rather than
// ambient randomness.
public static class NotificationRetryBackoff
{
    // attemptNumber is the attempt that just failed (1-based: the first failure is attempt 1). Delay
    // doubles each time - delay(1) = initialRetryDelaySeconds, delay(2) = initialRetryDelaySeconds * 2,
    // delay(3) = * 4, and so on - clamped to maxRetryDelaySeconds.
    //
    // Computed with double/Math.Pow rather than integer bit-shifting specifically so a large
    // attemptNumber can never overflow: Math.Pow saturates to double.PositiveInfinity rather than
    // wrapping/throwing, and the Math.Min clamp below always wins long before that could matter -
    // BackgroundJobsOptionsValidator additionally bounds MaxNotificationAttempts itself (see
    // BackgroundJobsOptions.MaxAllowedNotificationAttempts), so attemptNumber is never actually large in
    // practice; this is defense-in-depth, not the only guard.
    public static TimeSpan ComputeDelay(int attemptNumber, int initialRetryDelaySeconds, int maxRetryDelaySeconds)
    {
        if (attemptNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptNumber), attemptNumber, "attemptNumber must be at least 1.");
        }

        var exponent = Math.Min(attemptNumber - 1, 62);
        var uncappedSeconds = initialRetryDelaySeconds * Math.Pow(2, exponent);
        var cappedSeconds = Math.Min(uncappedSeconds, maxRetryDelaySeconds);
        return TimeSpan.FromSeconds(cappedSeconds);
    }
}
