using System.Net.Sockets;
using System.Text.Json;
using BookSpace.Application.Email;
using BookSpace.Application.Notifications;
using BookSpace.Infrastructure.Email;
using MailKit.Net.Smtp;
using MailKit.Security;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Email;

public sealed class SmtpNotificationSenderTests
{
    private static readonly BookingNotificationPayload SamplePayload = new(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(2), 3);
    private static readonly EmailRecipient SampleRecipient = new("owner@bookspace.test", "Booking Owner");

    private static NotificationMessage BuildMessage(Guid? outboxItemId = null, string notificationType = BookingNotificationTypes.Confirmation, string? payloadJson = null) =>
        new(outboxItemId ?? Guid.NewGuid(), Guid.NewGuid(), notificationType, Guid.NewGuid(), payloadJson ?? JsonSerializer.Serialize(SamplePayload));

    private static EmailOptions EnabledOptions(string password = "a-gmail-app-password-value") => new()
    {
        Enabled = true,
        FromAddress = "bookspace.sender@gmail.com",
        FromName = "BookSpace",
        Smtp = new SmtpOptions
        {
            Host = "smtp.gmail.com", Port = 587, Security = SmtpSecurity.StartTls,
            Username = "bookspace.sender@gmail.com", Password = password,
        },
    };

    private static (SmtpNotificationSender Sender, FakeSmtpTransportFactory TransportFactory, CapturingLogger<SmtpNotificationSender> Logger) CreateSender(
        EmailOptions options, EmailRecipient? recipient = null, Func<ISmtpTransport>? transportCreator = null, FakeBookingReminderEligibilityChecker? reminderChecker = null)
    {
        var transportFactory = new FakeSmtpTransportFactory(transportCreator ?? (() => new FakeSmtpTransport()));
        var logger = new CapturingLogger<SmtpNotificationSender>();
        var sender = new SmtpNotificationSender(
            Microsoft.Extensions.Options.Options.Create(options),
            new FakeEmailRecipientResolver(recipient ?? SampleRecipient),
            new BookingEmailTemplateRenderer(),
            reminderChecker ?? new FakeBookingReminderEligibilityChecker(),
            transportFactory,
            logger);
        return (sender, transportFactory, logger);
    }

    [Fact]
    public async Task SendAsync_WithAValidConfigurationAndRecipient_SendsSuccessfullyAndDisposesTheTransport()
    {
        var (sender, transportFactory, _) = CreateSender(EnabledOptions());
        var message = BuildMessage();

        var result = await sender.SendAsync(message, CancellationToken.None);

        Assert.Equal(NotificationSendOutcome.Success, result.Outcome);
        var transport = Assert.IsType<FakeSmtpTransport>(Assert.Single(transportFactory.CreatedTransports));
        Assert.True(transport.Connected);
        Assert.True(transport.Authenticated);
        Assert.True(transport.Disconnected);
        Assert.True(transport.Disposed);
        Assert.NotNull(transport.SentMessage);
    }

    [Fact]
    public async Task SendAsync_UsesFromAddressFromNameAndRecipientAddress()
    {
        var (sender, transportFactory, _) = CreateSender(EnabledOptions());
        var message = BuildMessage();

        await sender.SendAsync(message, CancellationToken.None);

        var sent = ((FakeSmtpTransport)transportFactory.CreatedTransports.Single()).SentMessage!;
        Assert.Equal("BookSpace", sent.From.Mailboxes.Single().Name);
        Assert.Equal("bookspace.sender@gmail.com", sent.From.Mailboxes.Single().Address);
        Assert.Equal(SampleRecipient.Email, sent.To.Mailboxes.Single().Address);
        Assert.Equal(SampleRecipient.DisplayName, sent.To.Mailboxes.Single().Name);
    }

    [Fact]
    public async Task SendAsync_SetsAStableMessageIdAndOutboxIdHeaderDerivedFromTheOutboxItemId()
    {
        var outboxItemId = Guid.NewGuid();
        var (sender, transportFactory, _) = CreateSender(EnabledOptions());
        var message = BuildMessage(outboxItemId: outboxItemId);

        await sender.SendAsync(message, CancellationToken.None);

        var sent = ((FakeSmtpTransport)transportFactory.CreatedTransports.Single()).SentMessage!;
        Assert.Equal($"bookspace-outbox-{outboxItemId:D}@bookspace.local", sent.MessageId);
        Assert.Equal(outboxItemId.ToString(), sent.Headers["X-BookSpace-Outbox-Id"]);
    }

    [Fact]
    public async Task SendAsync_CalledTwiceForTheSameOutboxItem_ProducesTheSameMessageIdAndContent()
    {
        var outboxItemId = Guid.NewGuid();
        var payloadJson = JsonSerializer.Serialize(SamplePayload);
        var (sender, transportFactory, _) = CreateSender(EnabledOptions());

        await sender.SendAsync(BuildMessage(outboxItemId, payloadJson: payloadJson), CancellationToken.None);
        await sender.SendAsync(BuildMessage(outboxItemId, payloadJson: payloadJson), CancellationToken.None);

        var sentMessages = transportFactory.CreatedTransports.Cast<FakeSmtpTransport>().Select(t => t.SentMessage!).ToList();
        Assert.Equal(sentMessages[0].MessageId, sentMessages[1].MessageId);
        Assert.Equal(sentMessages[0].Headers["X-BookSpace-Outbox-Id"], sentMessages[1].Headers["X-BookSpace-Outbox-Id"]);
        Assert.Equal(sentMessages[0].Subject, sentMessages[1].Subject);
        Assert.Equal(sentMessages[0].HtmlBody, sentMessages[1].HtmlBody);
        Assert.Equal(sentMessages[0].TextBody, sentMessages[1].TextBody);
    }

    [Fact]
    public async Task SendAsync_WhenEmailIsDisabled_ReturnsPermanentFailureWithoutCreatingATransport()
    {
        var disabledOptions = new EmailOptions { Enabled = false, FromAddress = "bookspace.sender@gmail.com" };
        var sender = new SmtpNotificationSender(
            Microsoft.Extensions.Options.Options.Create(disabledOptions),
            new FakeEmailRecipientResolver(SampleRecipient),
            new BookingEmailTemplateRenderer(),
            new FakeBookingReminderEligibilityChecker(),
            new ThrowingSmtpTransportFactory(),
            new CapturingLogger<SmtpNotificationSender>());

        var result = await sender.SendAsync(BuildMessage(), CancellationToken.None);

        Assert.Equal(NotificationSendOutcome.PermanentFailure, result.Outcome);
    }

    [Fact]
    public async Task SendAsync_WhenRecipientIsMissing_ReturnsPermanentFailureWithoutCreatingATransport()
    {
        var sender = new SmtpNotificationSender(
            Microsoft.Extensions.Options.Options.Create(EnabledOptions()),
            new FakeEmailRecipientResolver(null),
            new BookingEmailTemplateRenderer(),
            new FakeBookingReminderEligibilityChecker(),
            new ThrowingSmtpTransportFactory(),
            new CapturingLogger<SmtpNotificationSender>());

        var result = await sender.SendAsync(BuildMessage(), CancellationToken.None);

        Assert.Equal(NotificationSendOutcome.PermanentFailure, result.Outcome);
    }

    [Fact]
    public async Task SendAsync_WithMalformedPayload_ReturnsPermanentFailureWithoutCreatingATransport()
    {
        var sender = new SmtpNotificationSender(
            Microsoft.Extensions.Options.Options.Create(EnabledOptions()),
            new FakeEmailRecipientResolver(SampleRecipient),
            new BookingEmailTemplateRenderer(),
            new FakeBookingReminderEligibilityChecker(),
            new ThrowingSmtpTransportFactory(),
            new CapturingLogger<SmtpNotificationSender>());

        var result = await sender.SendAsync(BuildMessage(payloadJson: "{not-valid"), CancellationToken.None);

        Assert.Equal(NotificationSendOutcome.PermanentFailure, result.Outcome);
    }

    [Fact]
    public async Task SendAsync_WithAnUnknownNotificationType_ReturnsPermanentFailureWithoutCreatingATransport()
    {
        var sender = new SmtpNotificationSender(
            Microsoft.Extensions.Options.Options.Create(EnabledOptions()),
            new FakeEmailRecipientResolver(SampleRecipient),
            new BookingEmailTemplateRenderer(),
            new FakeBookingReminderEligibilityChecker(),
            new ThrowingSmtpTransportFactory(),
            new CapturingLogger<SmtpNotificationSender>());

        var result = await sender.SendAsync(BuildMessage(notificationType: "Booking.SomethingElse"), CancellationToken.None);

        Assert.Equal(NotificationSendOutcome.PermanentFailure, result.Outcome);
    }

    [Fact]
    public async Task SendAsync_WhenSmtpReturnsA4xxResponse_ReturnsTransientFailure()
    {
        var failure = new SmtpCommandException(SmtpErrorCode.UnexpectedStatusCode, SmtpStatusCode.MailboxBusy, "Mailbox busy, try again later");
        var (sender, _, _) = CreateSender(EnabledOptions(), transportCreator: () => new FakeSmtpTransport(sendFailure: failure));

        var result = await sender.SendAsync(BuildMessage(), CancellationToken.None);

        Assert.Equal(NotificationSendOutcome.TransientFailure, result.Outcome);
    }

    [Fact]
    public async Task SendAsync_WhenSmtpReturnsA5xxResponse_ReturnsPermanentFailure()
    {
        var failure = new SmtpCommandException(SmtpErrorCode.UnexpectedStatusCode, SmtpStatusCode.MailboxUnavailable, "Mailbox unavailable");
        var (sender, _, _) = CreateSender(EnabledOptions(), transportCreator: () => new FakeSmtpTransport(sendFailure: failure));

        var result = await sender.SendAsync(BuildMessage(), CancellationToken.None);

        Assert.Equal(NotificationSendOutcome.PermanentFailure, result.Outcome);
    }

    [Fact]
    public async Task SendAsync_WhenAuthenticationFails_ReturnsPermanentFailure()
    {
        var (sender, _, _) = CreateSender(
            EnabledOptions(), transportCreator: () => new FakeSmtpTransport(authenticateFailure: new AuthenticationException("Authentication failed.")));

        var result = await sender.SendAsync(BuildMessage(), CancellationToken.None);

        Assert.Equal(NotificationSendOutcome.PermanentFailure, result.Outcome);
    }

    [Fact]
    public async Task SendAsync_WhenANetworkFailureOccurs_ReturnsTransientFailure()
    {
        var (sender, _, _) = CreateSender(
            EnabledOptions(), transportCreator: () => new FakeSmtpTransport(connectFailure: new SocketException()));

        var result = await sender.SendAsync(BuildMessage(), CancellationToken.None);

        Assert.Equal(NotificationSendOutcome.TransientFailure, result.Outcome);
    }

    [Fact]
    public async Task SendAsync_WhenTheCallerCancels_PropagatesOperationCanceledExceptionRatherThanAFailureResult()
    {
        var (sender, _, _) = CreateSender(EnabledOptions());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => sender.SendAsync(BuildMessage(), cts.Token));
    }

    [Fact]
    public async Task SendAsync_OnATransportFailure_StillDisposesTheTransport()
    {
        var (sender, transportFactory, _) = CreateSender(
            EnabledOptions(), transportCreator: () => new FakeSmtpTransport(sendFailure: new IOException("connection reset")));

        await sender.SendAsync(BuildMessage(), CancellationToken.None);

        var transport = (FakeSmtpTransport)transportFactory.CreatedTransports.Single();
        Assert.True(transport.Disposed);
    }

    // --- Reminder-only cancellation-race guard (docs/background-jobs.md, "Cancellation race for
    // reminders") - the other three notification types never consult IBookingReminderEligibilityChecker
    // at all (proven below), since each of them is only ever enqueued once its event already happened. ---

    [Fact]
    public async Task SendAsync_ForAReminder_ChecksEligibilityWithTheMessagesTenantIdAndThePayloadsBookingId()
    {
        var checker = new FakeBookingReminderEligibilityChecker(isEligible: true);
        var (sender, _, _) = CreateSender(EnabledOptions(), reminderChecker: checker);
        var message = new NotificationMessage(Guid.NewGuid(), Guid.NewGuid(), BookingNotificationTypes.Reminder, Guid.NewGuid(), JsonSerializer.Serialize(SamplePayload));

        await sender.SendAsync(message, CancellationToken.None);

        Assert.Equal(1, checker.CallCount);
        Assert.Equal(message.TenantId, checker.LastTenantId);
        Assert.Equal(SamplePayload.BookingId, checker.LastBookingId);
    }

    [Fact]
    public async Task SendAsync_ForAReminderThatIsStillEligible_SendsNormally()
    {
        var checker = new FakeBookingReminderEligibilityChecker(isEligible: true);
        var (sender, transportFactory, _) = CreateSender(EnabledOptions(), reminderChecker: checker);
        var message = new NotificationMessage(Guid.NewGuid(), Guid.NewGuid(), BookingNotificationTypes.Reminder, Guid.NewGuid(), JsonSerializer.Serialize(SamplePayload));

        var result = await sender.SendAsync(message, CancellationToken.None);

        Assert.Equal(NotificationSendOutcome.Success, result.Outcome);
        Assert.Single(transportFactory.CreatedTransports);
    }

    [Fact]
    public async Task SendAsync_ForAReminderWhoseBookingIsNoLongerEligible_ReturnsSuppressedWithoutEverCreatingATransport()
    {
        var checker = new FakeBookingReminderEligibilityChecker(isEligible: false);
        var sender = new SmtpNotificationSender(
            Microsoft.Extensions.Options.Options.Create(EnabledOptions()),
            new FakeEmailRecipientResolver(SampleRecipient),
            new BookingEmailTemplateRenderer(),
            checker,
            new ThrowingSmtpTransportFactory(), // proves no transport is ever even requested
            new CapturingLogger<SmtpNotificationSender>());
        var message = new NotificationMessage(Guid.NewGuid(), Guid.NewGuid(), BookingNotificationTypes.Reminder, Guid.NewGuid(), JsonSerializer.Serialize(SamplePayload));

        var result = await sender.SendAsync(message, CancellationToken.None);

        Assert.Equal(NotificationSendOutcome.Suppressed, result.Outcome);
        Assert.Equal(1, checker.CallCount);
    }

    [Fact]
    public async Task SendAsync_ForANonReminderType_NeverConsultsTheReminderEligibilityChecker()
    {
        var checker = new FakeBookingReminderEligibilityChecker(isEligible: false); // would suppress a reminder - must be irrelevant here
        var (sender, _, _) = CreateSender(EnabledOptions(), reminderChecker: checker);

        var result = await sender.SendAsync(BuildMessage(notificationType: BookingNotificationTypes.Confirmation), CancellationToken.None);

        Assert.Equal(NotificationSendOutcome.Success, result.Outcome);
        Assert.Equal(0, checker.CallCount);
    }

    [Fact]
    public async Task SendAsync_ForAReminderWithAMalformedPayload_ReturnsPermanentWithoutConsultingEligibility()
    {
        var checker = new FakeBookingReminderEligibilityChecker();
        var sender = new SmtpNotificationSender(
            Microsoft.Extensions.Options.Options.Create(EnabledOptions()),
            new FakeEmailRecipientResolver(SampleRecipient),
            new BookingEmailTemplateRenderer(),
            checker,
            new ThrowingSmtpTransportFactory(),
            new CapturingLogger<SmtpNotificationSender>());

        var result = await sender.SendAsync(BuildMessage(notificationType: BookingNotificationTypes.Reminder, payloadJson: "{not-valid"), CancellationToken.None);

        Assert.Equal(NotificationSendOutcome.PermanentFailure, result.Outcome);
        Assert.Equal(0, checker.CallCount); // template rendering already failed first - the payload never even reaches this check
    }

    [Fact]
    public async Task SendAsync_NeverLogsThePasswordOrTheRecipientEmailAddress_OnSuccessOrFailure()
    {
        const string password = "a-gmail-app-password-value";
        var (sender, _, logger) = CreateSender(EnabledOptions(password));
        await sender.SendAsync(BuildMessage(), CancellationToken.None);

        var (failingSender, _, failingLogger) = CreateSender(
            EnabledOptions(password), transportCreator: () => new FakeSmtpTransport(sendFailure: new SmtpCommandException(SmtpErrorCode.UnexpectedStatusCode, SmtpStatusCode.MailboxUnavailable, "rejected")));
        await failingSender.SendAsync(BuildMessage(), CancellationToken.None);

        foreach (var line in logger.Messages.Concat(failingLogger.Messages))
        {
            Assert.DoesNotContain(password, line);
            Assert.DoesNotContain(SampleRecipient.Email, line);
        }
    }
}
