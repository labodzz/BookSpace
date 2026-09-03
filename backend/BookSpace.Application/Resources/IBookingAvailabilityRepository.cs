using BookSpace.Domain.Entities;

namespace BookSpace.Application.Resources;

// Read-only, deliberately not named IBookingRepository - full Booking CRUD belongs to a later work
// package. This only exposes what the availability query needs: active bookings overlapping a range.
public interface IBookingAvailabilityRepository
{
    Task<IReadOnlyList<Booking>> GetActiveBookingsAsync(
        Guid resourceId, DateTimeOffset rangeStartUtc, DateTimeOffset rangeEndUtcExclusive, CancellationToken cancellationToken);
}
