using BookSpace.Application.Auth;
using BookSpace.Application.Common;
using BookSpace.Application.Security;
using BookSpace.Application.Users;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Users;

public sealed class DeactivateUserCommandHandlerTests
{
    private readonly Mock<IUserRepository> _userRepository = new();
    private readonly Guid _callerId = Guid.NewGuid();

    private DeactivateUserCommandHandler CreateSut() => new(_userRepository.Object, new FixedCurrentUserContext(_callerId));

    private void SetUpUser(Guid userId, UserStatus status, IReadOnlyList<string> roles)
    {
        _userRepository.Setup(r => r.FindByIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User { Id = userId, FirstName = "Jane", LastName = "Doe", Email = "jane@bookspace.test", Status = status });
        _userRepository.Setup(r => r.GetRolesAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(roles);
    }

    [Fact]
    public async Task Handle_AnActiveNonAdminUser_DeactivatesThem()
    {
        var userId = Guid.NewGuid();
        SetUpUser(userId, UserStatus.Active, ["Member"]);

        var sut = CreateSut();
        var result = await sut.Handle(new DeactivateUserCommandRequest(userId), CancellationToken.None);

        Assert.Equal(UserStatus.Inactive, result.Status);
        _userRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // Invited -> Inactive is a valid transition too - cancelling a pending invitation by deactivating
    // the still-pending user, since no separate "revoke invitation" action exists in this batch.
    [Fact]
    public async Task Handle_AStillInvitedUser_DeactivatesThemToo()
    {
        var userId = Guid.NewGuid();
        SetUpUser(userId, UserStatus.Invited, []);

        var sut = CreateSut();
        var result = await sut.Handle(new DeactivateUserCommandRequest(userId), CancellationToken.None);

        Assert.Equal(UserStatus.Inactive, result.Status);
    }

    // Idempotent, same DELETE-means-soft-transition convention as DeleteResourceCommandHandler.
    [Fact]
    public async Task Handle_AnAlreadyInactiveUser_ReturnsTheirCurrentStateWithoutError()
    {
        var userId = Guid.NewGuid();
        SetUpUser(userId, UserStatus.Inactive, []);

        var sut = CreateSut();
        var result = await sut.Handle(new DeactivateUserCommandRequest(userId), CancellationToken.None);

        Assert.Equal(UserStatus.Inactive, result.Status);
        _userRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WithAnUnknownUser_ThrowsNotFound()
    {
        _userRepository.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);

        var sut = CreateSut();
        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new DeactivateUserCommandRequest(Guid.NewGuid()), CancellationToken.None));

        Assert.Equal("User.NotFound", exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_CallerDeactivatingTheirOwnAccount_ThrowsSelfLockoutConflict()
    {
        SetUpUser(_callerId, UserStatus.Active, ["TenantAdmin"]);

        var sut = CreateSut();
        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new DeactivateUserCommandRequest(_callerId), CancellationToken.None));

        Assert.Equal("User.SelfLockout", exception.ErrorCode);
        _userRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_DeactivatingTheSoleActiveAdministrator_ThrowsLastAdminConflict()
    {
        var otherUserId = Guid.NewGuid();
        SetUpUser(otherUserId, UserStatus.Active, ["TenantAdmin"]);
        _userRepository.Setup(r => r.CountActiveAdministratorsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var sut = CreateSut();
        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new DeactivateUserCommandRequest(otherUserId), CancellationToken.None));

        Assert.Equal("User.LastAdminRemaining", exception.ErrorCode);
        _userRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_DeactivatingAnAdministratorWhenAnotherActiveAdministratorExists_Succeeds()
    {
        var otherUserId = Guid.NewGuid();
        SetUpUser(otherUserId, UserStatus.Active, ["TenantAdmin"]);
        _userRepository.Setup(r => r.CountActiveAdministratorsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(2);

        var sut = CreateSut();
        var result = await sut.Handle(new DeactivateUserCommandRequest(otherUserId), CancellationToken.None);

        Assert.Equal(UserStatus.Inactive, result.Status);
    }

    [Fact]
    public async Task Handle_DeactivatingANonAdministrator_NeverChecksTheLastAdminInvariant()
    {
        var otherUserId = Guid.NewGuid();
        SetUpUser(otherUserId, UserStatus.Active, ["Member"]);

        var sut = CreateSut();
        await sut.Handle(new DeactivateUserCommandRequest(otherUserId), CancellationToken.None);

        _userRepository.Verify(r => r.CountActiveAdministratorsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class FixedCurrentUserContext(Guid userId) : ICurrentUserContext
    {
        public Guid? UserId => userId;
        public Guid? TenantId => Guid.NewGuid();
        public IReadOnlyCollection<string> Roles => [];
    }
}
