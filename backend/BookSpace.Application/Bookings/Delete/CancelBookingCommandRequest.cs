using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Application.Security;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Bookings;

public sealed record CancelBookingCommandRequest(Guid Id) : IRequest<CancelBookingResponse>;

public sealed record CancelBookingResponse(
    Guid Id, Guid ResourceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Quantity, BookingStatus Status);

// Cancellation never needs IResourceBookingLock: removing an active booking can only reduce summed
// demand, which can never turn a valid capacity state into an invalid one, so an ordinary load-mutate-
// save is sufficient - see docs/bookings-and-concurrency.md.
public sealed class CancelBookingCommandHandler(IBookingRepository bookingRepository, ICurrentUserContext currentUserContext)
    : IRequestHandler<CancelBookingCommandRequest, CancelBookingResponse>
{
    private static readonly BookingStatus[] CancellableStatuses = [BookingStatus.Pending, BookingStatus.Confirmed];

    public async Task<CancelBookingResponse> Handle(CancelBookingCommandRequest request, CancellationToken cancellationToken)
    {
        var booking = await bookingRepository.FindByIdAsync(request.Id, cancellationToken);

        // Not-found and "belongs to a different user" look identical to the caller - never confirm
        // another user's booking exists via a different error shape (same philosophy already applied to
        // cross-tenant lookups elsewhere: NotFoundException, not a 403, so nothing about someone else's
        // data is leaked by the response shape).
        if (booking is null || booking.UserId != currentUserContext.UserId)
        {
            throw new NotFoundException($"Booking {request.Id} was not found.", ErrorCodes.BookingNotFound);
        }

        if (booking.Status != BookingStatus.Cancelled)
        {
            if (!CancellableStatuses.Contains(booking.Status))
            {
                throw new ConflictException(
                    $"Booking {booking.Id} cannot be cancelled from status {booking.Status}.", ErrorCodes.BookingCancellationNotAllowed);
            }

            booking.Status = BookingStatus.Cancelled;
            booking.CancelledAtUtc = DateTimeOffset.UtcNow;
            booking.CancelledByUserId = currentUserContext.UserId;
            await bookingRepository.SaveChangesAsync(cancellationToken);
        }

        return new CancelBookingResponse(booking.Id, booking.ResourceId, booking.StartUtc, booking.EndUtc, booking.Quantity, booking.Status);
    }
}
