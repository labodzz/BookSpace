using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Application.Resources;
using BookSpace.Application.Security;
using BookSpace.Domain.Enums;
using FluentValidation;

namespace BookSpace.Application.Bookings;

// SeriesId, when given, narrows to that recurring series' own occurrences only. FromUtc/ToUtc, when
// given, narrow to bookings overlapping that range - lets the frontend calendar ask for just the
// currently-visible date range instead of paging through a user's entire booking history.
public sealed record GetOwnBookingsQueryRequest(
    int Page = 1, int PageSize = 20, Guid? SeriesId = null, DateTimeOffset? FromUtc = null, DateTimeOffset? ToUtc = null)
    : IRequest<PagedResult<GetOwnBookingsResponseItem>>;

// CancelledByAdmin is derived, not a stored column: since this query only ever returns the caller's OWN
// bookings, the one case CancelledByUserId can differ from the booking's own owner is a TenantAdmin
// override (see CancelBookingCommandHandler and docs/open-questions.md) - surfaced as a plain
// bool so the client doesn't need to know the caller's own user ID to tell "I cancelled this" from "this
// was administratively cancelled" apart. SeriesId is surfaced (unlike other Booking fields we don't
// expose) because this same query already accepts SeriesId as a filter, so the concept is already public
// API surface - the client needs it back to tell which bookings belong to the same recurring series.
// TimeZoneId is the booking's resource's own IANA zone (as of now, not a snapshot from booking time) -
// lets the client show the booking's local time at the resource alongside the viewer's own local time,
// without a separate per-booking resource lookup. See GetOwnBookingsQueryHandler for how it's populated.
// CheckedInAtUtc is null until checked in. CheckInWindowOpensAtUtc/ClosesAtUtc are always computed
// (regardless of Status) from the CALLER'S OWN tenant settings (Tenant.CheckInLeadMinutes/
// NoShowGraceMinutes) and this booking's own StartUtc - see CheckInEligibilityChecker, which this query
// deliberately does NOT reuse (that checker also needs "now" and CheckedInAtUtc to decide pass/fail;
// listing bookings only needs the two boundary instants themselves, for the client to compute its own
// enabled/disabled button state and to display the exact window without hardcoding tenant-specific
// minutes). Exposed on every item, not just Confirmed ones, so the client never has to special-case a
// missing field.
public sealed record GetOwnBookingsResponseItem(
    Guid Id, Guid ResourceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Quantity, BookingStatus Status,
    DateTimeOffset? CancelledAtUtc, bool CancelledByAdmin, string? CancellationReason, Guid? SeriesId, string TimeZoneId,
    DateTimeOffset? CheckedInAtUtc, DateTimeOffset CheckInWindowOpensAtUtc, DateTimeOffset CheckInWindowClosesAtUtc);

public sealed class GetOwnBookingsQueryRequestValidator : AbstractValidator<GetOwnBookingsQueryRequest>
{
    public GetOwnBookingsQueryRequestValidator()
    {
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, 100);
        RuleFor(query => query.ToUtc)
            .GreaterThan(query => query.FromUtc)
            .When(query => query.FromUtc is not null && query.ToUtc is not null)
            .WithMessage("ToUtc must be after FromUtc.");
    }
}

// Deliberately has no ResourceId/TenantId/UserId parameter to accept from the client - the owner is
// always the caller (ICurrentUserContext.UserId), never client-supplied, so a member can only ever list
// their own bookings.
public sealed class GetOwnBookingsQueryHandler(
    IBookingRepository bookingRepository, IResourceRepository resourceRepository, ITenantRepository tenantRepository,
    ICurrentUserContext currentUserContext)
    : IRequestHandler<GetOwnBookingsQueryRequest, PagedResult<GetOwnBookingsResponseItem>>
{
    public async Task<PagedResult<GetOwnBookingsResponseItem>> Handle(GetOwnBookingsQueryRequest request, CancellationToken cancellationToken)
    {
        // The global SysAdmin has no TenantId and can never have a booking - "my bookings" is simply
        // always empty for it, not an error (unlike the write-side handlers in this folder, which reject
        // the attempt outright - see TenantMembershipGuard). Returning empty here, rather than throwing,
        // matters in practice: the dashboard calls this for every signed-in user unconditionally.
        if (currentUserContext.TenantId is null)
        {
            return new PagedResult<GetOwnBookingsResponseItem>([], request.Page, request.PageSize, 0);
        }

        var paged = await bookingRepository.GetOwnBookingsAsync(
            currentUserContext.UserId!.Value, request.SeriesId, request.FromUtc, request.ToUtc, request.Page, request.PageSize, cancellationToken);

        // Batched once per page, not once per booking - see IResourceRepository.GetByIdsAsync's own
        // comment. Bounded by the distinct resource count on this one page, never by the page size.
        var resourceIds = paged.Items.Select(booking => booking.ResourceId).Distinct().ToList();
        var resources = resourceIds.Count == 0
            ? []
            : await resourceRepository.GetByIdsAsync(resourceIds, cancellationToken) ?? [];
        var timeZoneByResourceId = resources.ToDictionary(resource => resource.Id, resource => resource.TimeZoneId);

        // One tenant lookup per page (not per booking) - every item on this page belongs to the SAME
        // caller, so there is only ever one tenant's CheckInLeadMinutes/NoShowGraceMinutes to resolve.
        var tenant = await tenantRepository.FindByIdAsync(currentUserContext.TenantId!.Value, cancellationToken)
            ?? throw new InvalidOperationException($"Tenant {currentUserContext.TenantId} for the current request was not found.");

        return new PagedResult<GetOwnBookingsResponseItem>(
            paged.Items.Select(booking => new GetOwnBookingsResponseItem(
                booking.Id, booking.ResourceId, booking.StartUtc, booking.EndUtc, booking.Quantity, booking.Status,
                booking.CancelledAtUtc, booking.CancelledByUserId is { } cancelledByUserId && cancelledByUserId != booking.UserId,
                booking.CancellationReason, booking.SeriesId, timeZoneByResourceId.GetValueOrDefault(booking.ResourceId, string.Empty),
                booking.CheckedInAtUtc, booking.StartUtc.AddMinutes(-tenant.CheckInLeadMinutes), booking.StartUtc.AddMinutes(tenant.NoShowGraceMinutes)))
                .ToList(),
            paged.Page,
            paged.PageSize,
            paged.TotalCount);
    }
}
