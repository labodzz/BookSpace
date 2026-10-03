using MailKit.Security;
using MimeKit;

namespace BookSpace.Infrastructure.Email;

// Thin seam around MailKit's SmtpClient so SmtpNotificationSender is testable without a real network
// connection or a real Gmail account - see docs/background-jobs.md ("SMTP connection lifetime"). Exactly
// the 4 operations one send needs, nothing else of MailKit's much larger SmtpClient surface is exposed.
// An implementation is never thread-safe and must never be shared across concurrent sends or registered
// as a singleton - see MailKitSmtpTransport/MailKitSmtpTransportFactory.
public interface ISmtpTransport : IAsyncDisposable
{
    Task ConnectAsync(string host, int port, SecureSocketOptions security, CancellationToken cancellationToken);

    Task AuthenticateAsync(string username, string password, CancellationToken cancellationToken);

    Task SendAsync(MimeMessage message, CancellationToken cancellationToken);

    Task DisconnectAsync(bool quit, CancellationToken cancellationToken);
}

// Creates a brand new ISmtpTransport for every call - never reused across messages, never shared across
// threads. See docs/background-jobs.md ("SMTP connection lifetime") for why one connection per message is
// the deliberate choice here, not a performance compromise nobody considered.
public interface ISmtpTransportFactory
{
    ISmtpTransport Create();
}
