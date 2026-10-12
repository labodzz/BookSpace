using Microsoft.Extensions.Options;

namespace BookSpace.Application.AiBooking;

// Registered against ValidateOnStart() (see Program.cs), same convention as EmailOptionsValidator: every
// check here is gated behind Enabled=true first, so Development/Testing/an unconfigured deployment never
// needs an API key - or any other AiBooking configuration at all - just to start.
public sealed class AiBookingOptionsValidator : IValidateOptions<AiBookingOptions>
{
    private const int MaxRequestTimeoutSeconds = 60;
    private const int MaxOutputTokensCeiling = 2000;

    public ValidateOptionsResult Validate(string? name, AiBookingOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return ValidateOptionsResult.Fail(
                "AiBooking:ApiKey is required when AiBooking:Enabled is true - set it via user-secrets locally "
                + "or the AiBooking__ApiKey environment variable, never in an appsettings*.json file.");
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            return ValidateOptionsResult.Fail("AiBooking:Model is required when AiBooking:Enabled is true.");
        }

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return ValidateOptionsResult.Fail("AiBooking:BaseUrl must be an absolute http(s) URL.");
        }

        if (options.RequestTimeoutSeconds <= 0)
        {
            return ValidateOptionsResult.Fail("AiBooking:RequestTimeoutSeconds must be a positive number of seconds.");
        }

        if (options.RequestTimeoutSeconds > MaxRequestTimeoutSeconds)
        {
            return ValidateOptionsResult.Fail(
                $"AiBooking:RequestTimeoutSeconds must not exceed {MaxRequestTimeoutSeconds} - a single HTTP request "
                + "inside an authenticated API call must never be allowed to hang indefinitely.");
        }

        if (options.MaxOutputTokens <= 0)
        {
            return ValidateOptionsResult.Fail("AiBooking:MaxOutputTokens must be a positive number of tokens.");
        }

        if (options.MaxOutputTokens > MaxOutputTokensCeiling)
        {
            return ValidateOptionsResult.Fail($"AiBooking:MaxOutputTokens must not exceed {MaxOutputTokensCeiling}.");
        }

        return ValidateOptionsResult.Success;
    }
}
