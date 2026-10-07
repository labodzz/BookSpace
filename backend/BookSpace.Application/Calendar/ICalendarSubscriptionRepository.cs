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

    // Bypasses the tenant query filter by design - the one sanctioned caller is
    // DeleteTenantCommandHandler (a SysAdmin action against a tenant that is never its own ambient
    // tenant). Revokes every still-active subscription for the tenant, so an external calendar client
    // polling an old feed URL stops working the moment the tenant is deleted, not merely the next time
    // someone happens to regenerate it. Idempotent: a tenant with no active subscriptions left (a
    // repeated delete attempt) revokes nothing.
    Task RevokeAllActiveForTenantAsync(Guid tenantId, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
