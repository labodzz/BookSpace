using BookSpace.Application.Resources;
using Xunit;

namespace BookSpace.Application.Tests.Resources;

public sealed class CreateResourceCommandValidatorTests
{
    private readonly CreateResourceCommandValidator _sut = new();

    [Fact]
    public void Validate_WithValidCommand_HasNoErrors()
    {
        var result = _sut.Validate(new CreateResourceCommand(Guid.NewGuid(), "Conference Room A", "desc", 8, true, "UTC"));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("", 8, "UTC")]
    [InlineData("Conference Room A", 0, "UTC")]
    [InlineData("Conference Room A", -1, "UTC")]
    [InlineData("Conference Room A", 8, "Not/ARealZone")]
    [InlineData("Conference Room A", 8, "")]
    public void Validate_WithInvalidFields_HasErrors(string name, int capacity, string timeZoneId)
    {
        var result = _sut.Validate(new CreateResourceCommand(Guid.NewGuid(), name, null, capacity, false, timeZoneId));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithEmptyResourceTypeId_HasErrors()
    {
        var result = _sut.Validate(new CreateResourceCommand(Guid.Empty, "Conference Room A", null, 8, false, "UTC"));

        Assert.False(result.IsValid);
    }
}
