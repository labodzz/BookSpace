namespace BookSpace.Application.Email;

// Only one provider exists today (Smtp) - the field exists so a future second provider (e.g. a
// transactional email API) is a new enum value and a new INotificationSender implementation, never a
// reinterpretation of what "Smtp" means.
public enum EmailProvider
{
    Smtp,
}

// Mirrors System.Net.Mail's long-standing naming so the concept is instantly familiar, independent of
// MailKit's own MailKit.Security.SecureSocketOptions enum (Infrastructure maps between the two - see
// SmtpSecurityExtensions) - Application stays free of a MailKit dependency entirely.
public enum SmtpSecurity
{
    StartTls,
    SslOnConnect,
    None,
}

// Bound from the "Email" configuration section - see docs/background-jobs.md ("Gmail SMTP sender").
// Provider-neutral shape even though Gmail is the only configuration this codebase ships a documented
// setup for today: nothing here is Gmail-specific except the default Host/Port values, which any other
// SMTP provider's own host/port can simply override. Disabled by default, same reasoning as
// BackgroundJobsOptions.Enabled - an unconfigured deployment must never attempt to send real email.
public sealed class EmailOptions
{
    public bool Enabled { get; init; }
    public EmailProvider Provider { get; init; } = EmailProvider.Smtp;

    // The address real recipients see as the sender - never defaulted, since sending "from" an address
    // nobody configured would be meaningless. Required (EmailOptionsValidator) only when Enabled=true.
    public string FromAddress { get; init; } = string.Empty;
    public string FromName { get; init; } = "BookSpace";

    public SmtpOptions Smtp { get; init; } = new();
}

// Host/Port/Security default to Gmail's documented SMTP submission endpoint (see
// docs/background-jobs.md) purely as a convenience for the one provider this codebase currently
// documents a setup for - Username/Password have no sensible default and are never read from
// appsettings*.json (see EmailOptionsValidator and docs/background-jobs.md, "Configuration and
// secrets") - only from user-secrets locally or Email__Smtp__Username/Email__Smtp__Password
// environment variables in a container/Azure.
public sealed class SmtpOptions
{
    public string Host { get; init; } = "smtp.gmail.com";
    public int Port { get; init; } = 587;
    public SmtpSecurity Security { get; init; } = SmtpSecurity.StartTls;
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
}
