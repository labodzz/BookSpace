using BookSpace.Api.Logging;
using Xunit;

namespace BookSpace.Api.Tests.Logging;

public sealed class CorrelationIdMiddlewareTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public CorrelationIdMiddlewareTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Request_WithoutCorrelationIdHeader_GetsOneGeneratedAndEchoedBack()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/db");

        var values = Assert.Single(response.Headers.GetValues(CorrelationIdMiddleware.HeaderName));
        Assert.False(string.IsNullOrWhiteSpace(values));
    }

    [Fact]
    public async Task Request_WithCorrelationIdHeader_EchoesTheSameValueBack()
    {
        using var client = _factory.CreateClient();
        var requestedCorrelationId = Guid.NewGuid().ToString();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/db");
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, requestedCorrelationId);

        var response = await client.SendAsync(request);

        var correlationId = Assert.Single(response.Headers.GetValues(CorrelationIdMiddleware.HeaderName));
        Assert.Equal(requestedCorrelationId, correlationId);
    }
}
