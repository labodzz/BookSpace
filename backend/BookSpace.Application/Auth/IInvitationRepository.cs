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

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
