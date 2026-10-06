using Microsoft.Extensions.Options;

namespace BookSpace.Application.Calendar;

// Registered against ValidateOnStart() (see Program.cs) so a missing/malformed PublicBaseUrl fails
// startup immediately and loudly, rather than only surfacing once a member tries to create their first
// subscription - see docs/calendar-subscription-feed.md.
public sealed class CalendarFeedOptionsValidator : IValidateOptions<CalendarFeedOptions>
{
    public ValidateOptionsResult Validate(string? name, CalendarFeedOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.PublicBaseUrl))
        {
            return ValidateOptionsResult.Fail(
                "CalendarFeed:PublicBaseUrl must be configured - see docs/calendar-subscription-feed.md.");
        }

        // Absolute only (no relative path, no query/fragment-only value) and http(s) only - never a
        // scheme this app couldn't itself be served over.
        if (!Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return ValidateOptionsResult.Fail("CalendarFeed:PublicBaseUrl must be an absolute http(s) URL, e.g. \"https://bookspace.example.com\".");
        }

        if (options.PastWindowDays <= 0)
        {
            return ValidateOptionsResult.Fail("CalendarFeed:PastWindowDays must be a positive number of days.");
        }

        if (options.PastWindowDays > CalendarFeedOptions.MaxPastWindowDays)
        {
            return ValidateOptionsResult.Fail($"CalendarFeed:PastWindowDays must not exceed {CalendarFeedOptions.MaxPastWindowDays}.");
        }

        if (options.FutureWindowDays <= 0)
        {
            return ValidateOptionsResult.Fail("CalendarFeed:FutureWindowDays must be a positive number of days.");
        }

        if (options.FutureWindowDays > CalendarFeedOptions.MaxFutureWindowDays)
        {
            return ValidateOptionsResult.Fail($"CalendarFeed:FutureWindowDays must not exceed {CalendarFeedOptions.MaxFutureWindowDays}.");
        }

        return ValidateOptionsResult.Success;
    }
}
