using BookSpace.Application.ResourceTypes;
using Xunit;

namespace BookSpace.Application.Tests.ResourceTypes;

public sealed class UpdateResourceTypeCommandRequestValidatorTests
{
    private readonly UpdateResourceTypeCommandRequestValidator _sut = new();

    [Fact]
    public void Validate_WithValidCommand_HasNoErrors()
    {
        var result = _sut.Validate(new UpdateResourceTypeCommandRequest(Guid.NewGuid(), "Boardroom"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WithEmptyId_HasErrors()
    {
        var result = _sut.Validate(new UpdateResourceTypeCommandRequest(Guid.Empty, "Boardroom"));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithEmptyName_HasErrors()
    {
        var result = _sut.Validate(new UpdateResourceTypeCommandRequest(Guid.NewGuid(), ""));

        Assert.False(result.IsValid);
    }
}
