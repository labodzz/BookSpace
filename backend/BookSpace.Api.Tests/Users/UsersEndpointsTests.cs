using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace BookSpace.Api.Tests.Users;

// UsersController had no dedicated test file at all before this - both its routes were only ever
// exercised incidentally inside Auth/AuthEndpointsTests.cs (which is really testing the auth/
// authorization pipeline, not UsersController's own behavior). GET /users specifically carries an
// explicit written claim in the controller ("this endpoint cannot leak another tenant's users even if
// someone forgets to add a filter") that had never been directly, end-to-end verified.
public sealed class UsersEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly CustomWebApplicationFactory _factory;

    public UsersEndpointsTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task GetUsers_AsTenantAdmin_ReturnsOnlyThatTenantsUsersNeverAnotherTenants()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.GetAsync("/users");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<List<UserSummaryResponse>>(JsonOptions);
        Assert.Contains(body!, user => user.Email == TestDataSeeder.AcmeAdminEmail);
        Assert.Contains(body!, user => user.Email == TestDataSeeder.AcmeMemberEmail);
        Assert.DoesNotContain(body!, user => user.Email == TestDataSeeder.GlobexMemberEmail);
        Assert.DoesNotContain(body!, user => user.Email == TestDataSeeder.GlobexAdminEmail);
        Assert.All(body!, user => Assert.Equal(TestDataSeeder.AcmeTenantId, user.TenantId));
    }

    [Fact]
    public async Task GetUsers_AsSysAdmin_ReturnsOk()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeSysAdminEmail);

        var response = await client.GetAsync("/users");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetUsers_AsMember_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);

        var response = await client.GetAsync("/users");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetUsers_WithoutToken_ReturnsUnauthorized()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/users");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<HttpClient> AuthenticatedClientAsync(string email)
    {
        var client = _factory.CreateClient();
        var loginResponse = await client.PostAsJsonAsync("/auth/login", new { email, password = TestDataSeeder.Password });
        loginResponse.EnsureSuccessStatusCode();
        var loginBody = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>(JsonOptions);
        client.DefaultRequestHeaders.Authorization = new("Bearer", loginBody!.AccessToken);
        return client;
    }

    private sealed record LoginResponse(string AccessToken);

    private sealed record UserSummaryResponse(Guid Id, string FirstName, string LastName, string Email, Guid TenantId);
}
