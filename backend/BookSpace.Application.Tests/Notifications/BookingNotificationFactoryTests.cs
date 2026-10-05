using System.Text.Json;
using BookSpace.Application.Notifications;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Xunit;

namespace BookSpace.Application.Tests.Notifications;

public sealed class BookingNotificationFactoryTests
{
    private static Booking CreateBooking() => new()
    {
        Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceId = Guid.NewGuid(), UserId = Guid.NewGuid(),
        StartUtc = DateTimeOffset.UtcNow.AddHours(1), EndUtc = DateTimeOffset.UtcNow.AddHours(2),
        Quantity = 2, Status = BookingStatus.Confirmed, CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Confirmation_BuildsARequestWithTheBookingsOwnTenantAndOwnerAsRecipient()
    {
        var booking = CreateBooking();

        var request = BookingNotificationFactory.Confirmation(booking);

        Assert.Equal(booking.TenantId, request.TenantId);
        Assert.Equal(booking.UserId, request.RecipientUserId);
        Assert.Equal(BookingNotificationTypes.Confirmation, request.NotificationType);
    }

    [Theory]
    [InlineData("confirmation")]
    [InlineData("rejection")]
    [InlineData("cancellation")]
    public void EachNotificationType_UsesTheDocumentedIdempotencyKeyFormat(string suffix)
    {
        var booking = CreateBooking();

        var request = suffix switch
        {
            "confirmation" => BookingNotificationFactory.Confirmation(booking),
            "rejection" => BookingNotificationFactory.Rejection(booking),
            _ => BookingNotificationFactory.Cancellation(booking),
        };

        Assert.Equal($"booking:{booking.Id}:{suffix}", request.IdempotencyKey);
        // Well under NotificationOutboxItem.MaxIdempotencyKeyLength (200) - a GUID-based key never gets
        // anywhere close, but this guards against a future format change silently growing past it.
        Assert.True(request.IdempotencyKey.Length < NotificationOutboxItem.MaxIdempotencyKeyLength);
    }

    [Fact]
    public void CalledTwiceForTheSameBookingAndType_ProducesTheIdenticalIdempotencyKey()
    {
        // The determinism a retried command/job cycle relies on: the SAME logical notification always
        // computes the SAME key, so the database's unique constraint (not this factory) is what actually
        // prevents a duplicate row - see docs/background-jobs.md ("Notification outbox").
        var booking = CreateBooking();

        var first = BookingNotificationFactory.Confirmation(booking);
        var second = BookingNotificationFactory.Confirmation(booking);

        Assert.Equal(first.IdempotencyKey, second.IdempotencyKey);
    }

    [Fact]
    public void OneBooking_CanHaveDistinctKeysForDifferentNotificationTypes()
    {
        var booking = CreateBooking();

        var confirmation = BookingNotificationFactory.Confirmation(booking);
        var rejection = BookingNotificationFactory.Rejection(booking);
        var cancellation = BookingNotificationFactory.Cancellation(booking);

        var keys = new[] { confirmation.IdempotencyKey, rejection.IdempotencyKey, cancellation.IdempotencyKey };
        Assert.Equal(keys.Length, keys.Distinct().Count());
    }

    [Fact]
    public void IdempotencyKey_NeverContainsARandomGuidOtherThanTheBookingsOwnId()
    {
        // A fresh random Guid baked into the key (e.g. Guid.NewGuid()) would silently break idempotency -
        // every call would mint a new key and the unique constraint could never catch a duplicate. The
        // only Guid allowed in the key is the booking's own, stable id.
        var booking = CreateBooking();

        var request = BookingNotificationFactory.Confirmation(booking);

        var withoutBookingId = request.IdempotencyKey.Replace(booking.Id.ToString(), string.Empty);
        Assert.DoesNotContain("-", withoutBookingId); // no other GUID-shaped fragment remains
    }

    // --- Reminder: builds from a DueBookingReminder projection, not a tracked Booking entity - see
    // BookingNotificationFactory's own doc comment for why. ---

    private static DueBookingReminder CreateReminderCandidate() => new(
        BookingId: Guid.NewGuid(), TenantId: Guid.NewGuid(), UserId: Guid.NewGuid(), ResourceId: Guid.NewGuid(),
        StartUtc: DateTimeOffset.UtcNow.AddHours(1), EndUtc: DateTimeOffset.UtcNow.AddHours(2), Quantity: 2);

    [Fact]
    public void Reminder_BuildsARequestWithTheCandidatesOwnTenantAndOwnerAsRecipient()
    {
        var candidate = CreateReminderCandidate();

        var request = BookingNotificationFactory.Reminder(candidate);

        Assert.Equal(candidate.TenantId, request.TenantId);
        Assert.Equal(candidate.UserId, request.RecipientUserId);
        Assert.Equal(BookingNotificationTypes.Reminder, request.NotificationType);
    }

    [Fact]
    public void Reminder_UsesTheDocumentedIdempotencyKeyFormat()
    {
        var candidate = CreateReminderCandidate();

        var request = BookingNotificationFactory.Reminder(candidate);

        Assert.Equal($"booking:{candidate.BookingId}:reminder", request.IdempotencyKey);
        Assert.True(request.IdempotencyKey.Length < NotificationOutboxItem.MaxIdempotencyKeyLength);
    }

    [Fact]
    public void Reminder_CalledTwiceForTheSameBooking_ProducesTheIdenticalIdempotencyKey()
    {
        var candidate = CreateReminderCandidate();

        var first = BookingNotificationFactory.Reminder(candidate);
        var second = BookingNotificationFactory.Reminder(candidate);

        Assert.Equal(first.IdempotencyKey, second.IdempotencyKey);
    }

    [Fact]
    public void Reminder_KeyNeverCollidesWithTheSameBookingsOtherNotificationTypes()
    {
        var booking = CreateBooking();
        var reminderCandidate = new DueBookingReminder(booking.Id, booking.TenantId, booking.UserId, booking.ResourceId, booking.StartUtc, booking.EndUtc, booking.Quantity);

        var confirmation = BookingNotificationFactory.Confirmation(booking);
        var reminder = BookingNotificationFactory.Reminder(reminderCandidate);

        Assert.NotEqual(confirmation.IdempotencyKey, reminder.IdempotencyKey);
    }

    [Fact]
    public void Reminder_PayloadContainsOnlyTheFieldsAFutureSenderNeedsAndNoSensitiveData()
    {
        var candidate = CreateReminderCandidate();

        var request = BookingNotificationFactory.Reminder(candidate);
        var payload = JsonSerializer.Deserialize<BookingNotificationPayload>(request.PayloadJson);

        Assert.NotNull(payload);
        Assert.Equal(candidate.BookingId, payload!.BookingId);
        Assert.Equal(candidate.ResourceId, payload.ResourceId);
        Assert.Equal(candidate.StartUtc, payload.StartUtc);
        Assert.Equal(candidate.EndUtc, payload.EndUtc);
        Assert.Equal(candidate.Quantity, payload.Quantity);
        Assert.DoesNotContain("token", request.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", request.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Payload_ContainsOnlyTheFieldsAFutureSenderNeedsAndNoSensitiveData()
    {
        var booking = CreateBooking();

        var request = BookingNotificationFactory.Confirmation(booking);
        var payload = JsonSerializer.Deserialize<BookingNotificationPayload>(request.PayloadJson);

        Assert.NotNull(payload);
        Assert.Equal(booking.Id, payload!.BookingId);
        Assert.Equal(booking.ResourceId, payload.ResourceId);
        Assert.Equal(booking.StartUtc, payload.StartUtc);
        Assert.Equal(booking.EndUtc, payload.EndUtc);
        Assert.Equal(booking.Quantity, payload.Quantity);
        // Never the recipient's email, a token, or a secret - RecipientUserId lives on the outbox request
        // itself, never inside the opaque payload.
        Assert.DoesNotContain("token", request.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", request.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }

    // --- ApprovalExpired: builds from a tracked Booking plus the ApprovalRequest id that just expired -
    // see BookingNotificationFactory's own doc comment for why the idempotency key stays in the
    // "booking:{bookingId}:{type}" family rather than an "approval:{id}:expired" key. ---

    [Fact]
    public void ApprovalExpired_BuildsARequestWithTheBookingsOwnTenantAndOwnerAsRecipient()
    {
        var booking = CreateBooking();

        var request = BookingNotificationFactory.ApprovalExpired(booking, Guid.NewGuid());

        Assert.Equal(booking.TenantId, request.TenantId);
        Assert.Equal(booking.UserId, request.RecipientUserId);
        Assert.Equal(BookingNotificationTypes.ApprovalExpired, request.NotificationType);
    }

    [Fact]
    public void ApprovalExpired_UsesTheDocumentedIdempotencyKeyFormat()
    {
        var booking = CreateBooking();

        var request = BookingNotificationFactory.ApprovalExpired(booking, Guid.NewGuid());

        Assert.Equal($"booking:{booking.Id}:approvalexpired", request.IdempotencyKey);
        Assert.True(request.IdempotencyKey.Length < NotificationOutboxItem.MaxIdempotencyKeyLength);
    }

    [Fact]
    public void ApprovalExpired_CalledTwiceForTheSameBookingAndApprovalRequest_ProducesTheIdenticalIdempotencyKey()
    {
        var booking = CreateBooking();
        var approvalRequestId = Guid.NewGuid();

        var first = BookingNotificationFactory.ApprovalExpired(booking, approvalRequestId);
        var second = BookingNotificationFactory.ApprovalExpired(booking, approvalRequestId);

        Assert.Equal(first.IdempotencyKey, second.IdempotencyKey);
    }

    [Fact]
    public void ApprovalExpired_KeyNeverCollidesWithTheSameBookingsOtherNotificationTypes()
    {
        var booking = CreateBooking();

        var confirmation = BookingNotificationFactory.Confirmation(booking);
        var approvalExpired = BookingNotificationFactory.ApprovalExpired(booking, Guid.NewGuid());

        Assert.NotEqual(confirmation.IdempotencyKey, approvalExpired.IdempotencyKey);
    }

    [Fact]
    public void ApprovalExpired_PayloadContainsOnlyTheFieldsAFutureSenderNeedsAndNoSensitiveData()
    {
        var booking = CreateBooking();
        var approvalRequestId = Guid.NewGuid();

        var request = BookingNotificationFactory.ApprovalExpired(booking, approvalRequestId);
        var payload = JsonSerializer.Deserialize<ApprovalExpiredNotificationPayload>(request.PayloadJson);

        Assert.NotNull(payload);
        Assert.Equal(approvalRequestId, payload!.ApprovalRequestId);
        Assert.Equal(booking.Id, payload.BookingId);
        Assert.Equal(booking.ResourceId, payload.ResourceId);
        Assert.Equal(booking.StartUtc, payload.StartUtc);
        Assert.Equal(booking.EndUtc, payload.EndUtc);
        Assert.Equal(booking.Quantity, payload.Quantity);
        Assert.DoesNotContain("token", request.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", request.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }
}
