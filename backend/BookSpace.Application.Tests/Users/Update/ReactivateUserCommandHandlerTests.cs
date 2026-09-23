using BookSpace.Application.Common;
using BookSpace.Application.Auth;
using BookSpace.Application.Users;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Users;

public sealed class ReactivateUserCommandHandlerTests
{
    private readonly Mock<IUserRepository> _userRepository = new();

    private ReactivateUserCommandHandler CreateSut() => new(_userRepository.Object);

    [Fact]
    public async Task Handle_AnInactiveUser_ReactivatesThem()
    {
        var userId = Guid.NewGuid();
        _userRepository.Setup(r => r.FindByIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User { Id = userId, Status = UserStatus.Inactive });
        _userRepository.Setup(r => r.GetRolesAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<string>)["Member"]);

        var sut = CreateSut();
        var result = await sut.Handle(new ReactivateUserCommandRequest(userId), CancellationToken.None);

        Assert.Equal(UserStatus.Active, result.Status);
        _userRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_AnAlreadyActiveUser_ThrowsConflictWithoutChangingAnything()
    {
        var userId = Guid.NewGuid();
        _userRepository.Setup(r => r.FindByIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User { Id = userId, Status = UserStatus.Active });

        var sut = CreateSut();
        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new ReactivateUserCommandRequest(userId), CancellationToken.None));

        Assert.Equal("User.StatusConflict", exception.ErrorCode);
        _userRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // Accepting the invitation is the correct path from Invited, not an admin "reactivate" action.
    [Fact]
    public async Task Handle_AStillInvitedUser_ThrowsConflictWithoutChangingAnything()
    {
        var userId = Guid.NewGuid();
        _userRepository.Setup(r => r.FindByIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User { Id = userId, Status = UserStatus.Invited });

        var sut = CreateSut();
        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new ReactivateUserCommandRequest(userId), CancellationToken.None));

        Assert.Equal("User.StatusConflict", exception.ErrorCode);
        _userRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WithAnUnknownUser_ThrowsNotFound()
    {
        _userRepository.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);

        var sut = CreateSut();
        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new ReactivateUserCommandRequest(Guid.NewGuid()), CancellationToken.None));

        Assert.Equal("User.NotFound", exception.ErrorCode);
    }
}
