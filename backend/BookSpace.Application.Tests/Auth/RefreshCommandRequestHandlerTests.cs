using BookSpace.Application.Auth;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Auth;

public sealed class RefreshCommandRequestHandlerTests
{
    private readonly Mock<IAuthenticationService> _authenticationService = new();

    [Fact]
    public async Task Handle_DelegatesToAuthenticationServiceWithTheCommandsRefreshToken()
    {
        var expected = new RefreshResponse(true, new AuthTokens("access", DateTimeOffset.UtcNow, "refresh", DateTimeOffset.UtcNow), false);
        _authenticationService
            .Setup(s => s.RefreshAsync("raw-refresh-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var sut = new RefreshCommandRequestHandler(_authenticationService.Object);

        var result = await sut.Handle(new RefreshCommandRequest("raw-refresh-token"), CancellationToken.None);

        Assert.Same(expected, result);
    }
}
