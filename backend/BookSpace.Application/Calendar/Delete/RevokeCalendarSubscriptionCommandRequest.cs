using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Application.Security;

namespace BookSpace.Application.Calendar;

// Ends the caller's own active subscription - the feed URL stops working immediately (its TokenHash no
// longer matches any ACTIVE row). Requires an existing active subscription; throws NotFoundException if
// there is nothing active to revoke, the same "precise precondition, not a silent no-op" shape every other
// delete/revoke command in this codebase follows.
public sealed record RevokeCalendarSubscriptionCommandRequest : IRequest<Unit>;

public sealed class RevokeCalendarSubscriptionCommandHandler(ICalendarSubscriptionRepository calendarSubscriptionRepository, ICurrentUserContext currentUserContext)
    : IRequestHandler<RevokeCalendarSubscriptionCommandRequest, Unit>
{
    public async Task<Unit> Handle(RevokeCalendarSubscriptionCommandRequest request, CancellationToken cancellationToken)
    {
        var existing = await calendarSubscriptionRepository.FindActiveByUserAsync(currentUserContext.UserId!.Value, cancellationToken)
            ?? throw new NotFoundException("No active calendar subscription to revoke.", ErrorCodes.CalendarSubscriptionNotFound);

        existing.RevokedAtUtc = DateTimeOffset.UtcNow;
        await calendarSubscriptionRepository.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }
}
