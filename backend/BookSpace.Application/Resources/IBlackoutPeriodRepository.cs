using BookSpace.Domain.Entities;

namespace BookSpace.Application.Resources;

public interface IBlackoutPeriodRepository
{
    Task<BlackoutPeriod?> FindByIdAsync(Guid id, CancellationToken cancellationToken);

    // Relies on the DbContext's global tenant query filter to scope results.
    Task<IReadOnlyList<BlackoutPeriod>> GetByResourceIdAsync(Guid resourceId, CancellationToken cancellationToken);

    Task AddAsync(BlackoutPeriod period, CancellationToken cancellationToken);

    Task RemoveAsync(BlackoutPeriod period, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
