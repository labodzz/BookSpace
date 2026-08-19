using System.Net;

namespace BookSpace.IntegrationTests;

public sealed class ApiSmokeTests(BookSpaceWebApplicationFactory factory)
    : IClassFixture<BookSpaceWebApplicationFactory>
{
    [Fact]
    public async Task Api_host_starts_successfully()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/");

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }
}
