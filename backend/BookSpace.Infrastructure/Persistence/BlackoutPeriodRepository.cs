using BookSpace.Application.Resources;
using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

internal sealed class BlackoutPeriodRepository(BookSpaceDbContext dbContext) : IBlackoutPeriodRepository
{
    public Task<BlackoutPeriod?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.BlackoutPeriods.FirstOrDefaultAsync(period => period.Id == id, cancellationToken);

    public async Task<IReadOnlyList<BlackoutPeriod>> GetByResourceIdAsync(Guid resourceId, CancellationToken cancellationToken) =>
        await dbContext.BlackoutPeriods
            .Where(period => period.ResourceId == resourceId)
            .OrderBy(period => period.StartUtc)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(BlackoutPeriod period, CancellationToken cancellationToken) =>
        await dbContext.BlackoutPeriods.AddAsync(period, cancellationToken);

    public Task RemoveAsync(BlackoutPeriod period, CancellationToken cancellationToken)
    {
        dbContext.BlackoutPeriods.Remove(period);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        dbContext.SaveChangesHandlingConflictsAsync(cancellationToken);
}
