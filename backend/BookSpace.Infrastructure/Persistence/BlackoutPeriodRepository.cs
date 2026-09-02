using BookSpace.Application.Resources;
using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

internal sealed class BlackoutPeriodRepository(BookSpaceDbContext dbContext) : IBlackoutPeriodRepository
{
    public Task<BlackoutPeriod?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.BlackoutPeriods.FirstOrDefaultAsync(period => period.Id == id, cancellationToken);

    // Sorted client-side after materializing rather than via ORDER BY: SQLite's EF provider can't
    // translate ORDER BY on a DateTimeOffset column (SQL Server has no such restriction, so this only
    // surfaces against the SQLite backend the integration test suite uses).
    public async Task<IReadOnlyList<BlackoutPeriod>> GetByResourceIdAsync(Guid resourceId, CancellationToken cancellationToken)
    {
        var periods = await dbContext.BlackoutPeriods
            .Where(period => period.ResourceId == resourceId)
            .ToListAsync(cancellationToken);

        return periods.OrderBy(period => period.StartUtc).ToList();
    }

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
