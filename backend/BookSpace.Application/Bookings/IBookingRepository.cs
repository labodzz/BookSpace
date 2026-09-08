using BookSpace.Application.Common;
using BookSpace.Domain.Entities;

namespace BookSpace.Application.Bookings;

public interface IBookingRepository
{
    Task<Booking?> FindByIdAsync(Guid id, CancellationToken cancellationToken);

    // A projection, not a tracked entity load - deliberately for ApproveBookingCommandHandler's
    // pre-lock step, which only needs the ResourceId to know which resource to lock and cannot afford
    // to track the Booking yet. If it used FindByIdAsync here, the SAME DbContext's identity map would
    // return that SAME tracked instance on the later "fresh" re-read taken after the lock is acquired,
    // silently serving stale in-memory data instead of the current row - defeating the entire point of
    // re-reading under the lock. Proven by BookSpace.Infrastructure.Tests.ApprovalConcurrencyTests,
    // which failed with two concurrent approvals both succeeding before this method existed.
    Task<Guid?> FindResourceIdAsync(Guid bookingId, CancellationToken cancellationToken);

    // Relies on the DbContext's global tenant query filter to scope results, further filtered to the
    // given owner so a member only ever sees their own bookings, never another tenant member's.
    // seriesId, when given, narrows to that recurring series' own occurrences only.
    Task<PagedResult<Booking>> GetOwnBookingsAsync(Guid userId, Guid? seriesId, int page, int pageSize, CancellationToken cancellationToken);

    // Every occurrence of a series (tenant-filtered, not further filtered by user - a series' occurrences
    // all share its one owning UserId by construction). Used to view a series' full occurrence list and
    // to cascade a "cancel remaining series" request.
    Task<IReadOnlyList<Booking>> GetBySeriesIdAsync(Guid seriesId, CancellationToken cancellationToken);

    // Bookings with an outstanding ApprovalRequest, for the approver's own queue. resourceIds narrows to
    // a specific set (a ResourceApprover's assigned resources); null returns every Pending booking in the
    // tenant (TenantAdmin/SysAdmin, who aren't restricted to specific resources).
    Task<IReadOnlyList<Booking>> GetPendingApprovalAsync(IReadOnlyList<Guid>? resourceIds, CancellationToken cancellationToken);

    Task AddAsync(Booking booking, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
