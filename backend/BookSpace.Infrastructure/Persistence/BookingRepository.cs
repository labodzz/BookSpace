using BookSpace.Application.Bookings;
using BookSpace.Application.Common;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

internal sealed class BookingRepository(BookSpaceDbContext dbContext) : IBookingRepository
{
    public Task<Booking?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Bookings.FirstOrDefaultAsync(booking => booking.Id == id, cancellationToken);

    // A projection (Select), not a tracked entity load - see the interface doc comment for why this
    // matters. EF Core never adds a projected anonymous/scalar result to the change tracker, so this
    // cannot collide with a later tracked FindByIdAsync for the same id on the same DbContext.
    public async Task<Guid?> FindResourceIdAsync(Guid bookingId, CancellationToken cancellationToken) =>
        await dbContext.Bookings.Where(booking => booking.Id == bookingId).Select(booking => (Guid?)booking.ResourceId).FirstOrDefaultAsync(cancellationToken);

    public async Task<PagedResult<Booking>> GetOwnBookingsAsync(Guid userId, Guid? seriesId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = dbContext.Bookings.Where(booking => booking.UserId == userId);
        if (seriesId is { } series)
        {
            query = query.Where(booking => booking.SeriesId == series);
        }

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

    public async Task<IReadOnlyList<Booking>> GetBySeriesIdAsync(Guid seriesId, CancellationToken cancellationToken) =>
        await dbContext.Bookings.Where(booking => booking.SeriesId == seriesId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Booking>> GetPendingApprovalAsync(IReadOnlyList<Guid>? resourceIds, CancellationToken cancellationToken)
    {
        var query = dbContext.Bookings.Where(booking => booking.Status == BookingStatus.Pending);
        if (resourceIds is not null)
        {
            query = query.Where(booking => resourceIds.Contains(booking.ResourceId));
        }

        return await query.ToListAsync(cancellationToken);
    }

    public async Task AddAsync(Booking booking, CancellationToken cancellationToken) =>
        await dbContext.Bookings.AddAsync(booking, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        dbContext.SaveChangesHandlingConflictsAsync(cancellationToken);
}
