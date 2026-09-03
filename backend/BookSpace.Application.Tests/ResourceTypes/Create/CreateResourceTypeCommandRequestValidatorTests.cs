using BookSpace.Application.ResourceTypes;
using Xunit;

namespace BookSpace.Application.Tests.ResourceTypes;

public sealed class CreateResourceTypeCommandRequestValidatorTests
{
    private readonly CreateResourceTypeCommandRequestValidator _sut = new();

    [Fact]
    public void Validate_WithValidName_HasNoErrors()
    {
        var result = _sut.Validate(new CreateResourceTypeCommandRequest("Meeting Room"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WithEmptyName_HasErrors()
    {
        var result = _sut.Validate(new CreateResourceTypeCommandRequest(""));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithNameOver100Characters_HasErrors()
    {
        var result = _sut.Validate(new CreateResourceTypeCommandRequest(new string('a', 101)));

        Assert.False(result.IsValid);
    }
}
