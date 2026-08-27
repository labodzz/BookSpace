using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace BookSpace.Api.Tests.Auth;

// Seeding happens once in CustomWebApplicationFactory.InitializeAsync (an IAsyncLifetime fixture,
// initialized once per test class), not here - IClassFixture gives each test method its own
// AuthEndpointsTests instance but the same shared factory/database.
public sealed class AuthEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly CustomWebApplicationFactory _factory;

    public AuthEndpointsTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Login_WithValidCredentials_ReturnsOkWithTokens()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/login", new
        {
            email = TestDataSeeder.AcmeAdminEmail,
            password = TestDataSeeder.Password,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        Assert.False(string.IsNullOrWhiteSpace(body!.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(body.RefreshToken));
    }

    [Fact]
    public async Task Login_WithMissingEmail_ReturnsBadRequestWithFieldErrors()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/login", new
        {
            email = "",
            password = TestDataSeeder.Password,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemPayload>(JsonOptions);
        Assert.NotNull(problem);
        Assert.True(problem!.Errors.ContainsKey("Email"));
    }

    [Fact]
    public async Task Refresh_WithMissingToken_ReturnsBadRequestWithFieldErrors()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/refresh", new { refreshToken = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemPayload>(JsonOptions);
        Assert.NotNull(problem);
        Assert.True(problem!.Errors.ContainsKey("RefreshToken"));
    }

    [Fact]
    public async Task Login_WithWrongPassword_ReturnsUnauthorized()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/login", new
        {
            email = TestDataSeeder.AcmeAdminEmail,
            password = "definitely-wrong",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithUnknownEmail_ReturnsUnauthorized()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/login", new
        {
            email = "nobody@bookspace.test",
            password = TestDataSeeder.Password,
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_WithValidToken_ReturnsRotatedTokens()
    {
        using var client = _factory.CreateClient();
        var loginBody = await LoginAsync(client, TestDataSeeder.AcmeAdminEmail);

        var response = await client.PostAsJsonAsync("/auth/refresh", new { refreshToken = loginBody.RefreshToken });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var refreshed = await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        // The refresh token always rotates. The access token is a stateless JWT with second-granularity
        // claims, so it can legitimately come out byte-identical when issued within the same second as
        // the original login - only the refresh token's uniqueness is a meaningful contract here.
        Assert.NotEqual(loginBody.RefreshToken, refreshed!.RefreshToken);
        Assert.False(string.IsNullOrWhiteSpace(refreshed.AccessToken));
    }

    [Fact]
    public async Task Refresh_ReplayingAnAlreadyRotatedToken_ReturnsUnauthorizedWithReuseMessage()
    {
        using var client = _factory.CreateClient();
        var loginBody = await LoginAsync(client, TestDataSeeder.GlobexMemberEmail);

        // Rotate once so the original token becomes "already replaced".
        await client.PostAsJsonAsync("/auth/refresh", new { refreshToken = loginBody.RefreshToken });

        // Replay the original (now-rotated) token.
        var replayResponse = await client.PostAsJsonAsync("/auth/refresh", new { refreshToken = loginBody.RefreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, replayResponse.StatusCode);
        var payload = await replayResponse.Content.ReadAsStringAsync();
        Assert.Contains("revoked", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithoutToken_ReturnsUnauthorized()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/users/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithValidToken_ReturnsOkWithCurrentUserClaims()
    {
        using var client = _factory.CreateClient();
        var loginBody = await LoginAsync(client, TestDataSeeder.AcmeAdminEmail);
        client.DefaultRequestHeaders.Authorization = new("Bearer", loginBody.AccessToken);

        var response = await client.GetAsync("/users/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.Contains(TestDataSeeder.AcmeAdminUserId.ToString(), payload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(TestDataSeeder.AcmeTenantId.ToString(), payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdminOnlyEndpoint_WithMemberRole_ReturnsForbidden()
    {
        using var client = _factory.CreateClient();
        var loginBody = await LoginAsync(client, TestDataSeeder.GlobexMemberEmail);
        client.DefaultRequestHeaders.Authorization = new("Bearer", loginBody.AccessToken);

        var response = await client.GetAsync("/users");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminOnlyEndpoint_WithTenantAdminRole_ReturnsOnlyThatTenantsUsers()
    {
        using var client = _factory.CreateClient();
        var loginBody = await LoginAsync(client, TestDataSeeder.AcmeAdminEmail);
        client.DefaultRequestHeaders.Authorization = new("Bearer", loginBody.AccessToken);

        var response = await client.GetAsync("/users");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.Contains(TestDataSeeder.AcmeAdminEmail, payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TestDataSeeder.GlobexMemberEmail, payload, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<AuthResponse> LoginAsync(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync("/auth/login", new { email, password = TestDataSeeder.Password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions))!;
    }

    private sealed record AuthResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAtUtc, string RefreshToken, DateTimeOffset RefreshTokenExpiresAtUtc);

    private sealed record ValidationProblemPayload(string? Title, int? Status, Dictionary<string, string[]> Errors);
}
