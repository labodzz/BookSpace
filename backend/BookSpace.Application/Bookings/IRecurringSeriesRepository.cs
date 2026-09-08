using BookSpace.Domain.Entities;

namespace BookSpace.Application.Bookings;

public interface IRecurringSeriesRepository
{
    Task<RecurringSeries?> FindByIdAsync(Guid id, CancellationToken cancellationToken);

    Task AddAsync(RecurringSeries series, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
