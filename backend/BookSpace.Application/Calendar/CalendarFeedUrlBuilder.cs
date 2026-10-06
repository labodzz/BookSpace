namespace BookSpace.Application.Calendar;

// Shared by Create/RegenerateCalendarSubscriptionCommandHandler - both need to turn a freshly minted raw
// token into the same absolute, pollable feed URL. See CalendarFeedOptions.PublicBaseUrl for why this is
// built from a configured base, never from the current request's Host header.
internal static class CalendarFeedUrlBuilder
{
    public static string Build(string publicBaseUrl, string rawToken) =>
        $"{publicBaseUrl.TrimEnd('/')}/api/calendar-feed?token={Uri.EscapeDataString(rawToken)}";
}
