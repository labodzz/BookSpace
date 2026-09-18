using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
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
// bookings, the one case CancelledByUserId can differ from the booking's own owner is a TenantAdmin/
// SysAdmin override (see CancelBookingCommandHandler and docs/open-questions.md) - surfaced as a plain
// bool so the client doesn't need to know the caller's own user ID to tell "I cancelled this" from "this
// was administratively cancelled" apart. SeriesId is surfaced (unlike other Booking fields we don't
// expose) because this same query already accepts SeriesId as a filter, so the concept is already public
// API surface - the client needs it back to tell which bookings belong to the same recurring series.
public sealed record GetOwnBookingsResponseItem(
    Guid Id, Guid ResourceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Quantity, BookingStatus Status,
    DateTimeOffset? CancelledAtUtc, bool CancelledByAdmin, string? CancellationReason, Guid? SeriesId);

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
public sealed class GetOwnBookingsQueryHandler(IBookingRepository bookingRepository, ICurrentUserContext currentUserContext)
    : IRequestHandler<GetOwnBookingsQueryRequest, PagedResult<GetOwnBookingsResponseItem>>
{
    public async Task<PagedResult<GetOwnBookingsResponseItem>> Handle(GetOwnBookingsQueryRequest request, CancellationToken cancellationToken)
    {
        var paged = await bookingRepository.GetOwnBookingsAsync(
            currentUserContext.UserId!.Value, request.SeriesId, request.FromUtc, request.ToUtc, request.Page, request.PageSize, cancellationToken);

        return new PagedResult<GetOwnBookingsResponseItem>(
            paged.Items.Select(booking => new GetOwnBookingsResponseItem(
                booking.Id, booking.ResourceId, booking.StartUtc, booking.EndUtc, booking.Quantity, booking.Status,
                booking.CancelledAtUtc, booking.CancelledByUserId is { } cancelledByUserId && cancelledByUserId != booking.UserId,
                booking.CancellationReason, booking.SeriesId)).ToList(),
            paged.Page,
            paged.PageSize,
            paged.TotalCount);
    }
}
