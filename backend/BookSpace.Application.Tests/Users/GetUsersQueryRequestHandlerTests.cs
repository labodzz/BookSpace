using BookSpace.Application.Auth;
using BookSpace.Application.Common;
using BookSpace.Application.Users;
using BookSpace.Domain.Entities;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Users;

public sealed class GetUsersQueryRequestHandlerTests
{
    private readonly Mock<IUserRepository> _userRepository = new();

    private GetUsersQueryRequestHandler CreateSut() => new(_userRepository.Object);

    [Fact]
    public async Task Handle_MapsRepositoryUsersToSummaryResponsesPreservingPaging()
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
        _userRepository.Setup(r => r.GetPagedAsync(2, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<User>([user], 2, 10, 1));
        var sut = CreateSut();

        var result = await sut.Handle(new GetUsersQueryRequest(Page: 2, PageSize: 10), CancellationToken.None);

        var summary = Assert.Single(result.Items);
        Assert.Equal(user.Id, summary.Id);
        Assert.Equal(user.FirstName, summary.FirstName);
        Assert.Equal(user.LastName, summary.LastName);
        Assert.Equal(user.Email, summary.Email);
        Assert.Equal(tenantId, summary.TenantId);
        Assert.Equal(2, result.Page);
        Assert.Equal(10, result.PageSize);
        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public async Task Handle_WithNoUsers_ReturnsEmptyList()
    {
        _userRepository.Setup(r => r.GetPagedAsync(1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<User>([], 1, 20, 0));
        var sut = CreateSut();

        var result = await sut.Handle(new GetUsersQueryRequest(), CancellationToken.None);

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Handle_PassesPageAndPageSizeThroughToTheRepository()
    {
        _userRepository.Setup(r => r.GetPagedAsync(3, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<User>([], 3, 5, 0));
        var sut = CreateSut();

        await sut.Handle(new GetUsersQueryRequest(Page: 3, PageSize: 5), CancellationToken.None);

        _userRepository.Verify(r => r.GetPagedAsync(3, 5, It.IsAny<CancellationToken>()), Times.Once);
    }
}
