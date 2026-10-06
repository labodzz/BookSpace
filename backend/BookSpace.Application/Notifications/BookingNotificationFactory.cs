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
    public const string Reminder = "Booking.Reminder";
    public const string ApprovalExpired = "Booking.ApprovalExpired";
    public const string NoShow = "Booking.NoShow";
}

// Exactly what a future sender/template needs to render a booking lifecycle notification - booking id,
// which resource, and the time window/quantity. Deliberately nothing else: no resource name (the sender
// can resolve that from ResourceId when it actually renders), no recipient email (RecipientUserId on the
// outbox row itself is how a future sender resolves the live address at send time - see
// NotificationOutboxItem's own doc comment), no tokens, no secrets. Dates stay DateTimeOffset (UTC), the
// same representation Booking itself already uses.
public sealed record BookingNotificationPayload(Guid BookingId, Guid ResourceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Quantity);

// Booking.ApprovalExpired's own payload, not BookingNotificationPayload: it needs one extra field
// (ApprovalRequestId, for traceability/audit - which specific approval request this notification is
// about) that no other booking lifecycle notification needs. Same rule as BookingNotificationPayload on
// what's deliberately absent: no recipient email, no token, no secret.
public sealed record ApprovalExpiredNotificationPayload(
    Guid ApprovalRequestId, Guid BookingId, Guid ResourceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Quantity);

// Builds the NotificationOutboxRequest for one booking lifecycle event, so every command handler that
// enqueues one (confirmation on create or on approval, rejection, cancellation) computes the same
// IdempotencyKey format and the same payload shape - see docs/background-jobs.md ("Booking lifecycle
// notifications") for why the key is "booking:{bookingId}:{type}" and why that is retry-safe.
public static class BookingNotificationFactory
{
    public static NotificationOutboxRequest Confirmation(Booking booking) => Build(booking, BookingNotificationTypes.Confirmation, "confirmation");

    public static NotificationOutboxRequest Rejection(Booking booking) => Build(booking, BookingNotificationTypes.Rejection, "rejection");

    public static NotificationOutboxRequest Cancellation(Booking booking) => Build(booking, BookingNotificationTypes.Cancellation, "cancellation");

    // Takes a tracked Booking (already re-read and transitioned to NoShow under the resource lock by
    // NoShowProcessor). Same BookingNotificationPayload shape as Confirmation/Rejection/Cancellation - no
    // extra field is needed (unlike ApprovalExpired's ApprovalRequestId), since a no-show is purely a fact
    // about the booking itself, not about some other entity.
    public static NotificationOutboxRequest NoShow(Booking booking) => Build(booking, BookingNotificationTypes.NoShow, "noshow");

    // Takes a DueBookingReminder projection, not a tracked Booking entity: IBookingReminderReader's
    // system-wide due query (see docs/background-jobs.md, "Reminder job") is a narrow, untracked
    // Select(...), never a full entity load - this overload exists so the reminder producer never needs
    // to load a full Booking just to build the same request shape the other three overloads build.
    public static NotificationOutboxRequest Reminder(DueBookingReminder booking)
    {
        var payload = new BookingNotificationPayload(booking.BookingId, booking.ResourceId, booking.StartUtc, booking.EndUtc, booking.Quantity);
        return new NotificationOutboxRequest(
            booking.TenantId,
            BookingNotificationTypes.Reminder,
            booking.UserId,
            JsonSerializer.Serialize(payload),
            $"booking:{booking.BookingId}:reminder");
    }

    // Takes a tracked Booking (already re-read and transitioned to Expired under the resource lock by
    // StaleApprovalProcessor) plus the ApprovalRequest id that just expired. Idempotency key deliberately
    // stays in the SAME "booking:{bookingId}:{type}" family every other booking notification uses (see
    // docs/background-jobs.md, "Stale-approval job - notification") rather than an "approval:{id}:expired"
    // key - this notification is fundamentally about the booking (same recipient, same payload shape as
    // the other three booking lifecycle notifications), so a separate key family would be an inconsistency,
    // not a improvement.
    public static NotificationOutboxRequest ApprovalExpired(Booking booking, Guid approvalRequestId)
    {
        var payload = new ApprovalExpiredNotificationPayload(approvalRequestId, booking.Id, booking.ResourceId, booking.StartUtc, booking.EndUtc, booking.Quantity);
        return new NotificationOutboxRequest(
            booking.TenantId,
            BookingNotificationTypes.ApprovalExpired,
            booking.UserId,
            JsonSerializer.Serialize(payload),
            $"booking:{booking.Id}:approvalexpired");
    }

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
