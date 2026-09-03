using BookSpace.Domain.Entities;

namespace BookSpace.Application.ResourceTypes;

public interface IResourceTypeRepository
{
    Task<ResourceType?> FindByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> ExistsByNameAsync(string name, Guid? excludingResourceTypeId, CancellationToken cancellationToken);

    // Backs the Delete guard: a ResourceType still referenced by a Resource can't be hard-deleted
    // (Resource.ResourceTypeId has OnDelete(Restrict)), so the handler checks this first and throws a
    // friendly ConflictException instead of letting a raw DbUpdateException reach GlobalExceptionHandler.
    Task<bool> IsReferencedByAnyResourceAsync(Guid resourceTypeId, CancellationToken cancellationToken);

    // Relies on the DbContext's global tenant query filter to scope results - no pagination, the
    // number of resource types per tenant is expected to stay small (a handful of categories).
    Task<IReadOnlyList<ResourceType>> GetAllAsync(CancellationToken cancellationToken);

    Task AddAsync(ResourceType resourceType, CancellationToken cancellationToken);

    Task RemoveAsync(ResourceType resourceType, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
