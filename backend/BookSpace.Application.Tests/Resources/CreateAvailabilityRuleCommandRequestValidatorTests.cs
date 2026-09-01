using BookSpace.Application.Resources;
using Xunit;

namespace BookSpace.Application.Tests.Resources;

public sealed class CreateAvailabilityRuleCommandRequestValidatorTests
{
    private readonly CreateAvailabilityRuleCommandRequestValidator _sut = new();

    [Fact]
    public void Validate_WithValidCommand_HasNoErrors()
    {
        var result = _sut.Validate(new CreateAvailabilityRuleCommandRequest(Guid.NewGuid(), DayOfWeek.Monday, new TimeOnly(8, 0), new TimeOnly(18, 0)));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WithEndTimeNotAfterStartTime_HasErrors()
    {
        var result = _sut.Validate(new CreateAvailabilityRuleCommandRequest(Guid.NewGuid(), DayOfWeek.Monday, new TimeOnly(18, 0), new TimeOnly(8, 0)));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithEmptyResourceId_HasErrors()
    {
        var result = _sut.Validate(new CreateAvailabilityRuleCommandRequest(Guid.Empty, DayOfWeek.Monday, new TimeOnly(8, 0), new TimeOnly(18, 0)));

        Assert.False(result.IsValid);
    }
}
