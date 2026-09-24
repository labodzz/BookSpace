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

    // Batched lookup for handlers that need to resolve several bookings' resources at once (e.g. to
    // read each one's TimeZoneId for a dual-timezone display) without querying once per booking. Returns
    // every match regardless of Status - unlike GetPagedAsync, an archived resource's own past bookings
    // must still be able to resolve its original TimeZoneId.
    Task<IReadOnlyList<Resource>> GetByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken);

    Task AddAsync(Resource resource, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
