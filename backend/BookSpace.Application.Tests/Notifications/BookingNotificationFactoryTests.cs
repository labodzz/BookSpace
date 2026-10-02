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
}
