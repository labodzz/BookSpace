using BookSpace.Application.AiBooking;
using Xunit;

namespace BookSpace.Application.Tests.AiBooking;

public sealed class AiBookingOptionsValidatorTests
{
    private readonly AiBookingOptionsValidator _sut = new();

    // The core "disabled by default, works with zero configuration" requirement - see the task's own
    // "When disabled, the app must start and operate normally without an LLM API key".
    [Fact]
    public void Validate_WhenDisabled_SucceedsEvenWithNoApiKeyConfigured()
    {
        var result = _sut.Validate(null, new AiBookingOptions { Enabled = false });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_WhenEnabledWithNoApiKey_Fails()
    {
        var result = _sut.Validate(null, new AiBookingOptions { Enabled = true, ApiKey = "" });

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_WhenEnabledWithAllRequiredValues_Succeeds()
    {
        var result = _sut.Validate(null, new AiBookingOptions { Enabled = true, ApiKey = "sk-test" });

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.com")]
    public void Validate_WhenEnabledWithAnInvalidBaseUrl_Fails(string baseUrl)
    {
        var result = _sut.Validate(null, new AiBookingOptions { Enabled = true, ApiKey = "sk-test", BaseUrl = baseUrl });

        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(61)]
    public void Validate_WhenEnabledWithAnOutOfRangeTimeout_Fails(int timeoutSeconds)
    {
        var result = _sut.Validate(null, new AiBookingOptions { Enabled = true, ApiKey = "sk-test", RequestTimeoutSeconds = timeoutSeconds });

        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2001)]
    public void Validate_WhenEnabledWithAnOutOfRangeMaxOutputTokens_Fails(int maxOutputTokens)
    {
        var result = _sut.Validate(null, new AiBookingOptions { Enabled = true, ApiKey = "sk-test", MaxOutputTokens = maxOutputTokens });

        Assert.True(result.Failed);
    }
}
