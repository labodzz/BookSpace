using BookSpace.Domain.Entities;

namespace BookSpace.Application.Resources;

public interface IResourceApproverRepository
{
    Task<ResourceApprover?> FindByResourceAndUserAsync(Guid resourceId, Guid userId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ResourceApprover>> GetByResourceIdAsync(Guid resourceId, CancellationToken cancellationToken);

    Task AddAsync(ResourceApprover approver, CancellationToken cancellationToken);

    Task RemoveAsync(ResourceApprover approver, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
