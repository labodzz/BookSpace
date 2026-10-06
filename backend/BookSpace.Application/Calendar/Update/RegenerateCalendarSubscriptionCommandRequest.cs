using BookSpace.Application.Auth;
using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using Microsoft.Extensions.Options;

namespace BookSpace.Application.Calendar;

// Rotates the caller's subscription token: the previous active row (if any) is revoked and a brand new
// one is inserted in the same SaveChangesAsync call - the same "reissue" convention Invitation already
// uses for reissuing an invitation. The OLD feed URL stops working the moment this completes (its
// TokenHash no longer matches any ACTIVE row - see GetCalendarFeedQueryHandler). Requires an existing
// active subscription; a caller with none must call CreateCalendarSubscriptionCommandRequest first -
// Regenerate deliberately does not silently create one, so "nothing to regenerate" is never confused with
// "successfully created."
public sealed record RegenerateCalendarSubscriptionCommandRequest : IRequest<CalendarSubscriptionTokenResponse>;

public sealed class RegenerateCalendarSubscriptionCommandHandler(
    ICalendarSubscriptionRepository calendarSubscriptionRepository, ICurrentUserContext currentUserContext, IOptions<CalendarFeedOptions> options)
    : IRequestHandler<RegenerateCalendarSubscriptionCommandRequest, CalendarSubscriptionTokenResponse>
{
    public async Task<CalendarSubscriptionTokenResponse> Handle(RegenerateCalendarSubscriptionCommandRequest request, CancellationToken cancellationToken)
    {
        var userId = currentUserContext.UserId!.Value;
        var existing = await calendarSubscriptionRepository.FindActiveByUserAsync(userId, cancellationToken)
            ?? throw new NotFoundException(
                "No active calendar subscription to regenerate. Create one first.", ErrorCodes.CalendarSubscriptionNotFound);

        existing.RevokedAtUtc = DateTimeOffset.UtcNow;

        var rawToken = SecureTokenGenerator.GenerateRawToken();
        var replacement = new CalendarSubscription
        {
            Id = Guid.NewGuid(),
            TenantId = currentUserContext.TenantId!.Value,
            UserId = userId,
            TokenHash = SecureTokenGenerator.HashToken(rawToken),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        await calendarSubscriptionRepository.AddAsync(replacement, cancellationToken);
        await calendarSubscriptionRepository.SaveChangesAsync(cancellationToken);

        return new CalendarSubscriptionTokenResponse(CalendarFeedUrlBuilder.Build(options.Value.PublicBaseUrl!, rawToken), replacement.CreatedAtUtc);
    }
}
