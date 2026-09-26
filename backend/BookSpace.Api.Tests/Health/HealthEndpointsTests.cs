using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace BookSpace.Api.Tests.Health;

// /health (liveness) and /health/db (readiness) are both [AllowAnonymous] - an orchestrator's health
// probe carries no bearer token, so the global authenticated-fallback policy must never gate either of
// them. See docs/container-deployment.md for how Azure Container Apps is expected to use these.
public sealed class HealthEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly CustomWebApplicationFactory _factory;

    public HealthEndpointsTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task GetHealth_WithNoAuthorizationHeader_ReturnsOkWithoutTouchingTheDatabase()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<StatusPayload>(JsonOptions);
        Assert.Equal("healthy", body!.Status);
    }

    [Fact]
    public async Task GetHealthDb_WithNoAuthorizationHeader_ReturnsOkWhenTheDatabaseIsReachable()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/db");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<StatusPayload>(JsonOptions);
        Assert.Equal("connected", body!.Status);
    }

    // The endpoint used to echo dbContext.Database.GetDbConnection().Database in the response - a
    // public, anonymous endpoint has no reason to disclose the configured database's name.
    [Fact]
    public async Task GetHealthDb_ResponseBody_NeverIncludesTheDatabaseName()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/db");

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("database", raw, StringComparison.OrdinalIgnoreCase);
    }

    // A deep link with no matching controller route or static file must not be gated by the global
    // authenticated-fallback policy - an unauthenticated browser refresh on e.g. /login must reach
    // Angular's own router, not a 401 the SPA never gets a chance to handle. The test project's content
    // root has no built wwwroot/index.html to actually serve (that only exists inside the Docker image -
    // see docs/container-deployment.md), so the file itself 404s here; what this proves is that the
    // fallback route is reached at all, unauthenticated, rather than being rejected before it runs.
    [Fact]
    public async Task GetAnUnmatchedDeepLink_WithNoAuthorizationHeader_IsNotRejectedByTheFallbackAuthorizationPolicy()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/login");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private sealed record StatusPayload(string Status);
}
