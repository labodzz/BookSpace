namespace BookSpace.Application.Security;

// What every tenant-scoped query and every authorization check reads from. TenantId is null for an
// unauthenticated request (login, refresh) - the DbContext's global query filter treats that as
// unrestricted, which is exactly right since those two flows must be able to find a user before any
// tenant is known.
public interface ICurrentUserContext
{
    Guid? UserId { get; }
    Guid? TenantId { get; }
    IReadOnlyCollection<string> Roles { get; }
}
