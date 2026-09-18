using BookSpace.Application.Auth;
using Xunit;

namespace BookSpace.Application.Tests.Auth;

public sealed class LoginCommandRequestValidatorTests
{
    private readonly LoginCommandRequestValidator _sut = new();

    [Fact]
    public void Validate_WithValidCommand_HasNoErrors()
    {
        var result = _sut.Validate(new LoginCommandRequest("user@bookspace.test", "correct-password"));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("", "correct-password")]
    [InlineData("not-an-email", "correct-password")]
    [InlineData("user@bookspace.test", "")]
    public void Validate_WithInvalidCommand_HasErrors(string email, string password)
    {
        var result = _sut.Validate(new LoginCommandRequest(email, password));

        Assert.False(result.IsValid);
    }
}
