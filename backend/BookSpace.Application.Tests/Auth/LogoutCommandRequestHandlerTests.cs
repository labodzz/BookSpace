using BookSpace.Application.Auth;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Auth;

public sealed class LogoutCommandRequestHandlerTests
{
    private readonly Mock<IAuthenticationService> _authenticationService = new();

    [Fact]
    public async Task Handle_DelegatesToAuthenticationServiceWithTheCommandsRefreshToken()
    {
        var expected = new LogoutResponse();
        _authenticationService
            .Setup(s => s.LogoutAsync("raw-refresh-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var sut = new LogoutCommandRequestHandler(_authenticationService.Object);

        var result = await sut.Handle(new LogoutCommandRequest("raw-refresh-token"), CancellationToken.None);

        Assert.Same(expected, result);
    }
}
