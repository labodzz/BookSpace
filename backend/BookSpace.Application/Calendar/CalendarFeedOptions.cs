namespace BookSpace.Application.Calendar;

// Bound from the "CalendarFeed" configuration section - see docs/calendar-subscription-feed.md.
public sealed class CalendarFeedOptions
{
    public const int MaxPastWindowDays = 365;
    public const int MaxFutureWindowDays = 730; // 2 years

    // The externally reachable origin (scheme + host, no path, no trailing slash) this deployment is
    // actually served from - e.g. "https://bookspace.example.com". Used to build the full, absolute
    // subscription URL returned to a caller on Create/Regenerate: an external calendar client needs a
    // real, fully-qualified URL to poll, not a path relative to this API. Deliberately never derived from
    // the incoming request's Host header - a Host header is caller-controlled and untrustworthy as a
    // source for a URL this app hands back to a client (classic Host-header-injection risk). Required
    // (CalendarFeedOptionsValidator fails startup without it), the same explicitly-configured,
    // reverse-proxy-aware pattern Cors:AllowedOrigins already uses - see
    // docs/container-deployment.md ("Reverse proxy and Azure ingress").
    public string? PublicBaseUrl { get; init; }

    // How far into the past, and how far into the future, GetCalendarFeedQueryHandler's window reaches
    // from "now" - see docs/calendar-subscription-feed.md ("Time window") for why both bounds exist and
    // why this stays the feed's only size bound (no separate event-count cap).
    public int PastWindowDays { get; init; } = 30;
    public int FutureWindowDays { get; init; } = 365;
}
