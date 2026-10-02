using System.Text.Json;
using BookSpace.Domain.Entities;

namespace BookSpace.Application.Notifications;

// Stable, short codes for NotificationOutboxItem.NotificationType - plain strings, not an enum, per that
// entity's own doc comment. Centralized here so every handler that enqueues a booking lifecycle
// notification uses the exact same code rather than re-typing a literal that could drift.
public static class BookingNotificationTypes
{
    public const string Confirmation = "Booking.Confirmation";
    public const string Rejection = "Booking.Rejection";
    public const string Cancellation = "Booking.Cancellation";
}

// Exactly what a future sender/template needs to render a booking lifecycle notification - booking id,
// which resource, and the time window/quantity. Deliberately nothing else: no resource name (the sender
// can resolve that from ResourceId when it actually renders), no recipient email (RecipientUserId on the
// outbox row itself is how a future sender resolves the live address at send time - see
// NotificationOutboxItem's own doc comment), no tokens, no secrets. Dates stay DateTimeOffset (UTC), the
// same representation Booking itself already uses.
public sealed record BookingNotificationPayload(Guid BookingId, Guid ResourceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Quantity);

// Builds the NotificationOutboxRequest for one booking lifecycle event, so every command handler that
// enqueues one (confirmation on create or on approval, rejection, cancellation) computes the same
// IdempotencyKey format and the same payload shape - see docs/background-jobs.md ("Booking lifecycle
// notifications") for why the key is "booking:{bookingId}:{type}" and why that is retry-safe.
public static class BookingNotificationFactory
{
    public static NotificationOutboxRequest Confirmation(Booking booking) => Build(booking, BookingNotificationTypes.Confirmation, "confirmation");

    public static NotificationOutboxRequest Rejection(Booking booking) => Build(booking, BookingNotificationTypes.Rejection, "rejection");

    public static NotificationOutboxRequest Cancellation(Booking booking) => Build(booking, BookingNotificationTypes.Cancellation, "cancellation");

    private static NotificationOutboxRequest Build(Booking booking, string notificationType, string idempotencyKeySuffix)
    {
        var payload = new BookingNotificationPayload(booking.Id, booking.ResourceId, booking.StartUtc, booking.EndUtc, booking.Quantity);

        // TenantId always comes from the booking itself, never from ICurrentUserContext - a handler calls
        // this after loading the booking through the ordinary tenant-filtered repository path, so
        // booking.TenantId IS the booking's real tenant; an approver/admin acting on it can only ever be
        // acting within that same tenant in the first place (a cross-tenant lookup never finds the row -
        // see docs/tenant-isolation.md), but asserting it here rather than reading the caller's own
        // tenant context keeps this correct even if that ever stopped being true for some future caller.
        return new NotificationOutboxRequest(
            booking.TenantId,
            notificationType,
            booking.UserId,
            JsonSerializer.Serialize(payload),
            $"booking:{booking.Id}:{idempotencyKeySuffix}");
    }
}
