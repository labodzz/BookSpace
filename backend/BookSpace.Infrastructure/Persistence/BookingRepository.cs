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

    public async Task<PagedResult<Booking>> GetOwnBookingsAsync(
        Guid userId, Guid? seriesId, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = dbContext.Bookings.Where(booking => booking.UserId == userId);
        if (seriesId is { } series)
        {
            query = query.Where(booking => booking.SeriesId == series);
        }

        // SQLite (fast integration tests only) cannot translate the DateTimeOffset `<`/`>` range
        // comparisons, or ORDER BY on a DateTimeOffset column, into SQL - the same limitation
        // BookingAvailabilityRepository already works around - so both the range filter and the
        // ordering fall back to an in-memory pass there; a single user's own booking count is never
        // large enough for that to matter. SQL Server does the range filter, ordering, and paging in
        // the database as usual, bounded by the range so a calendar view never has to page through a
        // user's entire booking history just to render one visible month.
        if (dbContext.Database.IsSqlServer())
        {
            if (fromUtc is { } from)
            {
                query = query.Where(booking => booking.EndUtc > from);
            }

            if (toUtc is { } to)
            {
                query = query.Where(booking => booking.StartUtc < to);
            }

            var totalCount = await query.CountAsync(cancellationToken);
            // Ordered by when the request was MADE (CreatedAtUtc), not by the booking's own start time -
            // "most recent request" and "starts soonest" are unrelated: an old booking for next month and
            // a booking made five seconds ago for tomorrow are not orderable by StartUtc alone. Id is a
            // deterministic tiebreaker for the (rare, but possible at typical timestamp precision)
            // case of two requests recording the same CreatedAtUtc - without it, paging could show the
            // same tied row twice or skip it, since relational ORDER BY is not guaranteed stable across
            // otherwise-equal keys.
            var items = await query
                .OrderByDescending(booking => booking.CreatedAtUtc)
                .ThenByDescending(booking => booking.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken);
            return new PagedResult<Booking>(items, page, pageSize, totalCount);
        }

        var allItems = await query.ToListAsync(cancellationToken);
        var rangeFiltered = allItems
            .Where(booking => fromUtc is not { } from || booking.EndUtc > from)
            .Where(booking => toUtc is not { } to || booking.StartUtc < to)
            .ToList();
        var pagedItems = rangeFiltered
            .OrderByDescending(booking => booking.CreatedAtUtc)
            .ThenByDescending(booking => booking.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
        return new PagedResult<Booking>(pagedItems, page, pageSize, rangeFiltered.Count);
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
