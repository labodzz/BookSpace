using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Bookings;

// CancelRemainingSeries distinguishes cancelling ONLY this occurrence (default, false) from cancelling
// this occurrence AND every later still-cancellable occurrence in the same series (true) - see
// docs/recurring-bookings-and-approvals.md. Ignored (no error) for a booking with no SeriesId - a
// one-off booking has no "remaining series" to cascade into.
public sealed record CancelBookingCommandRequest(Guid Id, bool CancelRemainingSeries = false) : IRequest<CancelBookingResponse>;

// CascadedOccurrenceIds lists every OTHER occurrence also cancelled by this call (empty unless
// CancelRemainingSeries was true and the booking belongs to a series) - the client is always told exactly
// what else was affected, never left to infer it.
public sealed record CancelBookingResponse(
    Guid Id, Guid ResourceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Quantity, BookingStatus Status,
    IReadOnlyList<Guid> CascadedOccurrenceIds);

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

        var cascadedIds = new List<Guid>();

        if (booking.Status != BookingStatus.Cancelled)
        {
            if (!CancellableStatuses.Contains(booking.Status))
            {
                throw new ConflictException(
                    $"Booking {booking.Id} cannot be cancelled from status {booking.Status}.", ErrorCodes.BookingCancellationNotAllowed);
            }

            CancelOccurrence(booking);

            // Every OTHER occurrence in the same series that starts at or after this one, and is still
            // in a cancellable status - never occurrences that already ran their course (Completed/
            // NoShow), were already Rejected, or predate this one. A booking with no SeriesId (a one-off
            // booking) has nothing to cascade into, regardless of the flag.
            if (request.CancelRemainingSeries && booking.SeriesId is { } seriesId)
            {
                var seriesOccurrences = await bookingRepository.GetBySeriesIdAsync(seriesId, cancellationToken);
                foreach (var occurrence in seriesOccurrences)
                {
                    if (occurrence.Id == booking.Id || occurrence.StartUtc < booking.StartUtc || !CancellableStatuses.Contains(occurrence.Status))
                    {
                        continue;
                    }

                    CancelOccurrence(occurrence);
                    cascadedIds.Add(occurrence.Id);
                }
            }

            await bookingRepository.SaveChangesAsync(cancellationToken);
        }

        return new CancelBookingResponse(booking.Id, booking.ResourceId, booking.StartUtc, booking.EndUtc, booking.Quantity, booking.Status, cascadedIds);
    }

    private void CancelOccurrence(Booking occurrence)
    {
        occurrence.Status = BookingStatus.Cancelled;
        occurrence.CancelledAtUtc = DateTimeOffset.UtcNow;
        occurrence.CancelledByUserId = currentUserContext.UserId;
    }
}
