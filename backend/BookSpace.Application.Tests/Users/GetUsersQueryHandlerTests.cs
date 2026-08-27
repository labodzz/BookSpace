using BookSpace.Application.Auth;
using BookSpace.Application.Users;
using BookSpace.Domain.Entities;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Users;

public sealed class GetUsersQueryHandlerTests
{
    private readonly Mock<IUserRepository> _userRepository = new();

    [Fact]
    public async Task Handle_MapsRepositoryUsersToSummaryResponses()
    {
        var tenantId = Guid.NewGuid();
        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            FirstName = "Test",
            LastName = "User",
            Email = "test.user@bookspace.test",
            PasswordHash = "stored-hash",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        _userRepository.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<User>)[user]);
        var sut = new GetUsersQueryHandler(_userRepository.Object);

        var result = await sut.Handle(new GetUsersQuery(), CancellationToken.None);

        var summary = Assert.Single(result);
        Assert.Equal(user.Id, summary.Id);
        Assert.Equal(user.FirstName, summary.FirstName);
        Assert.Equal(user.LastName, summary.LastName);
        Assert.Equal(user.Email, summary.Email);
        Assert.Equal(tenantId, summary.TenantId);
    }

    [Fact]
    public async Task Handle_WithNoUsers_ReturnsEmptyList()
    {
        _userRepository.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<User>)[]);
        var sut = new GetUsersQueryHandler(_userRepository.Object);

        var result = await sut.Handle(new GetUsersQuery(), CancellationToken.None);

        Assert.Empty(result);
    }
}
