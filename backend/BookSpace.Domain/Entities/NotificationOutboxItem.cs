using BookSpace.Domain.Common;
using BookSpace.Domain.Enums;

namespace BookSpace.Domain.Entities;

// A durable record that one logical notification was decided upon - see docs/background-jobs.md
// ("Notification outbox") for the full design. This task only persists the row exactly once per
// IdempotencyKey; it does not send anything. ITenantOwned because a notification always belongs to a
// specific tenant's business event (a booking), unlike JobLease which is a global infrastructure lock -
// see the "Tenant behavior" section of the docs for how a future cross-tenant background processor reads
// this table safely despite the global tenant query filter.
public sealed class NotificationOutboxItem : ITenantOwned
{
    // Kept in one place so BookSpaceModelConfiguration's column length and
    // NotificationOutboxWriter's validation can never drift apart.
    public const int MaxIdempotencyKeyLength = 200;

    public Guid Id { get; set; }
    public Guid TenantId { get; set; }

    // A short, stable code for what kind of notification this is (e.g. "Booking.Confirmation") - plain
    // string, not an enum, so a future notification type never needs a Domain-layer schema change to
    // introduce. Not used for any idempotency/uniqueness decision - IdempotencyKey alone is.
    public string NotificationType { get; set; } = string.Empty;

    // A stable reference to the recipient (not a copy of their current email) - the eventual sender
    // resolves the live email at send time, so a later profile change is never sent to a stale address
    // captured here.
    public Guid RecipientUserId { get; set; }

    // Whatever a future email template needs to render this notification (e.g. booking id, time window,
    // resource name) - opaque JSON here; the Domain/Infrastructure layers never parse it. Never log this
    // field's content - see docs/background-jobs.md.
    public string PayloadJson { get; set; } = string.Empty;

    // Identifies one specific logical notification (e.g. "booking:{bookingId}:confirmation") -
    // deterministic and stable across retries, scoped unique per-tenant (see
    // BookSpaceModelConfiguration and docs/background-jobs.md for why (TenantId, IdempotencyKey) rather
    // than a globally-unique key).
    public string IdempotencyKey { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }

    // When this item first becomes eligible for processing - defaults to CreatedAtUtc for an
    // immediately-due item, but also doubles as the retry backoff target a future retry processor pushes
    // forward after a failed attempt, so no second "NextAttemptAtUtc" column is needed.
    public DateTimeOffset AvailableAtUtc { get; set; }

    public NotificationOutboxStatus Status { get; set; } = NotificationOutboxStatus.Pending;

    // Incremented by a future retry processor on each processing attempt, successful or not - this task
    // never increments it (nothing processes items yet).
    public int AttemptCount { get; set; }

    public DateTimeOffset? LastAttemptAtUtc { get; set; }
}
