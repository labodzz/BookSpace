using BookSpace.Application.Mediator;
using BookSpace.Application.Security;

namespace BookSpace.Application.Calendar;

public sealed record GetCalendarSubscriptionStatusQueryRequest : IRequest<CalendarSubscriptionStatusResponse>;

// Never includes the token or feed URL - once created, there is no way to retrieve the raw token again
// (only its hash is stored - see CalendarSubscription). This exists purely so the frontend can show "you
// have an active subscription" after a page reload without re-prompting Create, and without ever showing
// the token again outside the single moment it was issued.
public sealed record CalendarSubscriptionStatusResponse(bool HasActiveSubscription, DateTimeOffset? CreatedAtUtc);

public sealed class GetCalendarSubscriptionStatusQueryHandler(
    ICalendarSubscriptionRepository calendarSubscriptionRepository, ICurrentUserContext currentUserContext)
    : IRequestHandler<GetCalendarSubscriptionStatusQueryRequest, CalendarSubscriptionStatusResponse>
{
    public async Task<CalendarSubscriptionStatusResponse> Handle(GetCalendarSubscriptionStatusQueryRequest request, CancellationToken cancellationToken)
    {
        var existing = await calendarSubscriptionRepository.FindActiveByUserAsync(currentUserContext.UserId!.Value, cancellationToken);
        return new CalendarSubscriptionStatusResponse(existing is not null, existing?.CreatedAtUtc);
    }
}
