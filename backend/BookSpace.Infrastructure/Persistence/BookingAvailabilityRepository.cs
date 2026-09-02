using BookSpace.Application.Resources;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

internal sealed class BookingAvailabilityRepository(BookSpaceDbContext dbContext) : IBookingAvailabilityRepository
{
    private static readonly BookingStatus[] ActiveStatuses = [BookingStatus.Pending, BookingStatus.Confirmed];

    public async Task<IReadOnlyList<Booking>> GetActiveBookingsAsync(
        Guid resourceId, DateTimeOffset rangeStartUtc, DateTimeOffset rangeEndUtcExclusive, CancellationToken cancellationToken) =>
        await dbContext.Bookings
            .Where(booking => booking.ResourceId == resourceId
                && ActiveStatuses.Contains(booking.Status)
                && booking.StartUtc < rangeEndUtcExclusive
                && booking.EndUtc > rangeStartUtc)
            .ToListAsync(cancellationToken);
}
