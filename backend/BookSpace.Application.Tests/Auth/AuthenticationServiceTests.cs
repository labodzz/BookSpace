using BookSpace.Application.Auth;
using BookSpace.Application.Common;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Auth;

public sealed class AuthenticationServiceTests
{
    private readonly Mock<IUserRepository> _userRepository = new();
    private readonly Mock<IRefreshTokenRepository> _refreshTokenRepository = new();
    private readonly Mock<IPasswordHasher> _passwordHasher = new();
    private readonly Mock<IJwtTokenGenerator> _jwtTokenGenerator = new();
    private readonly AuthOptions _authOptions = new()
    {
        Issuer = "bookspace-tests",
        Audience = "bookspace-tests",
        SigningKey = "unit-test-signing-key",
        AccessTokenMinutes = 15,
        RefreshTokenDays = 14,
    };

    private AuthenticationService CreateSut() => new(
        _userRepository.Object,
        _refreshTokenRepository.Object,
        _passwordHasher.Object,
        _jwtTokenGenerator.Object,
        Options.Create(_authOptions),
        NullLogger<AuthenticationService>.Instance);

    private static User CreateUser(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        FirstName = "Test",
        LastName = "User",
        Email = "test.user@bookspace.test",
        PasswordHash = "stored-hash",
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task LoginAsync_WithValidCredentials_ReturnsSucceededResultWithTokens()
    {
        var user = CreateUser();
        _userRepository.Setup(r => r.FindByEmailAsync(user.Email, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        _passwordHasher.Setup(h => h.Verify(user.PasswordHash, "correct-password")).Returns(true);
        _userRepository.Setup(r => r.GetRolesAsync(user.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)["Member"]);
        _jwtTokenGenerator.Setup(g => g.GenerateAccessToken(user, It.IsAny<IReadOnlyCollection<string>>()))
            .Returns(new AccessToken("access-token", DateTimeOffset.UtcNow.AddMinutes(15)));

        var sut = CreateSut();
        var result = await sut.LoginAsync(user.Email, "correct-password", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Tokens);
        Assert.Equal("access-token", result.Tokens!.AccessToken);
        Assert.False(string.IsNullOrWhiteSpace(result.Tokens.RefreshToken));

        _refreshTokenRepository.Verify(r => r.AddAsync(
            It.Is<RefreshToken>(t => t.UserId == user.Id && t.FamilyId != Guid.Empty),
            It.IsAny<CancellationToken>()), Times.Once);
        _refreshTokenRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LoginAsync_WithUnknownEmail_ReturnsFailureWithoutIssuingTokens()
    {
        _userRepository.Setup(r => r.FindByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        var sut = CreateSut();
        var result = await sut.LoginAsync("missing@bookspace.test", "any-password", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.Tokens);
        _passwordHasher.Verify(h => h.Verify(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _refreshTokenRepository.Verify(r => r.AddAsync(It.IsAny<RefreshToken>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LoginAsync_WithWrongPassword_ReturnsFailureWithoutIssuingTokens()
    {
        var user = CreateUser();
        _userRepository.Setup(r => r.FindByEmailAsync(user.Email, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        _passwordHasher.Setup(h => h.Verify(user.PasswordHash, "wrong-password")).Returns(false);

        var sut = CreateSut();
        var result = await sut.LoginAsync(user.Email, "wrong-password", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.Tokens);
        _refreshTokenRepository.Verify(r => r.AddAsync(It.IsAny<RefreshToken>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RefreshAsync_WithValidToken_RotatesTokenAndReturnsNewTokens()
    {
        var user = CreateUser();
        var familyId = Guid.NewGuid();
        var existingToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            FamilyId = familyId,
            TokenHash = "existing-hash",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(13),
        };

        _refreshTokenRepository.Setup(r => r.FindByTokenHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingToken);
        _userRepository.Setup(r => r.FindByIdForAuthenticationAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _userRepository.Setup(r => r.GetRolesAsync(user.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)["Member"]);
        _jwtTokenGenerator.Setup(g => g.GenerateAccessToken(user, It.IsAny<IReadOnlyCollection<string>>()))
            .Returns(new AccessToken("new-access-token", DateTimeOffset.UtcNow.AddMinutes(15)));

        var sut = CreateSut();
        var result = await sut.RefreshAsync("raw-refresh-token", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(result.ReuseDetected);
        Assert.Equal("new-access-token", result.Tokens!.AccessToken);
        Assert.NotNull(existingToken.ReplacedByTokenId);
        Assert.NotNull(existingToken.RevokedAtUtc);
        _refreshTokenRepository.Verify(r => r.RevokeFamilyAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // The load-bearing invariant reuse-detection depends on entirely: every token descended from one
    // login keeps the SAME FamilyId through every rotation, so RevokeFamilyAsync(familyId) reaches the
    // whole chain. No test previously captured the newly-issued child token during a rotation (as
    // opposed to a fresh login) and asserted its FamilyId - a regression that passed Guid.NewGuid()
    // instead of the parent's FamilyId here would silently defeat family-wide revocation and every
    // existing test would still pass.
    [Fact]
    public async Task RefreshAsync_WithValidToken_TheNewChildTokenKeepsTheParentsFamilyId()
    {
        var user = CreateUser();
        var familyId = Guid.NewGuid();
        var existingToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            FamilyId = familyId,
            TokenHash = "existing-hash",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(13),
        };
        _refreshTokenRepository.Setup(r => r.FindByTokenHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingToken);
        _userRepository.Setup(r => r.FindByIdForAuthenticationAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _userRepository.Setup(r => r.GetRolesAsync(user.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)["Member"]);
        _jwtTokenGenerator.Setup(g => g.GenerateAccessToken(user, It.IsAny<IReadOnlyCollection<string>>()))
            .Returns(new AccessToken("new-access-token", DateTimeOffset.UtcNow.AddMinutes(15)));
        RefreshToken? capturedChild = null;
        _refreshTokenRepository
            .Setup(r => r.AddAsync(It.IsAny<RefreshToken>(), It.IsAny<CancellationToken>()))
            .Callback<RefreshToken, CancellationToken>((token, _) => capturedChild = token)
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        await sut.RefreshAsync("raw-refresh-token", CancellationToken.None);

        Assert.NotNull(capturedChild);
        Assert.Equal(familyId, capturedChild!.FamilyId);
        Assert.NotEqual(existingToken.Id, capturedChild.Id); // a genuinely new token, not the same row
    }

    [Fact]
    public async Task RefreshAsync_WithExpiredToken_ReturnsFailureWithoutReuseDetection()
    {
        var existingToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            FamilyId = Guid.NewGuid(),
            TokenHash = "existing-hash",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-20),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
        };
        _refreshTokenRepository.Setup(r => r.FindByTokenHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingToken);

        var sut = CreateSut();
        var result = await sut.RefreshAsync("raw-refresh-token", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.ReuseDetected);
        Assert.Null(result.Tokens);
        _refreshTokenRepository.Verify(r => r.RevokeFamilyAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RefreshAsync_WithUnknownTokenHash_ReturnsFailure()
    {
        _refreshTokenRepository.Setup(r => r.FindByTokenHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((RefreshToken?)null);

        var sut = CreateSut();
        var result = await sut.RefreshAsync("unknown-token", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.ReuseDetected);
    }

    [Fact]
    public async Task RefreshAsync_WithAlreadyRotatedToken_RevokesFamilyAndReportsReuseDetected()
    {
        var familyId = Guid.NewGuid();
        var existingToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            FamilyId = familyId,
            TokenHash = "already-rotated-hash",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-2),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(12),
            ReplacedByTokenId = Guid.NewGuid(),
        };
        _refreshTokenRepository.Setup(r => r.FindByTokenHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingToken);

        var sut = CreateSut();
        var result = await sut.RefreshAsync("stolen-token", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(result.ReuseDetected);
        Assert.Null(result.Tokens);
        _refreshTokenRepository.Verify(r => r.RevokeFamilyAsync(familyId, It.IsAny<CancellationToken>()), Times.Once);
        _refreshTokenRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RefreshAsync_WithExplicitlyRevokedToken_RevokesFamilyAndReportsReuseDetected()
    {
        var familyId = Guid.NewGuid();
        var existingToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            FamilyId = familyId,
            TokenHash = "revoked-hash",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-2),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(12),
            RevokedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        _refreshTokenRepository.Setup(r => r.FindByTokenHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingToken);

        var sut = CreateSut();
        var result = await sut.RefreshAsync("revoked-token", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(result.ReuseDetected);
        _refreshTokenRepository.Verify(r => r.RevokeFamilyAsync(familyId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RefreshAsync_WithAlreadyRotatedTokenThatHasSinceExpired_StillRevokesFamilyAndReportsReuseDetected()
    {
        // Reuse detection must fire even when the presented token is ALSO now expired - an attacker
        // who waits out a stolen token's own expiry window before replaying it must not get a quieter
        // "just invalid" outcome that skips family revocation. This is the precedence bug fixed this
        // session: expiry was previously checked before reuse, silently swallowing this exact case.
        var familyId = Guid.NewGuid();
        var existingToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            FamilyId = familyId,
            TokenHash = "already-rotated-and-expired-hash",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-30),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(-16),
            ReplacedByTokenId = Guid.NewGuid(),
        };
        _refreshTokenRepository.Setup(r => r.FindByTokenHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingToken);

        var sut = CreateSut();
        var result = await sut.RefreshAsync("stolen-and-now-expired-token", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(result.ReuseDetected);
        Assert.Null(result.Tokens);
        _refreshTokenRepository.Verify(r => r.RevokeFamilyAsync(familyId, It.IsAny<CancellationToken>()), Times.Once);
        _refreshTokenRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RefreshAsync_WhenRotationLosesAConcurrencyRace_ReturnsFailureWithoutReuseDetection()
    {
        // Simulates a second concurrent refresh request losing the race: IssueTokensAsync's
        // SaveChangesAsync throws ConflictException (translated from a RowVersion mismatch by
        // SaveChangesHandlingConflictsAsync) because another request already rotated this exact token
        // first. The loser must fail cleanly, not be mistaken for a reuse/theft signal.
        var user = CreateUser();
        var existingToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            FamilyId = Guid.NewGuid(),
            TokenHash = "raced-hash",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(13),
        };
        _refreshTokenRepository.Setup(r => r.FindByTokenHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingToken);
        _userRepository.Setup(r => r.FindByIdForAuthenticationAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _userRepository.Setup(r => r.GetRolesAsync(user.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)["Member"]);
        _jwtTokenGenerator.Setup(g => g.GenerateAccessToken(user, It.IsAny<IReadOnlyCollection<string>>()))
            .Returns(new AccessToken("new-access-token", DateTimeOffset.UtcNow.AddMinutes(15)));
        _refreshTokenRepository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConflictException("The record was modified by another request. Reload and try again."));

        var sut = CreateSut();
        var result = await sut.RefreshAsync("raced-token", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.ReuseDetected);
        Assert.Null(result.Tokens);
    }

    [Fact]
    public async Task LogoutAsync_WithKnownToken_RevokesItsFamily()
    {
        var familyId = Guid.NewGuid();
        var existingToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            FamilyId = familyId,
            TokenHash = "known-hash",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(13),
        };
        _refreshTokenRepository.Setup(r => r.FindByTokenHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingToken);

        var sut = CreateSut();
        await sut.LogoutAsync("raw-refresh-token", CancellationToken.None);

        _refreshTokenRepository.Verify(r => r.RevokeFamilyAsync(familyId, It.IsAny<CancellationToken>()), Times.Once);
        _refreshTokenRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // A logout for a token the server doesn't recognize (already expired and pruned, already revoked,
    // or simply never valid) must not throw or leak that distinction - it's still a "successful" logout
    // from the caller's perspective, since the client is going to clear its own local state regardless.
    [Fact]
    public async Task LogoutAsync_WithUnknownToken_DoesNothingAndStillReturnsAResponse()
    {
        _refreshTokenRepository.Setup(r => r.FindByTokenHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((RefreshToken?)null);

        var sut = CreateSut();
        var result = await sut.LogoutAsync("unknown-token", CancellationToken.None);

        Assert.NotNull(result);
        _refreshTokenRepository.Verify(r => r.RevokeFamilyAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _refreshTokenRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RefreshAsync_WhenUserNoLongerExists_ReturnsFailure()
    {
        var existingToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            FamilyId = Guid.NewGuid(),
            TokenHash = "orphaned-hash",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(13),
        };
        _refreshTokenRepository.Setup(r => r.FindByTokenHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingToken);
        _userRepository.Setup(r => r.FindByIdForAuthenticationAsync(existingToken.UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        var sut = CreateSut();
        var result = await sut.RefreshAsync("orphaned-token", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.ReuseDetected);
        Assert.Null(result.Tokens);
    }
}
