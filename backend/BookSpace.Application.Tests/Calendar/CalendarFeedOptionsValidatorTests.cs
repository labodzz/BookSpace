using BookSpace.Application.Calendar;
using Xunit;

namespace BookSpace.Application.Tests.Calendar;

public sealed class CalendarFeedOptionsValidatorTests
{
    private readonly CalendarFeedOptionsValidator _validator = new();

    [Fact]
    public void Validate_WithAFullyConfiguredOptions_Succeeds()
    {
        var result = _validator.Validate(name: null, new CalendarFeedOptions { PublicBaseUrl = "https://bookspace.example.com" });

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithAMissingPublicBaseUrl_Fails(string? publicBaseUrl)
    {
        var result = _validator.Validate(name: null, new CalendarFeedOptions { PublicBaseUrl = publicBaseUrl });

        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("/relative/path")]
    [InlineData("ftp://bookspace.example.com")]
    public void Validate_WithAMalformedOrNonHttpPublicBaseUrl_Fails(string publicBaseUrl)
    {
        var result = _validator.Validate(name: null, new CalendarFeedOptions { PublicBaseUrl = publicBaseUrl });

        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WithAZeroOrNegativePastWindowDays_Fails(int pastWindowDays)
    {
        var result = _validator.Validate(name: null, new CalendarFeedOptions { PublicBaseUrl = "https://bookspace.example.com", PastWindowDays = pastWindowDays });

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_WithAPastWindowDaysAboveTheMax_Fails()
    {
        var result = _validator.Validate(
            name: null,
            new CalendarFeedOptions { PublicBaseUrl = "https://bookspace.example.com", PastWindowDays = CalendarFeedOptions.MaxPastWindowDays + 1 });

        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WithAZeroOrNegativeFutureWindowDays_Fails(int futureWindowDays)
    {
        var result = _validator.Validate(name: null, new CalendarFeedOptions { PublicBaseUrl = "https://bookspace.example.com", FutureWindowDays = futureWindowDays });

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_WithAFutureWindowDaysAboveTheMax_Fails()
    {
        var result = _validator.Validate(
            name: null,
            new CalendarFeedOptions { PublicBaseUrl = "https://bookspace.example.com", FutureWindowDays = CalendarFeedOptions.MaxFutureWindowDays + 1 });

        Assert.True(result.Failed);
    }
}
