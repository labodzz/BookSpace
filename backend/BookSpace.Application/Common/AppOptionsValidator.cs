using Microsoft.Extensions.Options;

namespace BookSpace.Application.Common;

// Registered against ValidateOnStart() (see Program.cs) so a missing/malformed PublicBaseUrl fails
// startup immediately and loudly, rather than only surfacing once a SysAdmin tries to onboard the first
// tenant - the same ValidateOnStart() convention every other options validator in this codebase uses
// (BackgroundJobsOptionsValidator, EmailOptionsValidator, CalendarFeedOptionsValidator).
public sealed class AppOptionsValidator : IValidateOptions<AppOptions>
{
    public ValidateOptionsResult Validate(string? name, AppOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.PublicBaseUrl))
        {
            return ValidateOptionsResult.Fail("App:PublicBaseUrl must be configured - see docs/tenant-onboarding.md.");
        }

        if (!Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return ValidateOptionsResult.Fail("App:PublicBaseUrl must be an absolute http(s) URL, e.g. \"https://bookspace.example.com\".");
        }

        return ValidateOptionsResult.Success;
    }
}
