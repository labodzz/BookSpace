using Microsoft.Extensions.Options;

namespace BookSpace.Application.Email;

// Registered against ValidateOnStart() (see Program.cs), same convention as
// BackgroundJobsOptionsValidator - a misconfigured Email section fails startup immediately rather than
// surfacing only once a real notification is due to be sent. Unlike BackgroundJobsOptionsValidator,
// every check here is gated behind Enabled=true first: when Email:Enabled is false (the default), no
// SMTP field is required at all, so Development/Testing/an unconfigured deployment never needs any
// email configuration or credentials just to start - see docs/background-jobs.md ("Gmail SMTP sender").
public sealed class EmailOptionsValidator : IValidateOptions<EmailOptions>
{
    public ValidateOptionsResult Validate(string? name, EmailOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        if (options.Provider != EmailProvider.Smtp)
        {
            return ValidateOptionsResult.Fail($"Email:Provider '{options.Provider}' is not a supported provider.");
        }

        if (string.IsNullOrWhiteSpace(options.FromAddress))
        {
            return ValidateOptionsResult.Fail("Email:FromAddress is required when Email:Enabled is true.");
        }

        if (string.IsNullOrWhiteSpace(options.Smtp.Host))
        {
            return ValidateOptionsResult.Fail("Email:Smtp:Host is required when Email:Enabled is true.");
        }

        if (options.Smtp.Port is <= 0 or > 65535)
        {
            return ValidateOptionsResult.Fail("Email:Smtp:Port must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(options.Smtp.Username))
        {
            return ValidateOptionsResult.Fail(
                "Email:Smtp:Username is required when Email:Enabled is true - set it via user-secrets locally "
                + "or the Email__Smtp__Username environment variable, never in an appsettings*.json file.");
        }

        if (string.IsNullOrWhiteSpace(options.Smtp.Password))
        {
            return ValidateOptionsResult.Fail(
                "Email:Smtp:Password is required when Email:Enabled is true - set it via user-secrets locally "
                + "or the Email__Smtp__Password environment variable (a Gmail App Password, never a real Google "
                + "account password), never in an appsettings*.json file.");
        }

        return ValidateOptionsResult.Success;
    }
}
