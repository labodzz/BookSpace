using BookSpace.Application.Bookings;
using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

internal sealed class ApprovalRequestRepository(BookSpaceDbContext dbContext) : IApprovalRequestRepository
{
    public Task<ApprovalRequest?> FindByBookingIdAsync(Guid bookingId, CancellationToken cancellationToken) =>
        dbContext.ApprovalRequests.FirstOrDefaultAsync(request => request.BookingId == bookingId, cancellationToken);

    public async Task<IReadOnlyList<ApprovalRequest>> GetByBookingIdsAsync(IReadOnlyList<Guid> bookingIds, CancellationToken cancellationToken) =>
        await dbContext.ApprovalRequests
            .Where(request => bookingIds.Contains(request.BookingId))
            .ToListAsync(cancellationToken);

    public async Task AddAsync(ApprovalRequest approvalRequest, CancellationToken cancellationToken) =>
        await dbContext.ApprovalRequests.AddAsync(approvalRequest, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        dbContext.SaveChangesHandlingConflictsAsync(cancellationToken);
}
