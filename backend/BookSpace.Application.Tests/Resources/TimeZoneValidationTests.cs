using BookSpace.Application.Resources;
using Xunit;

namespace BookSpace.Application.Tests.Resources;

// TimeZoneValidation was previously only exercised indirectly through CreateResourceCommandRequestValidatorTests
// - this gives it its own dedicated coverage, including the whitespace-only case that indirect coverage missed.
public sealed class TimeZoneValidationTests
{
    [Fact]
    public void BeAValidTimeZoneId_WithUtc_ReturnsTrue()
    {
        Assert.True(TimeZoneValidation.BeAValidTimeZoneId("UTC"));
    }

    [Fact]
    public void BeAValidTimeZoneId_WithARealIanaZone_ReturnsTrue()
    {
        Assert.True(TimeZoneValidation.BeAValidTimeZoneId("Europe/Sarajevo"));
    }

    [Fact]
    public void BeAValidTimeZoneId_WithAnUnknownZone_ReturnsFalse()
    {
        Assert.False(TimeZoneValidation.BeAValidTimeZoneId("Not/ARealZone"));
    }

    [Fact]
    public void BeAValidTimeZoneId_WithAnEmptyString_ReturnsFalse()
    {
        Assert.False(TimeZoneValidation.BeAValidTimeZoneId(""));
    }

    [Fact]
    public void BeAValidTimeZoneId_WithAWhitespaceOnlyString_ReturnsFalse()
    {
        Assert.False(TimeZoneValidation.BeAValidTimeZoneId("   "));
    }
}
