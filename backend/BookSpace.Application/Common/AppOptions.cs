namespace BookSpace.Application.Common;

// Bound from the "App" configuration section - see docs/tenant-onboarding.md ("Public URL configuration").
// Deliberately a separate setting from CalendarFeedOptions.PublicBaseUrl (BookSpace.Application/Calendar/),
// not a shared one - and NOT always the same value, unlike what might first seem natural:
// - In a packaged/container deployment (Production, or local Podman/Compose), Angular and the API are
//   served from the one and only origin (see docs/architecture.md) - both settings SHOULD be configured
//   identically there.
// - In local Development, they are genuinely different origins: `ng serve` serves the Angular SPA on
//   http://localhost:4200 (see Cors:AllowedOrigins), while the API itself runs on a different port (see
//   appsettings.Development.json). This setting must point at the FRONTEND origin (where the
//   browser-rendered accept-invitation PAGE lives), while CalendarFeedOptions.PublicBaseUrl points at the
//   API origin (an endpoint an external calendar app polls directly, never a page a browser renders) - the
//   two are not interchangeable even though they happen to coincide in a packaged deployment.
public sealed class AppOptions
{
    // The externally reachable origin (scheme + host, no path, no trailing slash) this deployment is
    // actually served from - e.g. "https://bookspace.example.com". Used to build the absolute invitation-
    // acceptance URL a tenant-onboarding email links to (TenantInvitationUrlBuilder.Build), never from the
    // incoming request's Host header - a Host header is caller-controlled and untrustworthy as the source
    // of a URL this app hands to a client (the same Host-header-injection concern
    // CalendarFeedOptions.PublicBaseUrl's own doc comment already documents for its own, separate setting).
    public string? PublicBaseUrl { get; init; }
}
