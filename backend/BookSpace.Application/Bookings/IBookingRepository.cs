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
    // seriesId, when given, narrows to that recurring series' own occurrences only. fromUtc/toUtc, when
    // given, narrow to bookings overlapping that range - added for the frontend calendar, which must be
    // able to ask for "just this visible month" instead of paging through a user's entire booking history.
    // Ordered by CreatedAtUtc descending (then Id descending as a deterministic tiebreaker) - most
    // RECENTLY REQUESTED first, not soonest-starting first. This is what the My Bookings list wants, and
    // it's a page-order-only concern: both frontend consumers that paginate through fromUtc/toUtc
    // (Calendar, the dashboard's upcoming-bookings widget) fetch every page in range and re-sort the
    // union by StartUtc themselves, so this ordering is invisible to them outside of pathological
    // truncation.
    Task<PagedResult<Booking>> GetOwnBookingsAsync(
        Guid userId, Guid? seriesId, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, int page, int pageSize, CancellationToken cancellationToken);

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
