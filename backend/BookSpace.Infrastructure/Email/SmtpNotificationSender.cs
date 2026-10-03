using System.Diagnostics;
using System.Net.Sockets;
using BookSpace.Application.Email;
using BookSpace.Application.Notifications;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace BookSpace.Infrastructure.Email;

// The production INotificationSender - Gmail (or any STARTTLS/SMTP-AUTH provider) via MailKit. See
// docs/background-jobs.md ("Gmail SMTP sender") for the full design: why Gmail/MailKit, the crash window
// and at-least-once delivery semantics, and the transient/permanent classification table. This class owns
// none of NotificationOutboxProcessor's retry/backoff/AttemptCount/dead-letter machinery - it returns
// exactly one NotificationSendResult per call and never retries internally.
internal sealed class SmtpNotificationSender(
    IOptions<EmailOptions> options,
    IEmailRecipientResolver recipientResolver,
    IEmailTemplateRenderer templateRenderer,
    ISmtpTransportFactory transportFactory,
    ILogger<SmtpNotificationSender> logger) : INotificationSender
{
    public async Task<NotificationSendResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken)
    {
        var emailOptions = options.Value;

        // Disabled is the default (EmailOptions.Enabled) - a deployment that never configured Email must
        // never silently report success for a notification nothing actually delivered. This is checked
        // before any recipient lookup/network call, not caught as some kind of exception, and is logged
        // with no credential involved.
        if (!emailOptions.Enabled)
        {
            logger.LogInformation(
                "Notification email not sent: Email:Enabled is false. OutboxItemId={OutboxItemId}", message.OutboxItemId);
            return NotificationSendResult.Permanent("Email sending is disabled (Email:Enabled=false).");
        }

        var recipient = await recipientResolver.ResolveAsync(message.TenantId, message.RecipientUserId, cancellationToken);
        if (recipient is null)
        {
            logger.LogInformation(
                "Notification email not sent: no recipient with a usable email address. OutboxItemId={OutboxItemId} TenantId={TenantId}",
                message.OutboxItemId, message.TenantId);
            return NotificationSendResult.Permanent("Recipient not found or has no usable email address.");
        }

        EmailContent content;
        try
        {
            content = templateRenderer.Render(message.NotificationType, message.PayloadJson, recipient.DisplayName);
        }
        catch (EmailTemplateException exception)
        {
            logger.LogInformation(
                "Notification email not sent: template rendering failed. OutboxItemId={OutboxItemId} NotificationType={NotificationType} Reason={Reason}",
                message.OutboxItemId, message.NotificationType, exception.Message);
            return NotificationSendResult.Permanent($"Unable to render notification: {exception.Message}");
        }

        var mimeMessage = BuildMimeMessage(emailOptions, recipient, message.OutboxItemId, content);

        var stopwatch = Stopwatch.StartNew();
        await using var transport = transportFactory.Create();
        try
        {
            var security = MapSecurity(emailOptions.Smtp.Security);
            await transport.ConnectAsync(emailOptions.Smtp.Host, emailOptions.Smtp.Port, security, cancellationToken);
            await transport.AuthenticateAsync(emailOptions.Smtp.Username, emailOptions.Smtp.Password, cancellationToken);
            await transport.SendAsync(mimeMessage, cancellationToken);
            await transport.DisconnectAsync(true, cancellationToken);

            logger.LogInformation(
                "Notification email sent. OutboxItemId={OutboxItemId} NotificationType={NotificationType} DurationMs={DurationMs}",
                message.OutboxItemId, message.NotificationType, stopwatch.ElapsedMilliseconds);
            return NotificationSendResult.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Never reclassified as a delivery outcome - propagates untouched, exactly like
            // NotificationOutboxProcessor expects (see INotificationSender's own doc comment).
            throw;
        }
        catch (Exception exception)
        {
            return Classify(exception, message, stopwatch.ElapsedMilliseconds);
        }
    }

    private static MimeMessage BuildMimeMessage(EmailOptions emailOptions, EmailRecipient recipient, Guid outboxItemId, EmailContent content)
    {
        var mimeMessage = new MimeMessage();
        mimeMessage.From.Add(new MailboxAddress(emailOptions.FromName, emailOptions.FromAddress));
        mimeMessage.To.Add(new MailboxAddress(recipient.DisplayName, recipient.Email));
        mimeMessage.Subject = content.Subject;

        // Deterministic from the outbox item id alone - identical on a retry of the same item (same id,
        // same content). NOT a dedup guarantee: Gmail/SMTP is not obligated to honor Message-Id for
        // deduplication - this exists for traceability and possible provider/client-side dedup only. See
        // docs/background-jobs.md ("Message-Id and the SMTP/Gmail crash window"). No angle brackets here -
        // MimeKit's MessageId setter expects a bare addr-spec and adds the <...> wrapper itself when the
        // header is actually written on the wire.
        mimeMessage.MessageId = $"bookspace-outbox-{outboxItemId:D}@bookspace.local";
        mimeMessage.Headers.Add("X-BookSpace-Outbox-Id", outboxItemId.ToString());

        var bodyBuilder = new BodyBuilder { TextBody = content.PlainTextBody, HtmlBody = content.HtmlBody };
        mimeMessage.Body = bodyBuilder.ToMessageBody();
        return mimeMessage;
    }

    private static SecureSocketOptions MapSecurity(SmtpSecurity security) => security switch
    {
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        SmtpSecurity.None => SecureSocketOptions.None,
        _ => throw new ArgumentOutOfRangeException(nameof(security), security, "Unsupported SMTP security option."),
    };

    // See docs/background-jobs.md ("Transient/permanent classification") for the full table and the
    // reasoning behind the conservative default. Never logs exception.Message/ToString() or the raw
    // exception object here - only safe, structured fields (exception type name, SMTP status code) - a
    // MailKit auth/command exception's own message can embed server response text this sender has no way
    // to guarantee is credential-free.
    private NotificationSendResult Classify(Exception exception, NotificationMessage message, long durationMs)
    {
        switch (exception)
        {
            case AuthenticationException:
                logger.LogWarning(
                    "Notification email send failed: SMTP authentication failed. OutboxItemId={OutboxItemId} DurationMs={DurationMs}",
                    message.OutboxItemId, durationMs);
                return NotificationSendResult.Permanent("SMTP authentication failed.");

            case SmtpCommandException smtpCommandException:
                var statusCode = (int)smtpCommandException.StatusCode;
                var isTransient = statusCode is >= 400 and < 500;
                logger.LogWarning(
                    "Notification email send failed: SMTP command error. OutboxItemId={OutboxItemId} SmtpStatusCode={SmtpStatusCode} DurationMs={DurationMs}",
                    message.OutboxItemId, statusCode, durationMs);
                return isTransient
                    ? NotificationSendResult.Transient($"SMTP transient error ({statusCode}).")
                    : NotificationSendResult.Permanent($"SMTP permanent error ({statusCode}).");

            case SmtpProtocolException or SocketException or IOException:
                logger.LogWarning(
                    "Notification email send failed: transport-level error ({ExceptionType}). OutboxItemId={OutboxItemId} DurationMs={DurationMs}",
                    exception.GetType().Name, message.OutboxItemId, durationMs);
                return NotificationSendResult.Transient("A transport-level error occurred while sending.");

            case ArgumentException or FormatException:
                logger.LogWarning(
                    "Notification email send failed: invalid configuration or address ({ExceptionType}). OutboxItemId={OutboxItemId} DurationMs={DurationMs}",
                    exception.GetType().Name, message.OutboxItemId, durationMs);
                return NotificationSendResult.Permanent("Invalid email configuration or address.");

            default:
                // Conservative, documented choice: an exception type this sender does not specifically
                // recognize is treated as transient, bounded by NotificationOutboxProcessor's own
                // max-attempt cap, rather than dead-lettering on the first occurrence of something that
                // might turn out to be a brief condition this sender simply didn't anticipate.
                logger.LogWarning(
                    "Notification email send failed: unclassified error ({ExceptionType}). OutboxItemId={OutboxItemId} DurationMs={DurationMs}",
                    exception.GetType().Name, message.OutboxItemId, durationMs);
                return NotificationSendResult.Transient("An unexpected error occurred while sending.");
        }
    }
}
