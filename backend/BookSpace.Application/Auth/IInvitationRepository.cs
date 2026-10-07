using BookSpace.Domain.Entities;

namespace BookSpace.Application.Auth;

public interface IInvitationRepository
{
    Task AddAsync(Invitation invitation, CancellationToken cancellationToken);

    // Bypasses the tenant query filter, same reasoning as IUserRepository.FindByEmailAsync/
    // FindByIdForAuthenticationAsync: accepting an invitation happens with no tenant context yet - the
    // caller presents only a raw token, not a valid access token. Only
    // AuthenticationService.AcceptInvitationAsync should call this.
    Task<Invitation?> FindByTokenHashForAcceptanceAsync(string tokenHash, CancellationToken cancellationToken);

    // Tenant-scoped (via the query filter) lookup of the current still-active invitation for a given
    // user, if any - "active" meaning not yet accepted and not yet revoked (expiry is checked by the
    // caller against ExpiresAtUtc, not filtered out here, since InviteUserCommandHandler's reissue path
    // needs to revoke an expired-but-not-yet-cleaned-up row exactly the same as a live one).
    Task<Invitation?> FindActiveByUserIdAsync(Guid userId, CancellationToken cancellationToken);

    // Explicitly bypasses the tenant query filter and re-applies BOTH tenantId and userId in the same
    // Where clause - the Invitation-side counterpart to IUserRepository.FindByIdForTenantAdministrationAsync,
    // for the identical reason (a SysAdmin's own ambient tenant is never the tenant being onboarded). Only
    // ResendTenantAdminInvitationCommandHandler should call this.
    Task<Invitation?> FindActiveByUserIdForTenantAdministrationAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken);

    // Bypasses the tenant query filter by design - the one sanctioned caller is
    // DeleteTenantCommandHandler (a SysAdmin action against a tenant that is never its own ambient
    // tenant). Revokes every still-active (not yet accepted, not yet revoked) invitation for the tenant,
    // so a pending invitation link can never be accepted after its tenant has been soft-deleted even if
    // the underlying User row's own status hasn't been checked yet - see AuthenticationService.
    // Idempotent: a tenant with no active invitations left (a repeated delete attempt) revokes nothing.
    Task RevokeAllActiveForTenantAsync(Guid tenantId, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
