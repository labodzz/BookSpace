namespace BookSpace.Application.Tenants;

// Builds the absolute, frontend-facing invitation-acceptance URL a tenant-onboarding email links to - the
// same "build from a configured public base, never from the current request's Host header" shape
// BookSpace.Application/Calendar/CalendarFeedUrlBuilder already established for its own, separate feature.
// Points at the Angular SPA's own client-side route (no "/api" prefix - this is never an API endpoint
// itself), which the frontend registers as a public (guestGuard-gated) route - see
// docs/tenant-onboarding.md.
internal static class TenantInvitationUrlBuilder
{
    public static string Build(string publicBaseUrl, string rawToken) =>
        $"{publicBaseUrl.TrimEnd('/')}/accept-invitation?token={Uri.EscapeDataString(rawToken)}";
}
