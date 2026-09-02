using BookSpace.Application.Resources;
using Xunit;

namespace BookSpace.Application.Tests.Resources;

public sealed class GetResourceAvailabilityQueryRequestValidatorTests
{
    private readonly GetResourceAvailabilityQueryRequestValidator _sut = new();

    private static readonly DateOnly Date = new(2026, 9, 7);

    [Fact]
    public void Validate_WithValidRange_HasNoErrors()
    {
        var result = _sut.Validate(new GetResourceAvailabilityQueryRequest(Guid.NewGuid(), Date, Date.AddDays(7)));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WithToDateBeforeFromDate_HasErrors()
    {
        var result = _sut.Validate(new GetResourceAvailabilityQueryRequest(Guid.NewGuid(), Date, Date.AddDays(-1)));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithRangeExceeding92Days_HasErrors()
    {
        var result = _sut.Validate(new GetResourceAvailabilityQueryRequest(Guid.NewGuid(), Date, Date.AddDays(92)));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithEmptyResourceId_HasErrors()
    {
        var result = _sut.Validate(new GetResourceAvailabilityQueryRequest(Guid.Empty, Date, Date));

        Assert.False(result.IsValid);
    }
}
