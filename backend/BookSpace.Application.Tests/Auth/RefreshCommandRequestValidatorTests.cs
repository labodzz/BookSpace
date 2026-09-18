using BookSpace.Application.Auth;
using Xunit;

namespace BookSpace.Application.Tests.Auth;

public sealed class RefreshCommandRequestValidatorTests
{
    private readonly RefreshCommandRequestValidator _sut = new();

    [Fact]
    public void Validate_WithNonEmptyToken_HasNoErrors()
    {
        var result = _sut.Validate(new RefreshCommandRequest("some-refresh-token"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WithEmptyToken_HasErrors()
    {
        var result = _sut.Validate(new RefreshCommandRequest(""));

        Assert.False(result.IsValid);
    }
}
