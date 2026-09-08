using BookSpace.Application.Resources;
using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

internal sealed class ResourceApproverRepository(BookSpaceDbContext dbContext) : IResourceApproverRepository
{
    public Task<ResourceApprover?> FindByResourceAndUserAsync(Guid resourceId, Guid userId, CancellationToken cancellationToken) =>
        dbContext.ResourceApprovers.FirstOrDefaultAsync(
            approver => approver.ResourceId == resourceId && approver.UserId == userId, cancellationToken);

    public async Task<IReadOnlyList<ResourceApprover>> GetByResourceIdAsync(Guid resourceId, CancellationToken cancellationToken) =>
        await dbContext.ResourceApprovers
            .Where(approver => approver.ResourceId == resourceId)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> GetResourceIdsByUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await dbContext.ResourceApprovers
            .Where(approver => approver.UserId == userId)
            .Select(approver => approver.ResourceId)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(ResourceApprover approver, CancellationToken cancellationToken) =>
        await dbContext.ResourceApprovers.AddAsync(approver, cancellationToken);

    public Task RemoveAsync(ResourceApprover approver, CancellationToken cancellationToken)
    {
        dbContext.ResourceApprovers.Remove(approver);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        dbContext.SaveChangesHandlingConflictsAsync(cancellationToken);
}
