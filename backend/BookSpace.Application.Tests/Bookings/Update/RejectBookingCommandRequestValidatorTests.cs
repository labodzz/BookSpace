using BookSpace.Application.Bookings;
using Xunit;

namespace BookSpace.Application.Tests.Bookings;

public sealed class RejectBookingCommandRequestValidatorTests
{
    private readonly RejectBookingCommandRequestValidator _sut = new();

    [Fact]
    public void Validate_WithValidCommand_HasNoErrors()
    {
        var result = _sut.Validate(new RejectBookingCommandRequest(Guid.NewGuid(), "Conflicts with maintenance"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WithNullDecisionNote_HasNoErrors()
    {
        var result = _sut.Validate(new RejectBookingCommandRequest(Guid.NewGuid(), null));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WithDecisionNoteOfExactly2000Characters_HasNoErrors()
    {
        var result = _sut.Validate(new RejectBookingCommandRequest(Guid.NewGuid(), new string('a', 2000)));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WithDecisionNoteOf2001Characters_HasErrors()
    {
        var result = _sut.Validate(new RejectBookingCommandRequest(Guid.NewGuid(), new string('a', 2001)));

        Assert.False(result.IsValid);
    }
}
