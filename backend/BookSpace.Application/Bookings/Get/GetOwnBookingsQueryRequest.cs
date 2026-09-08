using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Application.Security;
using BookSpace.Domain.Enums;
using FluentValidation;

namespace BookSpace.Application.Bookings;

// SeriesId, when given, narrows to that recurring series' own occurrences only.
public sealed record GetOwnBookingsQueryRequest(int Page = 1, int PageSize = 20, Guid? SeriesId = null) : IRequest<PagedResult<GetOwnBookingsResponseItem>>;

public sealed record GetOwnBookingsResponseItem(
    Guid Id, Guid ResourceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Quantity, BookingStatus Status);

public sealed class GetOwnBookingsQueryRequestValidator : AbstractValidator<GetOwnBookingsQueryRequest>
{
    public GetOwnBookingsQueryRequestValidator()
    {
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, 100);
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
            currentUserContext.UserId!.Value, request.SeriesId, request.Page, request.PageSize, cancellationToken);

        return new PagedResult<GetOwnBookingsResponseItem>(
            paged.Items.Select(booking => new GetOwnBookingsResponseItem(
                booking.Id, booking.ResourceId, booking.StartUtc, booking.EndUtc, booking.Quantity, booking.Status)).ToList(),
            paged.Page,
            paged.PageSize,
            paged.TotalCount);
    }
}
