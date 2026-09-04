using BookSpace.Application.Resources;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

internal sealed class BookingAvailabilityRepository(BookSpaceDbContext dbContext) : IBookingAvailabilityRepository
{
    private static readonly BookingStatus[] ActiveStatuses = [BookingStatus.Pending, BookingStatus.Confirmed];

    // On SQL Server, ResourceId, Status, AND the time-range overlap are all filtered in SQL, bounded by
    // the Booking(TenantId, ResourceId, StartUtc, EndUtc) index - a resource with a long booking
    // history must not force every availability query to materialize every booking it has ever had
    // just to answer a query for next week. EF Core's SQLite provider (used by the integration test
    // suite for speed) cannot translate the DateTimeOffset `<`/`>` comparisons into SQL - proven by
    // GetAvailability_* tests throwing InvalidOperationException the moment this range filter was
    // pushed into the query - so it falls back to the previous fetch-then-filter-client-side approach
    // there; real range-filtering behavior is instead proven against real SQL Server (see
    // BookingAvailabilityRepositoryTests).
    public async Task<IReadOnlyList<Booking>> GetActiveBookingsAsync(
        Guid resourceId, DateTimeOffset rangeStartUtc, DateTimeOffset rangeEndUtcExclusive, CancellationToken cancellationToken)
    {
        if (dbContext.Database.IsSqlServer())
        {
            return await dbContext.Bookings
                .Where(booking => booking.ResourceId == resourceId
                    && ActiveStatuses.Contains(booking.Status)
                    && booking.StartUtc < rangeEndUtcExclusive
                    && booking.EndUtc > rangeStartUtc)
                .ToListAsync(cancellationToken);
        }

        var activeBookings = await dbContext.Bookings
            .Where(booking => booking.ResourceId == resourceId && ActiveStatuses.Contains(booking.Status))
            .ToListAsync(cancellationToken);

        return activeBookings
            .Where(booking => booking.StartUtc < rangeEndUtcExclusive && booking.EndUtc > rangeStartUtc)
            .ToList();
    }
}
