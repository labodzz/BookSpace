using BookSpace.Application.Resources;
using Xunit;

namespace BookSpace.Application.Tests.Resources;

public sealed class CreateBlackoutPeriodCommandValidatorTests
{
    private readonly CreateBlackoutPeriodCommandValidator _sut = new();

    [Fact]
    public void Validate_WithValidCommand_HasNoErrors()
    {
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var result = _sut.Validate(new CreateBlackoutPeriodCommand(Guid.NewGuid(), start, start.AddHours(2), "Maintenance"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WithEndNotAfterStart_HasErrors()
    {
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var result = _sut.Validate(new CreateBlackoutPeriodCommand(Guid.NewGuid(), start, start, "Maintenance"));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithEmptyReason_HasErrors()
    {
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var result = _sut.Validate(new CreateBlackoutPeriodCommand(Guid.NewGuid(), start, start.AddHours(1), ""));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithEmptyResourceId_HasErrors()
    {
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var result = _sut.Validate(new CreateBlackoutPeriodCommand(Guid.Empty, start, start.AddHours(1), "Maintenance"));

        Assert.False(result.IsValid);
    }
}
