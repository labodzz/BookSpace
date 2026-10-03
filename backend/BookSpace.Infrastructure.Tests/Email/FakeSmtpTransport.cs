using BookSpace.Infrastructure.Email;
using MailKit.Security;
using MimeKit;

namespace BookSpace.Infrastructure.Tests.Email;

// Records every call it receives and can be configured to fail at any one of the four steps - used so
// SmtpNotificationSenderTests never touches a real network connection or a real Gmail account. Mirrors
// the exact ISmtpTransport contract the production MailKitSmtpTransport implements.
internal sealed class FakeSmtpTransport(
    Exception? connectFailure = null, Exception? authenticateFailure = null, Exception? sendFailure = null) : ISmtpTransport
{
    public bool Connected { get; private set; }
    public bool Authenticated { get; private set; }
    public bool Disconnected { get; private set; }
    public bool Disposed { get; private set; }
    public string? AuthenticatedUsername { get; private set; }
    public string? AuthenticatedPassword { get; private set; }
    public MimeMessage? SentMessage { get; private set; }

    public Task ConnectAsync(string host, int port, SecureSocketOptions security, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (connectFailure is not null)
        {
            throw connectFailure;
        }

        Connected = true;
        return Task.CompletedTask;
    }

    public Task AuthenticateAsync(string username, string password, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (authenticateFailure is not null)
        {
            throw authenticateFailure;
        }

        AuthenticatedUsername = username;
        AuthenticatedPassword = password;
        Authenticated = true;
        return Task.CompletedTask;
    }

    public Task SendAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sendFailure is not null)
        {
            throw sendFailure;
        }

        SentMessage = message;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(bool quit, CancellationToken cancellationToken)
    {
        Disconnected = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeSmtpTransportFactory(Func<ISmtpTransport> create) : ISmtpTransportFactory
{
    public List<ISmtpTransport> CreatedTransports { get; } = [];

    public ISmtpTransport Create()
    {
        var transport = create();
        CreatedTransports.Add(transport);
        return transport;
    }
}

// Proves the sender never even asks for a transport in a scenario that must short-circuit before any
// network concern (disabled sending, missing recipient, template failure).
internal sealed class ThrowingSmtpTransportFactory : ISmtpTransportFactory
{
    public ISmtpTransport Create() => throw new InvalidOperationException("No transport should be created for this scenario.");
}

internal sealed class FakeEmailRecipientResolver(BookSpace.Application.Notifications.EmailRecipient? result) : BookSpace.Application.Notifications.IEmailRecipientResolver
{
    public Guid? LastTenantId { get; private set; }
    public Guid? LastRecipientUserId { get; private set; }

    public Task<BookSpace.Application.Notifications.EmailRecipient?> ResolveAsync(Guid tenantId, Guid recipientUserId, CancellationToken cancellationToken)
    {
        LastTenantId = tenantId;
        LastRecipientUserId = recipientUserId;
        return Task.FromResult(result);
    }
}

internal sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Messages.Add(formatter(state, exception));
        if (exception is not null)
        {
            Messages.Add(exception.ToString());
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
