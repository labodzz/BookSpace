using BookSpace.Application.Common;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookSpace.Application.Auth;

public sealed class AuthenticationService(
    IUserRepository userRepository,
    IRefreshTokenRepository refreshTokenRepository,
    IInvitationRepository invitationRepository,
    IPasswordHasher passwordHasher,
    IJwtTokenGenerator jwtTokenGenerator,
    IOptions<AuthOptions> authOptions,
    ILogger<AuthenticationService> logger) : IAuthenticationService
{
    public async Task<LoginResponse> LoginAsync(string email, string password, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindByEmailAsync(email, cancellationToken);
        // Short-circuited via || before Verify is even called: a deactivated (or still-Invited, never
        // yet given a real password) user presenting their genuinely-correct old password must still
        // fail without spending a PBKDF2 verification - and either way, the identical generic
        // "invalid credentials" outcome as an unknown email or a wrong password, never revealing which.
        if (user is null || user.Status != UserStatus.Active || !passwordHasher.Verify(user.PasswordHash, password))
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
        var tokenHash = SecureTokenGenerator.HashToken(refreshToken);
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
        var tokenHash = SecureTokenGenerator.HashToken(refreshToken);
        var existing = await refreshTokenRepository.FindByTokenHashAsync(tokenHash, cancellationToken);
        if (existing is not null)
        {
            await refreshTokenRepository.RevokeFamilyAsync(existing.FamilyId, cancellationToken);
            await refreshTokenRepository.SaveChangesAsync(cancellationToken);
        }

        return new LogoutResponse();
    }

    // Every reason a presented token is unusable (unknown, already accepted, revoked, expired, or the
    // underlying User somehow no longer Invited - e.g. an admin deactivated the pending invite
    // independently of the invitation record) collapses into the identical generic outcome - same
    // "don't reveal account state" philosophy LoginAsync/RefreshAsync already apply to their own
    // failures. See docs/user-administration.md §4.
    public async Task<AcceptInvitationResponse> AcceptInvitationAsync(string token, string password, CancellationToken cancellationToken)
    {
        var tokenHash = SecureTokenGenerator.HashToken(token);
        var invitation = await invitationRepository.FindByTokenHashForAcceptanceAsync(tokenHash, cancellationToken);

        if (invitation is null || invitation.AcceptedAtUtc is not null || invitation.RevokedAtUtc is not null
            || invitation.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            return new AcceptInvitationResponse(false, ErrorCodes.InvitationInvalid);
        }

        // Pre-tenant-context lookup, same reasoning as FindByIdForAuthenticationAsync's own doc comment
        // - the caller presents only a raw token, no valid access token, so no tenant is known yet.
        var user = await userRepository.FindByIdForAuthenticationAsync(invitation.UserId, cancellationToken);
        if (user is null || user.Status != UserStatus.Invited)
        {
            return new AcceptInvitationResponse(false, ErrorCodes.InvitationInvalid);
        }

        user.PasswordHash = passwordHasher.Hash(password);
        user.Status = UserStatus.Active;
        invitation.AcceptedAtUtc = DateTimeOffset.UtcNow;

        try
        {
            // One SaveChangesAsync call persists both entities: the same DbContext instance backs both
            // userRepository and invitationRepository within this request, so the User change tracked
            // above is included here too, without a separate userRepository.SaveChangesAsync() call -
            // the same atomic-write pattern IssueTokensAsync already uses for its own two-entity write.
            await invitationRepository.SaveChangesAsync(cancellationToken);
        }
        catch (ConflictException)
        {
            // A concurrent acceptance of the same token already won - Invitation.RowVersion no longer
            // matches what this request read. Mirrors RefreshAsync's own catch (ConflictException)
            // handling of a concurrent-rotation loss: collapsed into the same generic failure, not a
            // distinct error, so a losing racer learns nothing more than "this token didn't work."
            return new AcceptInvitationResponse(false, ErrorCodes.InvitationInvalid);
        }

        return new AcceptInvitationResponse(true);
    }

    private async Task<AuthTokens> IssueTokensAsync(
        User user,
        IReadOnlyCollection<string> roles,
        Guid familyId,
        RefreshToken? rotatedFrom,
        CancellationToken cancellationToken)
    {
        var accessToken = jwtTokenGenerator.GenerateAccessToken(user, roles);

        var rawRefreshToken = SecureTokenGenerator.GenerateRawToken();
        var refreshTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(authOptions.Value.RefreshTokenDays);
        var newRefreshToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            FamilyId = familyId,
            TokenHash = SecureTokenGenerator.HashToken(rawRefreshToken),
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
}
