using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace BookSpace.Infrastructure.Email;

// The only production ISmtpTransport - a thin wrapper over a real MailKit.Net.Smtp.SmtpClient. One
// instance is created, used for exactly one message, then disposed - see MailKitSmtpTransportFactory and
// docs/background-jobs.md ("SMTP connection lifetime").
internal sealed class MailKitSmtpTransport : ISmtpTransport
{
    private readonly SmtpClient _client = new();

    public Task ConnectAsync(string host, int port, SecureSocketOptions security, CancellationToken cancellationToken) =>
        _client.ConnectAsync(host, port, security, cancellationToken);

    public Task AuthenticateAsync(string username, string password, CancellationToken cancellationToken) =>
        _client.AuthenticateAsync(username, password, cancellationToken);

    public Task SendAsync(MimeMessage message, CancellationToken cancellationToken) =>
        _client.SendAsync(message, cancellationToken);

    public Task DisconnectAsync(bool quit, CancellationToken cancellationToken) =>
        _client.DisconnectAsync(quit, cancellationToken);

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}

// Registered as a singleton - it is stateless and only ever hands out brand new transport instances, never
// holds or reuses one itself. See docs/background-jobs.md ("SMTP connection lifetime").
internal sealed class MailKitSmtpTransportFactory : ISmtpTransportFactory
{
    public ISmtpTransport Create() => new MailKitSmtpTransport();
}
