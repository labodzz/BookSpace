using BookSpace.Application.Resources;
using BookSpace.Domain.Enums;
using Xunit;

namespace BookSpace.Application.Tests.Resources;

public sealed class UpdateResourceCommandRequestValidatorTests
{
    private readonly UpdateResourceCommandRequestValidator _sut = new();

    [Fact]
    public void Validate_WithValidCommand_HasNoErrors()
    {
        var result = _sut.Validate(new UpdateResourceCommandRequest(Guid.NewGuid(), Guid.NewGuid(), "Name", null, 4, false, "UTC", ResourceStatus.Active));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WithArchivedStatus_HasErrors()
    {
        var result = _sut.Validate(new UpdateResourceCommandRequest(Guid.NewGuid(), Guid.NewGuid(), "Name", null, 4, false, "UTC", ResourceStatus.Archived));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithEmptyId_HasErrors()
    {
        var result = _sut.Validate(new UpdateResourceCommandRequest(Guid.Empty, Guid.NewGuid(), "Name", null, 4, false, "UTC", ResourceStatus.Active));

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("", 4, "UTC")]
    [InlineData("Name", 0, "UTC")]
    [InlineData("Name", 4, "Not/ARealZone")]
    public void Validate_WithInvalidFields_HasErrors(string name, int capacity, string timeZoneId)
    {
        var result = _sut.Validate(new UpdateResourceCommandRequest(Guid.NewGuid(), Guid.NewGuid(), name, null, capacity, false, timeZoneId, ResourceStatus.Active));

        Assert.False(result.IsValid);
    }

    // Six rules below had zero test coverage despite existing on this validator - Create's identical
    // rules (Name/Description MaxLength) are tested; Update's were not.

    [Fact]
    public void Validate_WithEmptyResourceTypeId_HasErrors()
    {
        var result = _sut.Validate(new UpdateResourceCommandRequest(Guid.NewGuid(), Guid.Empty, "Name", null, 4, false, "UTC", ResourceStatus.Active));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithNameOfExactly200Characters_HasNoErrors()
    {
        var result = _sut.Validate(new UpdateResourceCommandRequest(Guid.NewGuid(), Guid.NewGuid(), new string('a', 200), null, 4, false, "UTC", ResourceStatus.Active));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WithNameOf201Characters_HasErrors()
    {
        var result = _sut.Validate(new UpdateResourceCommandRequest(Guid.NewGuid(), Guid.NewGuid(), new string('a', 201), null, 4, false, "UTC", ResourceStatus.Active));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithDescriptionOf2001Characters_HasErrors()
    {
        var result = _sut.Validate(new UpdateResourceCommandRequest(Guid.NewGuid(), Guid.NewGuid(), "Name", new string('a', 2001), 4, false, "UTC", ResourceStatus.Active));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithEmptyTimeZoneId_HasErrors()
    {
        var result = _sut.Validate(new UpdateResourceCommandRequest(Guid.NewGuid(), Guid.NewGuid(), "Name", null, 4, false, "", ResourceStatus.Active));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithNegativeCapacity_HasErrors()
    {
        var result = _sut.Validate(new UpdateResourceCommandRequest(Guid.NewGuid(), Guid.NewGuid(), "Name", null, -1, false, "UTC", ResourceStatus.Active));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithOutOfRangeStatus_HasErrors()
    {
        var result = _sut.Validate(new UpdateResourceCommandRequest(Guid.NewGuid(), Guid.NewGuid(), "Name", null, 4, false, "UTC", (ResourceStatus)999));

        Assert.False(result.IsValid);
    }
}
