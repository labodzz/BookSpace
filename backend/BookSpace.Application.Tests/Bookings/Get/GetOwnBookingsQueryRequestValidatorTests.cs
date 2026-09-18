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

    [Fact]
    public void Validate_WithPageSizeOfExactlyOneHundred_HasNoErrors()
    {
        var result = _sut.Validate(new GetOwnBookingsQueryRequest(PageSize: 100));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WithPageSizeOfExactlyOne_HasNoErrors()
    {
        var result = _sut.Validate(new GetOwnBookingsQueryRequest(PageSize: 1));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WithToUtcAfterFromUtc_HasNoErrors()
    {
        var fromUtc = DateTimeOffset.UtcNow;
        var result = _sut.Validate(new GetOwnBookingsQueryRequest(FromUtc: fromUtc, ToUtc: fromUtc.AddDays(1)));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WithToUtcNotAfterFromUtc_HasErrors()
    {
        var fromUtc = DateTimeOffset.UtcNow;
        var result = _sut.Validate(new GetOwnBookingsQueryRequest(FromUtc: fromUtc, ToUtc: fromUtc));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithOnlyFromUtc_HasNoErrors()
    {
        var result = _sut.Validate(new GetOwnBookingsQueryRequest(FromUtc: DateTimeOffset.UtcNow));

        Assert.True(result.IsValid);
    }
}
