using BookSpace.Application.Auth;
using BookSpace.Application.Bookings;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Auth;

// See IDeletedTenantAccessGuard's own doc comment for why this check exists separately from
// AuthenticationService's own IsUsableAsync, and why it is deliberately narrower than "any non-Active
// status" - Suspended tenants and ordinarily-Inactive users must NOT be revoked here (see
// SuspendTenantCommandHandler's own doc comment on the accepted, bounded access-token window), only
// Withdrawn users and Deleted tenants - the two states a tenant soft-delete itself writes.
public sealed class DeletedTenantAccessGuardTests
{
    private readonly Mock<IUserRepository> _userRepository = new();
    private readonly Mock<ITenantRepository> _tenantRepository = new();

    private DeletedTenantAccessGuard CreateSut() => new(_userRepository.Object, _tenantRepository.Object);

    private static User CreateUser(Guid? tenantId, UserStatus status) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, FirstName = "A", LastName = "B",
        Email = "a@b.test", PasswordHash = "x", Status = status, CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private static Tenant CreateTenant(Guid id, TenantStatus status) =>
        new() { Id = id, Name = "Acme", DefaultTimeZoneId = "UTC", Status = status, CreatedAtUtc = DateTimeOffset.UtcNow };

    [Fact]
    public async Task IsRevokedAsync_WhenTheUserNoLongerExists_ReturnsTrue()
    {
        _userRepository.Setup(r => r.FindByIdForAuthenticationAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);

        var sut = CreateSut();
        Assert.True(await sut.IsRevokedAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task IsRevokedAsync_ForAWithdrawnUser_ReturnsTrueWithoutEverQueryingAnyTenant()
    {
        var tenantId = Guid.NewGuid();
        var user = CreateUser(tenantId, UserStatus.Withdrawn);
        _userRepository.Setup(r => r.FindByIdForAuthenticationAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);

        var sut = CreateSut();
        Assert.True(await sut.IsRevokedAsync(user.Id, CancellationToken.None));
        _tenantRepository.Verify(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IsRevokedAsync_ForAUserWhoseTenantWasDeleted_ReturnsTrue()
    {
        var tenantId = Guid.NewGuid();
        var user = CreateUser(tenantId, UserStatus.Active);
        _userRepository.Setup(r => r.FindByIdForAuthenticationAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _tenantRepository.Setup(r => r.FindByIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateTenant(tenantId, TenantStatus.Deleted));

        var sut = CreateSut();
        Assert.True(await sut.IsRevokedAsync(user.Id, CancellationToken.None));
    }

    // The deliberate narrowing - see the class doc comment. A Suspended tenant's already-issued access
    // token keeps working for the rest of its own short lifetime, exactly as SuspendTenantCommandHandler's
    // own doc comment already documents and accepts; this guard must not silently change that.
    [Fact]
    public async Task IsRevokedAsync_ForAUserWhoseTenantIsMerelySuspended_ReturnsFalse()
    {
        var tenantId = Guid.NewGuid();
        var user = CreateUser(tenantId, UserStatus.Active);
        _userRepository.Setup(r => r.FindByIdForAuthenticationAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _tenantRepository.Setup(r => r.FindByIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateTenant(tenantId, TenantStatus.Suspended));

        var sut = CreateSut();
        Assert.False(await sut.IsRevokedAsync(user.Id, CancellationToken.None));
    }

    // The other deliberate narrowing - an ordinarily-Inactive user (an administrator's own reversible
    // deactivation, never a tenant deletion) keeps the same existing, accepted bounded-access-token-window
    // behavior; only Withdrawn is a hard, immediate cutoff.
    [Fact]
    public async Task IsRevokedAsync_ForAnOrdinarilyInactiveUserOfAStillActiveTenant_ReturnsFalse()
    {
        var tenantId = Guid.NewGuid();
        var user = CreateUser(tenantId, UserStatus.Inactive);
        _userRepository.Setup(r => r.FindByIdForAuthenticationAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _tenantRepository.Setup(r => r.FindByIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateTenant(tenantId, TenantStatus.Active));

        var sut = CreateSut();
        Assert.False(await sut.IsRevokedAsync(user.Id, CancellationToken.None));
    }

    [Fact]
    public async Task IsRevokedAsync_ForTheGlobalSysAdmin_ReturnsFalseWithoutEverQueryingAnyTenant()
    {
        var sysAdmin = CreateUser(tenantId: null, UserStatus.Active);
        _userRepository.Setup(r => r.FindByIdForAuthenticationAsync(sysAdmin.Id, It.IsAny<CancellationToken>())).ReturnsAsync(sysAdmin);

        var sut = CreateSut();
        Assert.False(await sut.IsRevokedAsync(sysAdmin.Id, CancellationToken.None));
        _tenantRepository.Verify(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
