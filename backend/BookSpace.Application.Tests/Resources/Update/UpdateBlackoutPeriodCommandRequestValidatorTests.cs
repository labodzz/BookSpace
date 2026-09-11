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

    // Three rules below had zero test coverage for Update despite Create's identical validator having
    // tests for all of them (empty ResourceId, empty Reason, Reason MaxLength(1000) at both boundaries).

    [Fact]
    public void Validate_WithEmptyResourceId_HasErrors()
    {
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var result = _sut.Validate(new UpdateBlackoutPeriodCommandRequest(Guid.Empty, Guid.NewGuid(), start, start.AddHours(1), "Maintenance"));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithEmptyReason_HasErrors()
    {
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var result = _sut.Validate(new UpdateBlackoutPeriodCommandRequest(Guid.NewGuid(), Guid.NewGuid(), start, start.AddHours(1), ""));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithReasonOfExactly1000Characters_HasNoErrors()
    {
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var result = _sut.Validate(new UpdateBlackoutPeriodCommandRequest(Guid.NewGuid(), Guid.NewGuid(), start, start.AddHours(1), new string('a', 1000)));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WithReasonOf1001Characters_HasErrors()
    {
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var result = _sut.Validate(new UpdateBlackoutPeriodCommandRequest(Guid.NewGuid(), Guid.NewGuid(), start, start.AddHours(1), new string('a', 1001)));

        Assert.False(result.IsValid);
    }
}
