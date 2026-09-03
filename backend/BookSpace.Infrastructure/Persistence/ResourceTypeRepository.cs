using BookSpace.Application.ResourceTypes;
using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

internal sealed class ResourceTypeRepository(BookSpaceDbContext dbContext) : IResourceTypeRepository
{
    public Task<ResourceType?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.ResourceTypes.FirstOrDefaultAsync(resourceType => resourceType.Id == id, cancellationToken);

    public Task<bool> ExistsByNameAsync(string name, Guid? excludingResourceTypeId, CancellationToken cancellationToken)
    {
        var query = dbContext.ResourceTypes.Where(resourceType => resourceType.Name == name);
        if (excludingResourceTypeId is { } id)
        {
            query = query.Where(resourceType => resourceType.Id != id);
        }

        return query.AnyAsync(cancellationToken);
    }

    // dbContext.Resources already carries its own tenant query filter, so this never crosses into
    // another tenant's usage of a same-named type in a different tenant's ResourceType row.
    public Task<bool> IsReferencedByAnyResourceAsync(Guid resourceTypeId, CancellationToken cancellationToken) =>
        dbContext.Resources.AnyAsync(resource => resource.ResourceTypeId == resourceTypeId, cancellationToken);

    public async Task<IReadOnlyList<ResourceType>> GetAllAsync(CancellationToken cancellationToken) =>
        await dbContext.ResourceTypes.ToListAsync(cancellationToken);

    public async Task AddAsync(ResourceType resourceType, CancellationToken cancellationToken) =>
        await dbContext.ResourceTypes.AddAsync(resourceType, cancellationToken);

    public Task RemoveAsync(ResourceType resourceType, CancellationToken cancellationToken)
    {
        dbContext.ResourceTypes.Remove(resourceType);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        dbContext.SaveChangesHandlingConflictsAsync(cancellationToken);
}
