using Xunit;

namespace BookSpace.Api.Tests.Cors;

// CustomWebApplicationFactory configures Cors:AllowedOrigins to exactly one origin
// (http://localhost:4200, the Angular dev-server default) via the same Cors__AllowedOrigins__0
// environment-variable mechanism the Auth section already uses. /health/db is used as the target
// endpoint since it's [AllowAnonymous] and needs no bearer token to reach.
public sealed class CorsEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string AllowedOrigin = "http://localhost:4200";

    private readonly CustomWebApplicationFactory _factory;

    public CorsEndpointsTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Request_FromAnAllowedOrigin_ReceivesTheMatchingAccessControlAllowOriginHeader()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/db");
        request.Headers.Add("Origin", AllowedOrigin);

        var response = await client.SendAsync(request);

        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values));
        Assert.Equal(AllowedOrigin, Assert.Single(values));
    }

    // The policy fails closed: an origin that was never configured gets no CORS header at all - not a
    // rejection status code (CORS is enforced by the browser refusing to expose the response, not by
    // the server refusing to process the request), but the header a browser checks is absent.
    [Fact]
    public async Task Request_FromADisallowedOrigin_ReceivesNoAccessControlAllowOriginHeader()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/db");
        request.Headers.Add("Origin", "http://evil.test");

        var response = await client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task PreflightRequest_FromAnAllowedOrigin_IsAccepted()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/health/db");
        request.Headers.Add("Origin", AllowedOrigin);
        request.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await client.SendAsync(request);

        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values));
        Assert.Equal(AllowedOrigin, Assert.Single(values));
    }

    [Fact]
    public async Task PreflightRequest_FromADisallowedOrigin_ReceivesNoAccessControlAllowOriginHeader()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/health/db");
        request.Headers.Add("Origin", "http://evil.test");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
