using BookSpace.Application.Bookings;
using Xunit;

namespace BookSpace.Application.Tests.Bookings;

public sealed class ApproveBookingCommandRequestValidatorTests
{
    private readonly ApproveBookingCommandRequestValidator _sut = new();

    [Fact]
    public void Validate_WithValidCommand_HasNoErrors()
    {
        var result = _sut.Validate(new ApproveBookingCommandRequest(Guid.NewGuid(), "Looks good"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WithNullDecisionNote_HasNoErrors()
    {
        var result = _sut.Validate(new ApproveBookingCommandRequest(Guid.NewGuid(), null));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WithDecisionNoteOfExactly2000Characters_HasNoErrors()
    {
        var result = _sut.Validate(new ApproveBookingCommandRequest(Guid.NewGuid(), new string('a', 2000)));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WithDecisionNoteOf2001Characters_HasErrors()
    {
        var result = _sut.Validate(new ApproveBookingCommandRequest(Guid.NewGuid(), new string('a', 2001)));

        Assert.False(result.IsValid);
    }
}
