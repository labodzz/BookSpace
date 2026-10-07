using BookSpace.Application.Security;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Auth;

// See IDeletedTenantAccessGuard's own doc comment for the full design. Reuses
// IUserRepository.FindByIdForAuthenticationAsync - the same pre-tenant-context, filter-bypassing lookup
// AuthenticationService.RefreshAsync already uses - since a withdrawn user's own TenantId still equals the
// (now-deleted) tenant's id, and the ordinary tenant-filtered FindByIdAsync would resolve against the
// CALLER's own ambient tenant claim, not necessarily find anything for a withdrawn user at all.
public sealed class DeletedTenantAccessGuard(IUserRepository userRepository, BookSpace.Application.Bookings.ITenantRepository tenantRepository)
    : IDeletedTenantAccessGuard
{
    public async Task<bool> IsRevokedAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindByIdForAuthenticationAsync(userId, cancellationToken);
        if (user is null || user.Status == UserStatus.Withdrawn)
        {
            return true;
        }

        if (user.TenantId is not { } tenantId)
        {
            // The global SysAdmin - no tenant to have been deleted out from under it.
            return false;
        }

        var tenant = await tenantRepository.FindByIdAsync(tenantId, cancellationToken);
        return tenant is null || tenant.Status == TenantStatus.Deleted;
    }
}
