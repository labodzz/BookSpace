using BookSpace.Application.Common;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Resources;

public interface IResourceRepository
{
    Task<Resource?> FindByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> ExistsByNameAsync(string name, Guid? excludingResourceId, CancellationToken cancellationToken);

    Task<bool> ResourceTypeExistsAsync(Guid resourceTypeId, CancellationToken cancellationToken);

    // Relies on the DbContext's global tenant query filter to scope results - callers must not add
    // their own TenantId filter on top of this. Excludes archived resources unless a status is
    // explicitly requested.
    Task<PagedResult<Resource>> GetPagedAsync(
        int page, int pageSize, Guid? resourceTypeId, ResourceStatus? status, CancellationToken cancellationToken);

    Task AddAsync(Resource resource, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
