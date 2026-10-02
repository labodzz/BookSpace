namespace BookSpace.Application.Notifications;

// Exactly what a sender needs to actually deliver one notification - deliberately NOT the full
// NotificationOutboxItem/DueNotificationOutboxItem (no Id-as-outbox-row-identity confusion, no
// IdempotencyKey a sender has no business comparing). OutboxItemId is included only so a future real
// sender can pass it through as the provider's own idempotency key where supported - see
// docs/background-jobs.md ("Exactly-once limitation").
public sealed record NotificationMessage(Guid OutboxItemId, string NotificationType, Guid RecipientUserId, string PayloadJson);

public enum NotificationSendOutcome
{
    Success,

    // Retrying later is expected to help (the provider was briefly down, a timeout, rate limiting, a
    // transient network error).
    TransientFailure,

    // Retrying is not expected to help (an invalid recipient, an unrecognized notification type, a
    // malformed payload, or the provider explicitly rejecting the request as unrecoverable).
    PermanentFailure,
}

// ErrorMessage is a short, sanitized summary a sender implementation constructs deliberately - never a
// raw exception's message/stack trace, never a provider credential or the full provider response. It is
// stored, truncated, as NotificationOutboxItem.LastError - see docs/background-jobs.md ("LastError").
public sealed record NotificationSendResult(NotificationSendOutcome Outcome, string? ErrorMessage = null)
{
    public static NotificationSendResult Success() => new(NotificationSendOutcome.Success);
    public static NotificationSendResult Transient(string errorMessage) => new(NotificationSendOutcome.TransientFailure, errorMessage);
    public static NotificationSendResult Permanent(string errorMessage) => new(NotificationSendOutcome.PermanentFailure, errorMessage);
}

// One notification delivery operation - see docs/background-jobs.md ("Per-item processing") for how
// NotificationOutboxProcessor uses this. No production implementation exists yet (see
// docs/background-jobs.md, "Why this is not wired into the worker yet" - repeated here for the processor
// task): a real provider-backed sender is later WP-8 work. Implementations must:
//   - return Success/TransientFailure/PermanentFailure via the typed result - never throw for an
//     ordinary delivery failure the caller needs to classify and act on.
//   - only ever throw OperationCanceledException, and only for genuine cancellation (the token this
//     method was given being cancelled) - never to represent a delivery outcome.
//   - never log or include recipient email, full payload, or provider credentials in ErrorMessage.
public interface INotificationSender
{
    Task<NotificationSendResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken);
}
