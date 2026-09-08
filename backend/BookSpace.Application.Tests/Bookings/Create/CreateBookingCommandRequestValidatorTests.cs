using BookSpace.Application.Bookings;
using Xunit;

namespace BookSpace.Application.Tests.Bookings;

public sealed class CreateBookingCommandRequestValidatorTests
{
    private readonly CreateBookingCommandRequestValidator _sut = new();

    private static readonly DateTimeOffset Start = DateTimeOffset.UtcNow.AddHours(1);

    [Fact]
    public void Validate_WithValidRequest_HasNoErrors()
    {
        var result = _sut.Validate(new CreateBookingCommandRequest(Guid.NewGuid(), Start, Start.AddHours(1), Quantity: 1));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WithEmptyResourceId_HasErrors()
    {
        var result = _sut.Validate(new CreateBookingCommandRequest(Guid.Empty, Start, Start.AddHours(1)));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithStartInThePast_HasErrors()
    {
        var result = _sut.Validate(new CreateBookingCommandRequest(Guid.NewGuid(), Start.AddHours(-2), Start.AddHours(-1)));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithEndEqualToStart_HasErrors()
    {
        var result = _sut.Validate(new CreateBookingCommandRequest(Guid.NewGuid(), Start, Start));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithEndBeforeStart_HasErrors()
    {
        var result = _sut.Validate(new CreateBookingCommandRequest(Guid.NewGuid(), Start, Start.AddHours(-1)));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithZeroQuantity_HasErrors()
    {
        var result = _sut.Validate(new CreateBookingCommandRequest(Guid.NewGuid(), Start, Start.AddHours(1), Quantity: 0));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithNegativeQuantity_HasErrors()
    {
        var result = _sut.Validate(new CreateBookingCommandRequest(Guid.NewGuid(), Start, Start.AddHours(1), Quantity: -1));

        Assert.False(result.IsValid);
    }
}
