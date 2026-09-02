using BookSpace.Application.Resources;
using Xunit;

namespace BookSpace.Application.Tests.Resources;

public sealed class UpdateBlackoutPeriodCommandRequestValidatorTests
{
    private readonly UpdateBlackoutPeriodCommandRequestValidator _sut = new();

    [Fact]
    public void Validate_WithValidCommand_HasNoErrors()
    {
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var result = _sut.Validate(new UpdateBlackoutPeriodCommandRequest(Guid.NewGuid(), Guid.NewGuid(), start, start.AddHours(2), "Maintenance"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WithEndNotAfterStart_HasErrors()
    {
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var result = _sut.Validate(new UpdateBlackoutPeriodCommandRequest(Guid.NewGuid(), Guid.NewGuid(), start, start, "Maintenance"));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithEmptyBlackoutId_HasErrors()
    {
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var result = _sut.Validate(new UpdateBlackoutPeriodCommandRequest(Guid.NewGuid(), Guid.Empty, start, start.AddHours(1), "Maintenance"));

        Assert.False(result.IsValid);
    }
}
