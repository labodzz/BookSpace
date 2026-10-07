using BookSpace.Application.Calendar;
using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

internal sealed class CalendarSubscriptionRepository(BookSpaceDbContext dbContext) : ICalendarSubscriptionRepository
{
    public Task<CalendarSubscription?> FindActiveByUserAsync(Guid userId, CancellationToken cancellationToken) =>
        dbContext.CalendarSubscriptions.FirstOrDefaultAsync(
            subscription => subscription.UserId == userId && subscription.RevokedAtUtc == null, cancellationToken);

    // Explicitly bypasses the tenant query filter - see the interface doc comment.
    public Task<CalendarSubscription?> FindActiveByTokenHashAsync(string tokenHash, CancellationToken cancellationToken) =>
        dbContext.CalendarSubscriptions.IgnoreQueryFilters().FirstOrDefaultAsync(
            subscription => subscription.TokenHash == tokenHash && subscription.RevokedAtUtc == null, cancellationToken);

    public async Task AddAsync(CalendarSubscription subscription, CancellationToken cancellationToken) =>
        await dbContext.CalendarSubscriptions.AddAsync(subscription, cancellationToken);

    // See the interface doc comment - explicitly bypasses the tenant filter for the one sanctioned
    // cross-tenant caller (SysAdmin tenant deletion).
    public async Task RevokeAllActiveForTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var activeSubscriptions = await dbContext.CalendarSubscriptions.IgnoreQueryFilters()
            .Where(subscription => subscription.TenantId == tenantId && subscription.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        foreach (var subscription in activeSubscriptions)
        {
            subscription.RevokedAtUtc = now;
        }
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => dbContext.SaveChangesHandlingConflictsAsync(cancellationToken);
}
