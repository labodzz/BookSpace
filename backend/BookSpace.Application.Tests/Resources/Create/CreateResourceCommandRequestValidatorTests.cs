using BookSpace.Application.Resources;
using Xunit;

namespace BookSpace.Application.Tests.Resources;

public sealed class CreateResourceCommandRequestValidatorTests
{
    private readonly CreateResourceCommandRequestValidator _sut = new();

    [Fact]
    public void Validate_WithValidCommand_HasNoErrors()
    {
        var result = _sut.Validate(new CreateResourceCommandRequest(Guid.NewGuid(), "Conference Room A", "desc", 8, true, "UTC"));

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
        var result = _sut.Validate(new CreateResourceCommandRequest(Guid.NewGuid(), name, null, capacity, false, timeZoneId));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithEmptyResourceTypeId_HasErrors()
    {
        var result = _sut.Validate(new CreateResourceCommandRequest(Guid.Empty, "Conference Room A", null, 8, false, "UTC"));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithNameOfExactly200Characters_HasNoErrors()
    {
        var result = _sut.Validate(new CreateResourceCommandRequest(Guid.NewGuid(), new string('a', 200), null, 8, false, "UTC"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WithNameOf201Characters_HasErrors()
    {
        var result = _sut.Validate(new CreateResourceCommandRequest(Guid.NewGuid(), new string('a', 201), null, 8, false, "UTC"));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithDescriptionOfExactly2000Characters_HasNoErrors()
    {
        var result = _sut.Validate(new CreateResourceCommandRequest(Guid.NewGuid(), "Conference Room A", new string('a', 2000), 8, false, "UTC"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WithDescriptionOf2001Characters_HasErrors()
    {
        var result = _sut.Validate(new CreateResourceCommandRequest(Guid.NewGuid(), "Conference Room A", new string('a', 2001), 8, false, "UTC"));

        Assert.False(result.IsValid);
    }
}
