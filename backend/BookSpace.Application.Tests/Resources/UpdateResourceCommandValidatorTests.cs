using BookSpace.Application.Resources;
using BookSpace.Domain.Enums;
using Xunit;

namespace BookSpace.Application.Tests.Resources;

public sealed class UpdateResourceCommandValidatorTests
{
    private readonly UpdateResourceCommandValidator _sut = new();

    [Fact]
    public void Validate_WithValidCommand_HasNoErrors()
    {
        var result = _sut.Validate(new UpdateResourceCommand(Guid.NewGuid(), Guid.NewGuid(), "Name", null, 4, false, "UTC", ResourceStatus.Active));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WithArchivedStatus_HasErrors()
    {
        var result = _sut.Validate(new UpdateResourceCommand(Guid.NewGuid(), Guid.NewGuid(), "Name", null, 4, false, "UTC", ResourceStatus.Archived));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithEmptyId_HasErrors()
    {
        var result = _sut.Validate(new UpdateResourceCommand(Guid.Empty, Guid.NewGuid(), "Name", null, 4, false, "UTC", ResourceStatus.Active));

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("", 4, "UTC")]
    [InlineData("Name", 0, "UTC")]
    [InlineData("Name", 4, "Not/ARealZone")]
    public void Validate_WithInvalidFields_HasErrors(string name, int capacity, string timeZoneId)
    {
        var result = _sut.Validate(new UpdateResourceCommand(Guid.NewGuid(), Guid.NewGuid(), name, null, capacity, false, timeZoneId, ResourceStatus.Active));

        Assert.False(result.IsValid);
    }
}
