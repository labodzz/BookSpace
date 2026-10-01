namespace BookSpace.Application.Notifications;

// TenantId is an explicit field, not read from ICurrentUserContext - unlike an ordinary web-request
// handler, a future background job enqueuing on behalf of many different tenants' bookings in one cycle
// has no single ambient tenant context to read. The caller always derives it from the business entity
// the notification is about (e.g. booking.TenantId), never from a client-suppliable value.
//
// AvailableAtUtc is optional - null means "available immediately" (AvailableAtUtc = CreatedAtUtc).
// A future scheduled notification (e.g. a reminder fired some lead time before a booking starts) would
// pass a future instant instead.
public sealed record NotificationOutboxRequest(
    Guid TenantId,
    string NotificationType,
    Guid RecipientUserId,
    string PayloadJson,
    string IdempotencyKey,
    DateTimeOffset? AvailableAtUtc = null);

public enum EnqueueOutcome
{
    // A new outbox row was persisted for this IdempotencyKey.
    Created,

    // A row for this exact (TenantId, IdempotencyKey) already existed - not an error. OutboxItemId is
    // null in this case: the writer does not read back and return the existing row's id, since nothing
    // in this task needs it (the caller already knows the logical notification was already recorded).
    AlreadyExists,
}

public sealed record EnqueueNotificationResult(EnqueueOutcome Outcome, Guid? OutboxItemId);

// Idempotently records that one logical notification should eventually be sent - see
// docs/background-jobs.md ("Notification outbox") for the full design, including why this task
// guarantees "exactly one durable row per IdempotencyKey," not "exactly-once email delivery." Does not
// send anything itself; a later WP-8 task reads rows this writer created and actually sends email.
public interface INotificationOutboxWriter
{
    // Never throws for the ordinary "this exact notification was already enqueued" outcome - that is the
    // whole point of idempotency, and callers (eventually, business command handlers) must not have to
    // catch an exception for a case that isn't an error. Only a genuinely unexpected database failure, or
    // an invalid IdempotencyKey (a caller bug, not a runtime race), escapes as an exception - see the
    // implementation's own documentation for exactly which.
    Task<EnqueueNotificationResult> EnqueueAsync(NotificationOutboxRequest request, CancellationToken cancellationToken);
}
