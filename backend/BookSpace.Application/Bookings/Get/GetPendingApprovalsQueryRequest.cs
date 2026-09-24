using BookSpace.Application.Mediator;
using BookSpace.Application.Resources;
using BookSpace.Application.Security;

namespace BookSpace.Application.Bookings;

public sealed record GetPendingApprovalsQueryRequest : IRequest<IReadOnlyList<GetPendingApprovalsResponseItem>>;

// SeriesId lets the client group an approver's queue by recurring series (e.g. to offer "approve every
// pending occurrence in this series at once") instead of only ever showing 100 flat, unrelated-looking
// rows for one series. TimeZoneId is the booking's resource's own IANA zone, added so the client can show
// the request's local time at the resource alongside the approver's own local time.
public sealed record GetPendingApprovalsResponseItem(
    Guid BookingId, Guid ResourceId, Guid UserId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Quantity, DateTimeOffset ExpiresAtUtc,
    Guid? SeriesId, string TimeZoneId);

// The approver's own queue: TenantAdmin/SysAdmin see every Pending booking in the tenant; a plain
// Approver sees only bookings for resources they're a ResourceApprover for - never another approver's
// resources, and never another tenant's bookings at all (the global query filter already guarantees that).
public sealed class GetPendingApprovalsQueryHandler(
    IBookingRepository bookingRepository, IApprovalRequestRepository approvalRequestRepository,
    IResourceApproverRepository resourceApproverRepository, IResourceRepository resourceRepository, ICurrentUserContext currentUserContext)
    : IRequestHandler<GetPendingApprovalsQueryRequest, IReadOnlyList<GetPendingApprovalsResponseItem>>
{
    public async Task<IReadOnlyList<GetPendingApprovalsResponseItem>> Handle(GetPendingApprovalsQueryRequest request, CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid>? resourceIds = null;
        if (!currentUserContext.Roles.Contains("TenantAdmin") && !currentUserContext.Roles.Contains("SysAdmin"))
        {
            resourceIds = await resourceApproverRepository.GetResourceIdsByUserAsync(currentUserContext.UserId!.Value, cancellationToken);
        }

        var bookings = await bookingRepository.GetPendingApprovalAsync(resourceIds, cancellationToken);
        if (bookings.Count == 0)
        {
            return [];
        }

        var approvalRequests = await approvalRequestRepository.GetByBookingIdsAsync(bookings.Select(booking => booking.Id).ToList(), cancellationToken);
        var expiryByBookingId = approvalRequests.ToDictionary(approvalRequest => approvalRequest.BookingId, approvalRequest => approvalRequest.ExpiresAtUtc);

        // Batched once for the whole queue, not once per booking - see IResourceRepository.GetByIdsAsync.
        var distinctResourceIds = bookings.Select(booking => booking.ResourceId).Distinct().ToList();
        var resources = await resourceRepository.GetByIdsAsync(distinctResourceIds, cancellationToken) ?? [];
        var timeZoneByResourceId = resources.ToDictionary(resource => resource.Id, resource => resource.TimeZoneId);

        return bookings
            .Select(booking => new GetPendingApprovalsResponseItem(
                booking.Id, booking.ResourceId, booking.UserId, booking.StartUtc, booking.EndUtc, booking.Quantity,
                expiryByBookingId.GetValueOrDefault(booking.Id), booking.SeriesId,
                timeZoneByResourceId.GetValueOrDefault(booking.ResourceId, string.Empty)))
            .OrderBy(item => item.StartUtc)
            .ToList();
    }
}
