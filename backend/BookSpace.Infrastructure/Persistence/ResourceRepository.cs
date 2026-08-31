using BookSpace.Application.Common;
using BookSpace.Application.Resources;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

internal sealed class ResourceRepository(BookSpaceDbContext dbContext) : IResourceRepository
{
    public Task<Resource?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Resources.FirstOrDefaultAsync(resource => resource.Id == id, cancellationToken);

    public Task<bool> ExistsByNameAsync(string name, Guid? excludingResourceId, CancellationToken cancellationToken)
    {
        var query = dbContext.Resources.Where(resource => resource.Name == name);
        if (excludingResourceId is { } id)
        {
            query = query.Where(resource => resource.Id != id);
        }

        return query.AnyAsync(cancellationToken);
    }

    public Task<bool> ResourceTypeExistsAsync(Guid resourceTypeId, CancellationToken cancellationToken) =>
        dbContext.ResourceTypes.AnyAsync(type => type.Id == resourceTypeId, cancellationToken);

    public async Task<PagedResult<Resource>> GetPagedAsync(
        int page, int pageSize, Guid? resourceTypeId, ResourceStatus? status, CancellationToken cancellationToken)
    {
        var query = dbContext.Resources.AsQueryable();

        if (resourceTypeId is { } typeId)
        {
            query = query.Where(resource => resource.ResourceTypeId == typeId);
        }

        query = status is { } explicitStatus
            ? query.Where(resource => resource.Status == explicitStatus)
            : query.Where(resource => resource.Status != ResourceStatus.Archived);

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(resource => resource.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<Resource>(items, page, pageSize, totalCount);
    }

    public async Task AddAsync(Resource resource, CancellationToken cancellationToken) =>
        await dbContext.Resources.AddAsync(resource, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        dbContext.SaveChangesAsync(cancellationToken);
}
