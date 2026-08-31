using BookSpace.Application.Resources;
using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

internal sealed class AvailabilityRuleRepository(BookSpaceDbContext dbContext) : IAvailabilityRuleRepository
{
    public Task<AvailabilityRule?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.AvailabilityRules.FirstOrDefaultAsync(rule => rule.Id == id, cancellationToken);

    public Task<bool> ExistsAsync(Guid resourceId, DayOfWeek dayOfWeek, TimeOnly startTime, TimeOnly endTime, CancellationToken cancellationToken) =>
        dbContext.AvailabilityRules.AnyAsync(
            rule => rule.ResourceId == resourceId && rule.DayOfWeek == dayOfWeek && rule.StartTime == startTime && rule.EndTime == endTime,
            cancellationToken);

    public async Task<IReadOnlyList<AvailabilityRule>> GetByResourceIdAsync(Guid resourceId, CancellationToken cancellationToken) =>
        await dbContext.AvailabilityRules
            .Where(rule => rule.ResourceId == resourceId)
            .OrderBy(rule => rule.DayOfWeek)
            .ThenBy(rule => rule.StartTime)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(AvailabilityRule rule, CancellationToken cancellationToken) =>
        await dbContext.AvailabilityRules.AddAsync(rule, cancellationToken);

    public Task RemoveAsync(AvailabilityRule rule, CancellationToken cancellationToken)
    {
        dbContext.AvailabilityRules.Remove(rule);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        dbContext.SaveChangesHandlingConflictsAsync(cancellationToken);
}
