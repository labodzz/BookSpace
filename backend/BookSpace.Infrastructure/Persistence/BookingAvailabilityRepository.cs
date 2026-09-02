using BookSpace.Application.Resources;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

internal sealed class BookingAvailabilityRepository(BookSpaceDbContext dbContext) : IBookingAvailabilityRepository
{
    private static readonly BookingStatus[] ActiveStatuses = [BookingStatus.Pending, BookingStatus.Confirmed];

    // Only ResourceId and Status are filtered in SQL; the time-range overlap check happens
    // client-side after materializing - EF Core's SQLite provider (used by the integration test
    // suite) cannot translate DateTimeOffset `<`/`>` comparisons into SQL (SQL Server has no such
    // restriction), so this can't be a single server-side query. Mirrors BlackoutPeriodRepository/the
    // availability handler, which already fetch a resource's full set and filter the time range in
    // memory for the same reason.
    public async Task<IReadOnlyList<Booking>> GetActiveBookingsAsync(
        Guid resourceId, DateTimeOffset rangeStartUtc, DateTimeOffset rangeEndUtcExclusive, CancellationToken cancellationToken)
    {
        var activeBookings = await dbContext.Bookings
            .Where(booking => booking.ResourceId == resourceId && ActiveStatuses.Contains(booking.Status))
            .ToListAsync(cancellationToken);

        return activeBookings
            .Where(booking => booking.StartUtc < rangeEndUtcExclusive && booking.EndUtc > rangeStartUtc)
            .ToList();
    }
}
