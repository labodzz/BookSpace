using BookSpace.Domain.Entities;

namespace BookSpace.Application.Resources;

public interface IAvailabilityRuleRepository
{
    Task<AvailabilityRule?> FindByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(Guid resourceId, DayOfWeek dayOfWeek, TimeOnly startTime, TimeOnly endTime, CancellationToken cancellationToken);

    // Relies on the DbContext's global tenant query filter to scope results.
    Task<IReadOnlyList<AvailabilityRule>> GetByResourceIdAsync(Guid resourceId, CancellationToken cancellationToken);

    Task AddAsync(AvailabilityRule rule, CancellationToken cancellationToken);

    Task RemoveAsync(AvailabilityRule rule, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
