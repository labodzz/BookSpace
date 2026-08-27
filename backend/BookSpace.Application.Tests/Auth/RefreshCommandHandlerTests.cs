using BookSpace.Application.Auth;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Auth;

public sealed class RefreshCommandHandlerTests
{
    private readonly Mock<IAuthenticationService> _authenticationService = new();

    [Fact]
    public async Task Handle_DelegatesToAuthenticationServiceWithTheCommandsRefreshToken()
    {
        var expected = new RefreshResult(true, new AuthTokens("access", DateTimeOffset.UtcNow, "refresh", DateTimeOffset.UtcNow), false);
        _authenticationService
            .Setup(s => s.RefreshAsync("raw-refresh-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var sut = new RefreshCommandHandler(_authenticationService.Object);

        var result = await sut.Handle(new RefreshCommand("raw-refresh-token"), CancellationToken.None);

        Assert.Same(expected, result);
    }
}
