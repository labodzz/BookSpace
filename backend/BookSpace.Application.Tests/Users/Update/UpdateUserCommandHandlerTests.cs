using BookSpace.Application.Auth;
using BookSpace.Application.Common;
using BookSpace.Application.Users;
using BookSpace.Domain.Entities;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Users;

public sealed class UpdateUserCommandHandlerTests
{
    private readonly Mock<IUserRepository> _userRepository = new();

    private UpdateUserCommandHandler CreateSut() => new(_userRepository.Object);

    [Fact]
    public async Task Handle_UpdatesFirstAndLastNameAndReturnsTheUpdatedUser()
    {
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, FirstName = "Old", LastName = "Name", Email = "jane@bookspace.test" };
        _userRepository.Setup(r => r.FindByIdAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _userRepository.Setup(r => r.GetRolesAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<string>)["Member"]);

        var sut = CreateSut();
        var result = await sut.Handle(new UpdateUserCommandRequest(userId, "New", "Name"), CancellationToken.None);

        Assert.Equal("New", result.FirstName);
        Assert.Equal("Name", result.LastName);
        Assert.Equal("jane@bookspace.test", result.Email); // unchanged - Email is not editable here
        _userRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithAnUnknownUser_ThrowsNotFound()
    {
        _userRepository.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);

        var sut = CreateSut();
        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new UpdateUserCommandRequest(Guid.NewGuid(), "New", "Name"), CancellationToken.None));

        Assert.Equal("User.NotFound", exception.ErrorCode);
    }
}
