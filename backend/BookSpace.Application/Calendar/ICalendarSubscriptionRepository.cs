using BookSpace.Domain.Entities;

namespace BookSpace.Application.Calendar;

public interface ICalendarSubscriptionRepository
{
    // Relies on the ambient tenant query filter - used by the authenticated Create/Regenerate/Revoke/
    // status handlers, which always run with a real ICurrentUserContext.
    Task<CalendarSubscription?> FindActiveByUserAsync(Guid userId, CancellationToken cancellationToken);

    // Explicitly bypasses the tenant query filter - same reasoning as UserRepository.FindByEmailAsync/
    // FindByIdForAuthenticationAsync: the feed endpoint is anonymous (an external calendar client
    // presents a subscription token, never a BookSpace JWT), so ICurrentUserContext.TenantId is null for
    // this whole request and the ambient filter would otherwise match nothing. Returns only an ACTIVE
    // (RevokedAtUtc IS NULL) row - a revoked or never-existing token must look identical to the caller
    // (see GetCalendarFeedQueryHandler, which turns either outcome into the same generic NotFoundException).
    Task<CalendarSubscription?> FindActiveByTokenHashAsync(string tokenHash, CancellationToken cancellationToken);

    Task AddAsync(CalendarSubscription subscription, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
