using BookSpace.Application.Bookings;
using BookSpace.Application.Common;
using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

internal sealed class BookingRepository(BookSpaceDbContext dbContext) : IBookingRepository
{
    public Task<Booking?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Bookings.FirstOrDefaultAsync(booking => booking.Id == id, cancellationToken);

    public async Task<PagedResult<Booking>> GetOwnBookingsAsync(Guid userId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = dbContext.Bookings.Where(booking => booking.UserId == userId);
        var totalCount = await query.CountAsync(cancellationToken);

        // SQLite (fast integration tests only) cannot translate ORDER BY on a DateTimeOffset column -
        // the same limitation BookingAvailabilityRepository already works around for range filters -
        // so it orders/pages in memory instead; a single user's own booking count is never large enough
        // for that to matter. SQL Server orders/pages in the database as usual.
        if (dbContext.Database.IsSqlServer())
        {
            var items = await query
                .OrderByDescending(booking => booking.StartUtc)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken);
            return new PagedResult<Booking>(items, page, pageSize, totalCount);
        }

        var allItems = await query.ToListAsync(cancellationToken);
        var pagedItems = allItems
            .OrderByDescending(booking => booking.StartUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
        return new PagedResult<Booking>(pagedItems, page, pageSize, totalCount);
    }

    public async Task AddAsync(Booking booking, CancellationToken cancellationToken) =>
        await dbContext.Bookings.AddAsync(booking, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        dbContext.SaveChangesHandlingConflictsAsync(cancellationToken);
}
