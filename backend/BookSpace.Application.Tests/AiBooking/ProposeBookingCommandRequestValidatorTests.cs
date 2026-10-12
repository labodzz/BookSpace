using BookSpace.Application.AiBooking;
using Xunit;

namespace BookSpace.Application.Tests.AiBooking;

public sealed class ProposeBookingCommandRequestValidatorTests
{
    private readonly ProposeBookingCommandRequestValidator _sut = new();

    [Fact]
    public void Validate_WithAReasonableRequest_HasNoErrors()
    {
        var result = _sut.Validate(new ProposeBookingCommandRequest("Book a lab for 3 people next Tuesday from 2 to 4 PM."));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WithEmptyText_HasErrors()
    {
        var result = _sut.Validate(new ProposeBookingCommandRequest(""));

        Assert.False(result.IsValid);
    }

    // Bounds how much untrusted free text is ever sent to the LLM per call - see "Limit input length" in
    // the task brief.
    [Fact]
    public void Validate_WithTextLongerThanTheMaximum_HasErrors()
    {
        var result = _sut.Validate(new ProposeBookingCommandRequest(new string('a', ProposeBookingCommandRequestValidator.MaxTextLength + 1)));

        Assert.False(result.IsValid);
    }

    // Bounds how large the client-held conversation transcript can grow - see ProposeBookingCommandRequest's
    // own doc comment on why this is bounded rather than an open-ended chat log.
    [Fact]
    public void Validate_WithMoreThanTheMaximumPriorTurns_HasErrors()
    {
        var tooManyTurns = Enumerable.Range(0, ProposeBookingCommandRequestValidator.MaxPriorTurns + 1)
            .Select(_ => new ProposeBookingTurnRequest("user", "hi"))
            .ToList();

        var result = _sut.Validate(new ProposeBookingCommandRequest("Book a lab tomorrow.", tooManyTurns));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithAnInvalidTurnRole_HasErrors()
    {
        var result = _sut.Validate(new ProposeBookingCommandRequest("Book a lab tomorrow.", [new ProposeBookingTurnRequest("system", "hi")]));

        Assert.False(result.IsValid);
    }
}
