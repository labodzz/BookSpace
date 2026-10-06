using BookSpace.Application.Auth;
using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using Microsoft.Extensions.Options;

namespace BookSpace.Application.Calendar;

// Deliberately takes no fields from the caller - the owner is always ICurrentUserContext, never
// client-supplied (see tenant-isolation-review). Fails with ConflictException if the caller already has
// an ACTIVE subscription: this command never silently reuses or replaces an existing one, so a caller who
// wants a fresh link for an existing subscription must call RegenerateCalendarSubscriptionCommandRequest
// instead, making "the old link stops working" an explicit, deliberate action rather than a side effect
// of calling Create twice.
public sealed record CreateCalendarSubscriptionCommandRequest : IRequest<CalendarSubscriptionTokenResponse>;

// FeedUrl is the raw token's one and only appearance anywhere in the system - BookSpace never stores it
// (only TokenHash) and never logs it (see docs/calendar-subscription-feed.md, "Token handling and
// logging"). A caller that loses this URL cannot recover it from the API again; the status query
// (GetCalendarSubscriptionStatusQueryRequest) only ever confirms a subscription exists, never the URL
// itself - regenerating is the only way back.
public sealed record CalendarSubscriptionTokenResponse(string FeedUrl, DateTimeOffset CreatedAtUtc);

public sealed class CreateCalendarSubscriptionCommandHandler(
    ICalendarSubscriptionRepository calendarSubscriptionRepository, ICurrentUserContext currentUserContext, IOptions<CalendarFeedOptions> options)
    : IRequestHandler<CreateCalendarSubscriptionCommandRequest, CalendarSubscriptionTokenResponse>
{
    public async Task<CalendarSubscriptionTokenResponse> Handle(CreateCalendarSubscriptionCommandRequest request, CancellationToken cancellationToken)
    {
        var userId = currentUserContext.UserId!.Value;
        var existing = await calendarSubscriptionRepository.FindActiveByUserAsync(userId, cancellationToken);
        if (existing is not null)
        {
            throw new ConflictException(
                "An active calendar subscription already exists. Regenerate it to get a new link.",
                ErrorCodes.CalendarSubscriptionAlreadyExists);
        }

        var rawToken = SecureTokenGenerator.GenerateRawToken();
        var subscription = new CalendarSubscription
        {
            Id = Guid.NewGuid(),
            TenantId = currentUserContext.TenantId!.Value,
            UserId = userId,
            TokenHash = SecureTokenGenerator.HashToken(rawToken),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        await calendarSubscriptionRepository.AddAsync(subscription, cancellationToken);
        await calendarSubscriptionRepository.SaveChangesAsync(cancellationToken);

        return new CalendarSubscriptionTokenResponse(CalendarFeedUrlBuilder.Build(options.Value.PublicBaseUrl!, rawToken), subscription.CreatedAtUtc);
    }
}
