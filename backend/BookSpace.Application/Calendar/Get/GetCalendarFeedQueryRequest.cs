using BookSpace.Application.Auth;
using BookSpace.Application.Bookings;
using BookSpace.Application.Common;
using BookSpace.Domain.Enums;
using BookSpace.Application.Mediator;
using Microsoft.Extensions.Options;

namespace BookSpace.Application.Calendar;

// Token arrives as a query-string parameter (see CalendarFeedController), never a route-path segment: a
// route segment would appear in Serilog's default request-logging summary line (HTTP {Method}
// {RequestPath} responded ...), which logs RequestPath but never QueryString - a query parameter is the
// one placement that keeps the raw token out of that log line by construction. See
// docs/calendar-subscription-feed.md ("Token handling and logging") for the full reasoning and its limits.
public sealed record GetCalendarFeedQueryRequest(string? Token) : IRequest<CalendarFeedResponse>;

public sealed record CalendarFeedResponse(string IcsContent);

public sealed class GetCalendarFeedQueryHandler(
    ICalendarSubscriptionRepository calendarSubscriptionRepository,
    IUserRepository userRepository,
    ITenantRepository tenantRepository,
    IBookingRepository bookingRepository,
    IOptions<CalendarFeedOptions> options)
    : IRequestHandler<GetCalendarFeedQueryRequest, CalendarFeedResponse>
{
    public async Task<CalendarFeedResponse> Handle(GetCalendarFeedQueryRequest request, CancellationToken cancellationToken)
    {
        // Every one of these failure causes - a missing token, an unknown hash, a revoked subscription, a
        // deactivated user, or a suspended tenant - collapses into the SAME generic NotFoundException, the
        // same "don't leak account state" philosophy login/refresh already apply to their own failures
        // (see docs/authentication.md): an external caller holding a dead/guessed token learns nothing
        // about WHY it no longer works.
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            throw new NotFoundException("Calendar subscription not found.", ErrorCodes.CalendarSubscriptionNotFound);
        }

        var tokenHash = SecureTokenGenerator.HashToken(request.Token);
        var subscription = await calendarSubscriptionRepository.FindActiveByTokenHashAsync(tokenHash, cancellationToken)
            ?? throw new NotFoundException("Calendar subscription not found.", ErrorCodes.CalendarSubscriptionNotFound);

        // Bypasses the tenant filter the same way AuthenticationService.RefreshAsync does (no tenant
        // context yet) - see IUserRepository.FindByIdForAuthenticationAsync. A deactivated member's old
        // subscription link must stop working the moment they are, exactly like it stops working for
        // login/refresh - never a separate, forgotten enforcement path.
        var user = await userRepository.FindByIdForAuthenticationAsync(subscription.UserId, cancellationToken);
        if (user is null || user.Status != UserStatus.Active)
        {
            throw new NotFoundException("Calendar subscription not found.", ErrorCodes.CalendarSubscriptionNotFound);
        }

        var tenant = await tenantRepository.FindByIdAsync(subscription.TenantId, cancellationToken);
        if (tenant is null || tenant.Status != TenantStatus.Active)
        {
            throw new NotFoundException("Calendar subscription not found.", ErrorCodes.CalendarSubscriptionNotFound);
        }

        var now = DateTimeOffset.UtcNow;
        var windowStartUtc = now.AddDays(-options.Value.PastWindowDays);
        var windowEndUtc = now.AddDays(options.Value.FutureWindowDays);

        var bookings = await bookingRepository.GetForCalendarFeedAsync(
            subscription.TenantId, subscription.UserId, windowStartUtc, windowEndUtc, cancellationToken);

        return new CalendarFeedResponse(IcsCalendarWriter.Write(bookings, now));
    }
}
