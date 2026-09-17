using BookSpace.Application.Common;
using BookSpace.Domain.Entities;

namespace BookSpace.Application.Auth;

public interface IUserRepository
{
    Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken);
    Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken);

    // Bypasses the tenant query filter, same reasoning as FindByEmailAsync: refresh (like login)
    // happens with no tenant context yet - the incoming request carries a refresh token, not a valid
    // access token, so ICurrentUserContext.TenantId is null. Only AuthenticationService.RefreshAsync
    // should call this; every other lookup-by-id (e.g. validating an approver assignment target) must
    // keep using the tenant-filtered FindByIdAsync above.
    Task<User?> FindByIdForAuthenticationAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetRolesAsync(Guid userId, CancellationToken cancellationToken);

    // Relies on the DbContext's global tenant query filter to scope results - callers must not
    // add their own TenantId filter on top of this. Paged (unlike ResourceType's GetAllAsync): a
    // tenant's user roster grows roughly linearly with headcount and is never pruned, unlike small,
    // human-curated lists such as resource types.
    Task<PagedResult<User>> GetPagedAsync(int page, int pageSize, CancellationToken cancellationToken);
}
