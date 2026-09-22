using BookSpace.Application.Auth;
using BookSpace.Application.Common;
using BookSpace.Application.Users;
using BookSpace.Domain.Entities;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Users;

public sealed class AssignUserRoleCommandHandlerTests
{
    private readonly Mock<IUserRepository> _userRepository = new();

    private AssignUserRoleCommandHandler CreateSut() => new(_userRepository.Object);

    [Fact]
    public async Task Handle_WithARoleTheUserDoesNotYetHold_AssignsItAndReturnsTheUpdatedRoleList()
    {
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        _userRepository.Setup(r => r.FindByIdAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(new User { Id = userId });
        _userRepository.Setup(r => r.FindRoleByNameAsync("Approver", It.IsAny<CancellationToken>())).ReturnsAsync(new Role { Id = roleId, Name = "Approver" });
        _userRepository.Setup(r => r.GetRolesAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<string>)["Member"]);

        var sut = CreateSut();
        var result = await sut.Handle(new AssignUserRoleCommandRequest(userId, "Approver"), CancellationToken.None);

        _userRepository.Verify(r => r.AddRoleAsync(userId, roleId, It.IsAny<CancellationToken>()), Times.Once);
        _userRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(["Member", "Approver"], result.Roles);
    }

    [Fact]
    public async Task Handle_WithARoleAlreadyHeld_ThrowsConflictWithoutAssigningAgain()
    {
        var userId = Guid.NewGuid();
        _userRepository.Setup(r => r.FindByIdAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(new User { Id = userId });
        _userRepository.Setup(r => r.FindRoleByNameAsync("Approver", It.IsAny<CancellationToken>())).ReturnsAsync(new Role { Id = Guid.NewGuid(), Name = "Approver" });
        _userRepository.Setup(r => r.GetRolesAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<string>)["Approver"]);

        var sut = CreateSut();
        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new AssignUserRoleCommandRequest(userId, "Approver"), CancellationToken.None));

        Assert.Equal("User.RoleConflict", exception.ErrorCode);
        _userRepository.Verify(r => r.AddRoleAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WithAnUnknownUser_ThrowsNotFound()
    {
        _userRepository.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);

        var sut = CreateSut();
        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new AssignUserRoleCommandRequest(Guid.NewGuid(), "Approver"), CancellationToken.None));

        Assert.Equal("User.NotFound", exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WithAnUnknownRoleName_ThrowsNotFound()
    {
        var userId = Guid.NewGuid();
        _userRepository.Setup(r => r.FindByIdAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(new User { Id = userId });
        _userRepository.Setup(r => r.FindRoleByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((Role?)null);

        var sut = CreateSut();
        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new AssignUserRoleCommandRequest(userId, "Approver"), CancellationToken.None));

        Assert.Equal("Role.NotFound", exception.ErrorCode);
    }
}
