using System.Security.Cryptography;
using System.Text;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using Microsoft.Extensions.Options;

namespace BookSpace.Application.Auth;

public sealed class AuthenticationService(
    IUserRepository userRepository,
    IRefreshTokenRepository refreshTokenRepository,
    IPasswordHasher passwordHasher,
    IJwtTokenGenerator jwtTokenGenerator,
    IOptions<AuthOptions> authOptions) : IAuthenticationService
{
    public async Task<LoginResult> LoginAsync(string email, string password, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindByEmailAsync(email, cancellationToken);
        if (user is null || !passwordHasher.Verify(user.PasswordHash, password))
        {
            return new LoginResult(false, null);
        }

        var roles = await userRepository.GetRolesAsync(user.Id, cancellationToken);
        var tokens = await IssueTokensAsync(user, roles, familyId: Guid.NewGuid(), rotatedFrom: null, cancellationToken);
        return new LoginResult(true, tokens);
    }

    public async Task<RefreshResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var tokenHash = HashToken(refreshToken);
        var existing = await refreshTokenRepository.FindByTokenHashAsync(tokenHash, cancellationToken);

        if (existing is null || existing.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            return new RefreshResult(false, null, false);
        }

        if (existing.RevokedAtUtc is not null || existing.ReplacedByTokenId is not null)
        {
            // This token was already rotated (or explicitly revoked) once - seeing it again means
            // someone other than the legitimate rotation is replaying it. Kill the whole family.
            await refreshTokenRepository.RevokeFamilyAsync(existing.FamilyId, cancellationToken);
            await refreshTokenRepository.SaveChangesAsync(cancellationToken);
            return new RefreshResult(false, null, true);
        }

        var user = await userRepository.FindByIdAsync(existing.UserId, cancellationToken);
        if (user is null)
        {
            return new RefreshResult(false, null, false);
        }

        var roles = await userRepository.GetRolesAsync(user.Id, cancellationToken);
        var tokens = await IssueTokensAsync(user, roles, existing.FamilyId, existing, cancellationToken);
        return new RefreshResult(true, tokens, false);
    }

    private async Task<AuthTokens> IssueTokensAsync(
        User user,
        IReadOnlyCollection<string> roles,
        Guid familyId,
        RefreshToken? rotatedFrom,
        CancellationToken cancellationToken)
    {
        var accessToken = jwtTokenGenerator.GenerateAccessToken(user, roles);

        var rawRefreshToken = GenerateRawRefreshToken();
        var refreshTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(authOptions.Value.RefreshTokenDays);
        var newRefreshToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            FamilyId = familyId,
            TokenHash = HashToken(rawRefreshToken),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = refreshTokenExpiresAtUtc,
        };

        await refreshTokenRepository.AddAsync(newRefreshToken, cancellationToken);

        if (rotatedFrom is not null)
        {
            rotatedFrom.ReplacedByTokenId = newRefreshToken.Id;
            rotatedFrom.RevokedAtUtc = DateTimeOffset.UtcNow;
        }

        await refreshTokenRepository.SaveChangesAsync(cancellationToken);

        return new AuthTokens(accessToken.Token, accessToken.ExpiresAtUtc, rawRefreshToken, refreshTokenExpiresAtUtc);
    }

    private static string GenerateRawRefreshToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    // Refresh tokens are high-entropy random values, not user-chosen passwords, so a plain fast hash
    // (not PBKDF2) is the right tool here - it only needs to stop a raw DB leak from being directly
    // usable, and it needs to be cheap enough to run on every refresh request.
    private static string HashToken(string rawToken) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
}
