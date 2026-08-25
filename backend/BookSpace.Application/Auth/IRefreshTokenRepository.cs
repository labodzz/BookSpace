using BookSpace.Domain.Entities;

namespace BookSpace.Application.Auth;

public interface IRefreshTokenRepository
{
    Task AddAsync(RefreshToken token, CancellationToken cancellationToken);
    Task<RefreshToken?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken);

    // Revokes every still-active token in the family - the response to detected reuse.
    Task RevokeFamilyAsync(Guid familyId, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
