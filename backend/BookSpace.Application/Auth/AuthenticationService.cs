using System.Security.Cryptography;
using System.Text;
using BookSpace.Application.Common;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookSpace.Application.Auth;

public sealed class AuthenticationService(
    IUserRepository userRepository,
    IRefreshTokenRepository refreshTokenRepository,
    IPasswordHasher passwordHasher,
    IJwtTokenGenerator jwtTokenGenerator,
    IOptions<AuthOptions> authOptions,
    ILogger<AuthenticationService> logger) : IAuthenticationService
{
    public async Task<LoginResponse> LoginAsync(string email, string password, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindByEmailAsync(email, cancellationToken);
        if (user is null || !passwordHasher.Verify(user.PasswordHash, password))
        {
            // Information, not Error - a wrong password is expected user error, same reasoning as
            // ValidationExceptionHandler. Only the email is logged, never the attempted password.
            logger.LogInformation("Login failed for {Email}: invalid credentials", email);
            return new LoginResponse(false, null, ErrorCodes.AuthInvalidCredentials);
        }

        var roles = await userRepository.GetRolesAsync(user.Id, cancellationToken);
        var tokens = await IssueTokensAsync(user, roles, familyId: Guid.NewGuid(), rotatedFrom: null, cancellationToken);
        return new LoginResponse(true, tokens);
    }

    public async Task<RefreshResponse> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var tokenHash = HashToken(refreshToken);
        var existing = await refreshTokenRepository.FindByTokenHashAsync(tokenHash, cancellationToken);

        if (existing is null)
        {
            return new RefreshResponse(false, null, false, ErrorCodes.AuthInvalidRefreshToken);
        }

        // Checked BEFORE expiry, deliberately: a token that was already rotated/revoked is a reuse
        // signal regardless of whether it has ALSO since expired - an attacker who waits out a stolen
        // token's own expiry window before replaying it must not get a quieter "just invalid" outcome
        // that skips family revocation.
        if (existing.RevokedAtUtc is not null || existing.ReplacedByTokenId is not null)
        {
            // This token was already rotated (or explicitly revoked) once - seeing it again means
            // someone other than the legitimate rotation is replaying it. Kill the whole family.
            // Warning, not Information - unlike a wrong password, this is a concrete signal of likely
            // refresh token theft, not routine expected user error.
            logger.LogWarning(
                "Refresh token reuse detected for user {UserId}; revoking token family {FamilyId}",
                existing.UserId,
                existing.FamilyId);
            await refreshTokenRepository.RevokeFamilyAsync(existing.FamilyId, cancellationToken);
            await refreshTokenRepository.SaveChangesAsync(cancellationToken);
            return new RefreshResponse(false, null, true, ErrorCodes.AuthRefreshReuseDetected);
        }

        if (existing.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            return new RefreshResponse(false, null, false, ErrorCodes.AuthInvalidRefreshToken);
        }

        var user = await userRepository.FindByIdForAuthenticationAsync(existing.UserId, cancellationToken);
        if (user is null)
        {
            return new RefreshResponse(false, null, false, ErrorCodes.AuthInvalidRefreshToken);
        }

        var roles = await userRepository.GetRolesAsync(user.Id, cancellationToken);

        try
        {
            var tokens = await IssueTokensAsync(user, roles, existing.FamilyId, existing, cancellationToken);
            return new RefreshResponse(true, tokens, false);
        }
        catch (ConflictException)
        {
            // Another concurrent refresh request already consumed (rotated) this same token first -
            // the RefreshToken row's RowVersion no longer matches what this request read, so its own
            // rotation was rolled back entirely (including the new child token it tried to insert).
            //
            // Deliberately NOT routed through reuse detection (no RevokeFamilyAsync call here): reuse
            // detection above is keyed on what THIS request itself read (RevokedAtUtc/ReplacedByTokenId
            // already set at the moment of the read) - a token presented after it was ALREADY, durably,
            // known-consumed. This request read a genuinely still-valid token; it only lost a race to
            // consume it that unfolded entirely after that read. Revoking the whole family here would
            // also kill the WINNING request's brand-new token, punishing the legitimate caller who
            // actually won for a race their own losing attempt caused - disproportionate for what is
            // most plausibly a client-side double-fire (retry, duplicate tab), not token theft.
            return new RefreshResponse(false, null, false, ErrorCodes.AuthInvalidRefreshToken);
        }
    }

    // Revokes only the calling session's own token family (the lineage descended from the presented
    // refresh token) - not every session the user has open elsewhere. This is "log out this device,"
    // not "log out everywhere"; the latter remains an open question (see docs/authentication.md).
    // Reuses RevokeFamilyAsync, the same mechanism reuse detection uses above - a voluntary logout and
    // a detected theft both end with "every token in this lineage stops working."
    //
    // Deliberately revokes on ANY known token for the family, active or already-rotated - unlike
    // RefreshAsync's reuse detection, this endpoint only ever runs from an explicit user action (a real
    // "Log out" click), never automatically: the frontend's interceptor reacts to a failed refresh with
    // AuthService.clearExpiredSession() (local-only, no server call), not this endpoint. That means a
    // tab that's fallen behind - still holding a pre-rotation token because it missed a BroadcastChannel
    // update, or was asleep through a sibling tab's refresh - can still authoritatively end "this
    // device's" session via logout, which is exactly what a deliberate Logout click should do.
    public async Task<LogoutResponse> LogoutAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var tokenHash = HashToken(refreshToken);
        var existing = await refreshTokenRepository.FindByTokenHashAsync(tokenHash, cancellationToken);
        if (existing is not null)
        {
            await refreshTokenRepository.RevokeFamilyAsync(existing.FamilyId, cancellationToken);
            await refreshTokenRepository.SaveChangesAsync(cancellationToken);
        }

        return new LogoutResponse();
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
