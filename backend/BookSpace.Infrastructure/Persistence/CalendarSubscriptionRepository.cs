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

    public Task SaveChangesAsync(CancellationToken cancellationToken) => dbContext.SaveChangesHandlingConflictsAsync(cancellationToken);
}
