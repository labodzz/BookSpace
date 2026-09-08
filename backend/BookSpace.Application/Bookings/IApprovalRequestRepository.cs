using BookSpace.Domain.Entities;

namespace BookSpace.Application.Bookings;

public interface IApprovalRequestRepository
{
    Task<ApprovalRequest?> FindByBookingIdAsync(Guid bookingId, CancellationToken cancellationToken);

    // Batched (not N+1) lookup for GetPendingApprovalsQueryHandler, which already has a bounded list of
    // booking ids from IBookingRepository.GetPendingApprovalAsync.
    Task<IReadOnlyList<ApprovalRequest>> GetByBookingIdsAsync(IReadOnlyList<Guid> bookingIds, CancellationToken cancellationToken);

    Task AddAsync(ApprovalRequest approvalRequest, CancellationToken cancellationToken);

    // Mutations do not call SaveChangesAsync themselves - a caller creating/deciding an ApprovalRequest
    // alongside its Booking saves both in one SaveChangesAsync call (either repository's, since they
    // share the same scoped DbContext), matching the existing repository convention.
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
