using BookSpace.Application.Security;
using BookSpace.Application.Users;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Users;

public sealed class GetCurrentUserQueryHandlerTests
{
    private readonly Mock<ICurrentUserContext> _currentUserContext = new();

    [Fact]
    public async Task Handle_ReturnsUserIdTenantIdAndRolesFromTheCurrentUserContext()
    {
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var roles = new[] { "TenantAdmin" };
        _currentUserContext.SetupGet(c => c.UserId).Returns(userId);
        _currentUserContext.SetupGet(c => c.TenantId).Returns(tenantId);
        _currentUserContext.SetupGet(c => c.Roles).Returns(roles);
        var sut = new GetCurrentUserQueryHandler(_currentUserContext.Object);

        var result = await sut.Handle(new GetCurrentUserQuery(), CancellationToken.None);

        Assert.Equal(userId, result.UserId);
        Assert.Equal(tenantId, result.TenantId);
        Assert.Equal(roles, result.Roles);
    }

    [Fact]
    public async Task Handle_WhenUnauthenticated_ReturnsNullUserIdAndTenantId()
    {
        _currentUserContext.SetupGet(c => c.UserId).Returns((Guid?)null);
        _currentUserContext.SetupGet(c => c.TenantId).Returns((Guid?)null);
        _currentUserContext.SetupGet(c => c.Roles).Returns([]);
        var sut = new GetCurrentUserQueryHandler(_currentUserContext.Object);

        var result = await sut.Handle(new GetCurrentUserQuery(), CancellationToken.None);

        Assert.Null(result.UserId);
        Assert.Null(result.TenantId);
        Assert.Empty(result.Roles);
    }
}
