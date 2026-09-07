using BookSpace.Application.Common;
using BookSpace.Domain.Entities;

namespace BookSpace.Application.Bookings;

public interface IBookingRepository
{
    Task<Booking?> FindByIdAsync(Guid id, CancellationToken cancellationToken);

    // Relies on the DbContext's global tenant query filter to scope results, further filtered to the
    // given owner so a member only ever sees their own bookings, never another tenant member's.
    Task<PagedResult<Booking>> GetOwnBookingsAsync(Guid userId, int page, int pageSize, CancellationToken cancellationToken);

    Task AddAsync(Booking booking, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
