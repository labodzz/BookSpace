using BookSpace.Application.Auth;
using Xunit;

namespace BookSpace.Application.Tests.Auth;

public sealed class RefreshCommandValidatorTests
{
    private readonly RefreshCommandValidator _sut = new();

    [Fact]
    public void Validate_WithNonEmptyToken_HasNoErrors()
    {
        var result = _sut.Validate(new RefreshCommand("some-refresh-token"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WithEmptyToken_HasErrors()
    {
        var result = _sut.Validate(new RefreshCommand(""));

        Assert.False(result.IsValid);
    }
}
