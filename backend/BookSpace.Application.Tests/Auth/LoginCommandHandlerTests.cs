using BookSpace.Application.Auth;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Auth;

public sealed class LoginCommandHandlerTests
{
    private readonly Mock<IAuthenticationService> _authenticationService = new();

    [Fact]
    public async Task Handle_DelegatesToAuthenticationServiceWithTheCommandsCredentials()
    {
        var expected = new LoginResult(true, new AuthTokens("access", DateTimeOffset.UtcNow, "refresh", DateTimeOffset.UtcNow));
        _authenticationService
            .Setup(s => s.LoginAsync("user@bookspace.test", "correct-password", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var sut = new LoginCommandHandler(_authenticationService.Object);

        var result = await sut.Handle(new LoginCommand("user@bookspace.test", "correct-password"), CancellationToken.None);

        Assert.Same(expected, result);
    }
}
