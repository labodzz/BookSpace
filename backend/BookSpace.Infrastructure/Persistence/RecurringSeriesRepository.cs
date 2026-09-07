using BookSpace.Application.Bookings;
using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

internal sealed class RecurringSeriesRepository(BookSpaceDbContext dbContext) : IRecurringSeriesRepository
{
    public Task<RecurringSeries?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.RecurringSeries.FirstOrDefaultAsync(series => series.Id == id, cancellationToken);

    public async Task AddAsync(RecurringSeries series, CancellationToken cancellationToken) =>
        await dbContext.RecurringSeries.AddAsync(series, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        dbContext.SaveChangesHandlingConflictsAsync(cancellationToken);
}
