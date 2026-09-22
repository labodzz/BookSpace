using BookSpace.Application.Auth;
using BookSpace.Application.Common;
using BookSpace.Application.Users;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Users;

public sealed class GetUserQueryHandlerTests
{
    private readonly Mock<IUserRepository> _userRepository = new();
    private readonly Mock<IInvitationRepository> _invitationRepository = new();

    private GetUserQueryHandler CreateSut() => new(_userRepository.Object, _invitationRepository.Object);

    [Fact]
    public async Task Handle_AnActiveUser_ReturnsFullDetailWithNoPendingInvitation()
    {
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow.AddDays(-10);
        _userRepository.Setup(r => r.FindByIdAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(new User
        {
            Id = userId, TenantId = tenantId, FirstName = "Jane", LastName = "Doe", Email = "jane@bookspace.test",
            Status = UserStatus.Active, CreatedAtUtc = createdAt,
        });
        _userRepository.Setup(r => r.GetRolesAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<string>)["Approver"]);

        var sut = CreateSut();
        var result = await sut.Handle(new GetUserQueryRequest(userId), CancellationToken.None);

        Assert.Equal(userId, result.Id);
        Assert.Equal(tenantId, result.TenantId);
        Assert.Equal(UserStatus.Active, result.Status);
        Assert.Equal(["Approver"], result.Roles);
        Assert.Equal(createdAt, result.CreatedAtUtc);
        Assert.Null(result.PendingInvitationExpiresAtUtc);
        _invitationRepository.Verify(r => r.FindActiveByUserIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_AnInvitedUserWithAnActiveInvitation_IncludesItsExpiry()
    {
        var userId = Guid.NewGuid();
        _userRepository.Setup(r => r.FindByIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User { Id = userId, Status = UserStatus.Invited });
        _userRepository.Setup(r => r.GetRolesAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<string>)[]);
        var expiresAt = DateTimeOffset.UtcNow.AddHours(60);
        _invitationRepository.Setup(r => r.FindActiveByUserIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Invitation { Id = Guid.NewGuid(), UserId = userId, ExpiresAtUtc = expiresAt });

        var sut = CreateSut();
        var result = await sut.Handle(new GetUserQueryRequest(userId), CancellationToken.None);

        Assert.Equal(expiresAt, result.PendingInvitationExpiresAtUtc);
    }

    [Fact]
    public async Task Handle_WithAnUnknownUser_ThrowsNotFound()
    {
        _userRepository.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);

        var sut = CreateSut();
        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new GetUserQueryRequest(Guid.NewGuid()), CancellationToken.None));

        Assert.Equal("User.NotFound", exception.ErrorCode);
    }
}
