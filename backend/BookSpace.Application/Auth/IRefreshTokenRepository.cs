using BookSpace.Domain.Entities;

namespace BookSpace.Application.Auth;

public interface IRefreshTokenRepository
{
    Task AddAsync(RefreshToken token, CancellationToken cancellationToken);
    Task<RefreshToken?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken);

    // Revokes every still-active token in the family - the response to detected reuse.
    Task RevokeFamilyAsync(Guid familyId, CancellationToken cancellationToken);

    // Revokes every still-active token for every listed user, across every family - unlike
    // RevokeFamilyAsync (one login session), this is for a tenant-wide cutoff (see
    // DeleteTenantCommandHandler, the one sanctioned caller) where every session of every user the
    // tenant ever had must stop being refreshable. RefreshToken carries no TenantId of its own (see
    // RefreshToken.cs), so the caller supplies exactly the user ids that belong to that tenant - typically
    // IUserRepository.WithdrawAllForTenantAsync's own return value. Idempotent: a user with no active
    // tokens left revokes nothing for them.
    Task RevokeAllForUsersAsync(IReadOnlyList<Guid> userIds, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
