using BookSpace.Application.Auth;
using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

internal sealed class RefreshTokenRepository(BookSpaceDbContext dbContext) : IRefreshTokenRepository
{
    public async Task AddAsync(RefreshToken token, CancellationToken cancellationToken) =>
        await dbContext.RefreshTokens.AddAsync(token, cancellationToken);

    public Task<RefreshToken?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken) =>
        dbContext.RefreshTokens.FirstOrDefaultAsync(token => token.TokenHash == tokenHash, cancellationToken);

    public async Task RevokeFamilyAsync(Guid familyId, CancellationToken cancellationToken)
    {
        var activeFamilyTokens = await dbContext.RefreshTokens
            .Where(token => token.FamilyId == familyId && token.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        foreach (var token in activeFamilyTokens)
        {
            token.RevokedAtUtc = now;
        }
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        dbContext.SaveChangesHandlingConflictsAsync(cancellationToken);
}
