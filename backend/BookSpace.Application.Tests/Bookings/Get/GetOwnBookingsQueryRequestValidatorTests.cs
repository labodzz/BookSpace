using BookSpace.Application.Bookings;
using Xunit;

namespace BookSpace.Application.Tests.Bookings;

public sealed class GetOwnBookingsQueryRequestValidatorTests
{
    private readonly GetOwnBookingsQueryRequestValidator _sut = new();

    [Fact]
    public void Validate_WithDefaultPaging_HasNoErrors()
    {
        var result = _sut.Validate(new GetOwnBookingsQueryRequest());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WithPageBelowOne_HasErrors()
    {
        var result = _sut.Validate(new GetOwnBookingsQueryRequest(Page: 0));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithPageSizeAboveOneHundred_HasErrors()
    {
        var result = _sut.Validate(new GetOwnBookingsQueryRequest(PageSize: 101));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithPageSizeBelowOne_HasErrors()
    {
        var result = _sut.Validate(new GetOwnBookingsQueryRequest(PageSize: 0));

        Assert.False(result.IsValid);
    }
}
