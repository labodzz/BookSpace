using BookSpace.Application.Bookings;
using Xunit;

namespace BookSpace.Application.Tests.Bookings;

public sealed class CancelBookingCommandRequestValidatorTests
{
    private readonly CancelBookingCommandRequestValidator _sut = new();

    [Fact]
    public void Validate_WithNullReason_HasNoErrors()
    {
        var result = _sut.Validate(new CancelBookingCommandRequest(Guid.NewGuid()));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WithReasonOfExactly1000Characters_HasNoErrors()
    {
        var result = _sut.Validate(new CancelBookingCommandRequest(Guid.NewGuid(), Reason: new string('a', 1000)));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WithReasonOf1001Characters_HasErrors()
    {
        var result = _sut.Validate(new CancelBookingCommandRequest(Guid.NewGuid(), Reason: new string('a', 1001)));

        Assert.False(result.IsValid);
    }
}
