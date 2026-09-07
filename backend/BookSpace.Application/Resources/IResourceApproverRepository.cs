using BookSpace.Domain.Entities;

namespace BookSpace.Application.Resources;

public interface IResourceApproverRepository
{
    Task<ResourceApprover?> FindByResourceAndUserAsync(Guid resourceId, Guid userId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ResourceApprover>> GetByResourceIdAsync(Guid resourceId, CancellationToken cancellationToken);

    // The resources a given user is an approver for - used by the approval workflow (Bookings feature)
    // to scope "my pending approvals" to only the resources the caller actually approves for.
    Task<IReadOnlyList<Guid>> GetResourceIdsByUserAsync(Guid userId, CancellationToken cancellationToken);

    Task AddAsync(ResourceApprover approver, CancellationToken cancellationToken);

    Task RemoveAsync(ResourceApprover approver, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
