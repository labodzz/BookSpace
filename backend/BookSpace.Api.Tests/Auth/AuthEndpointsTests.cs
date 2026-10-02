using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BookSpace.Api.Logging;
using Microsoft.AspNetCore.Http;
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

        var response = await client.PostAsJsonAsync("/api/auth/login", new
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

        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email = "",
            password = TestDataSeeder.Password,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemPayload>(JsonOptions);
        Assert.NotNull(problem);
        Assert.True(problem!.Errors.ContainsKey("Email"));
        Assert.True(response.Headers.Contains(CorrelationIdMiddleware.HeaderName));
    }

    [Fact]
    public async Task Refresh_WithMissingToken_ReturnsBadRequestWithFieldErrors()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemPayload>(JsonOptions);
        Assert.NotNull(problem);
        Assert.True(problem!.Errors.ContainsKey("RefreshToken"));
    }

    [Fact]
    public async Task Login_WithWrongPassword_ReturnsUnauthorized()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new
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

        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email = "nobody@bookspace.test",
            password = TestDataSeeder.Password,
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // A failed login/refresh is an ordinary result, not a thrown exception (see AuthController's
    // UnauthorizedProblemAsync), so this - not the IExceptionHandler pipeline - is what actually shapes
    // the 401 body. Asserts it matches the same ProblemDetails+errorCode+correlationId contract every
    // other error response in the API carries, and that a wrong password and an unknown email are
    // genuinely indistinguishable to the caller (same title, same detail, same errorCode).
    [Fact]
    public async Task Login_WithWrongPasswordOrUnknownEmail_ReturnsTheSameIndistinguishableProblemDetails()
    {
        using var client = _factory.CreateClient();

        var wrongPasswordResponse = await client.PostAsJsonAsync(
            "/api/auth/login", new { email = TestDataSeeder.AcmeAdminEmail, password = "definitely-wrong" });
        var unknownEmailResponse = await client.PostAsJsonAsync(
            "/api/auth/login", new { email = "nobody@bookspace.test", password = TestDataSeeder.Password });

        var wrongPasswordProblem = await wrongPasswordResponse.Content.ReadFromJsonAsync<ProblemPayload>(JsonOptions);
        var unknownEmailProblem = await unknownEmailResponse.Content.ReadFromJsonAsync<ProblemPayload>(JsonOptions);

        Assert.Equal(StatusCodes.Status401Unauthorized, wrongPasswordProblem!.Status);
        Assert.Equal("Auth.InvalidCredentials", wrongPasswordProblem.ErrorCode);
        Assert.True(wrongPasswordResponse.Headers.Contains(CorrelationIdMiddleware.HeaderName));
        Assert.NotNull(wrongPasswordProblem.CorrelationId);

        Assert.Equal(wrongPasswordProblem.Title, unknownEmailProblem!.Title);
        Assert.Equal(wrongPasswordProblem.Detail, unknownEmailProblem.Detail);
        Assert.Equal(wrongPasswordProblem.ErrorCode, unknownEmailProblem.ErrorCode);
    }

    [Fact]
    public async Task Refresh_WithValidToken_ReturnsRotatedTokens()
    {
        using var client = _factory.CreateClient();
        var loginBody = await LoginAsync(client, TestDataSeeder.AcmeAdminEmail);

        var response = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = loginBody.RefreshToken });

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
        await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = loginBody.RefreshToken });

        // Replay the original (now-rotated) token.
        var replayResponse = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = loginBody.RefreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, replayResponse.StatusCode);
        var payload = await replayResponse.Content.ReadAsStringAsync();
        Assert.Contains("revoked", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithoutToken_ReturnsUnauthorized()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/users/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithValidToken_ReturnsOkWithCurrentUserClaims()
    {
        using var client = _factory.CreateClient();
        var loginBody = await LoginAsync(client, TestDataSeeder.AcmeAdminEmail);
        client.DefaultRequestHeaders.Authorization = new("Bearer", loginBody.AccessToken);

        var response = await client.GetAsync("/api/users/me");

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

        var response = await client.GetAsync("/api/users");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminOnlyEndpoint_WithTenantAdminRole_ReturnsOnlyThatTenantsUsers()
    {
        using var client = _factory.CreateClient();
        var loginBody = await LoginAsync(client, TestDataSeeder.AcmeAdminEmail);
        client.DefaultRequestHeaders.Authorization = new("Bearer", loginBody.AccessToken);

        var response = await client.GetAsync("/api/users");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.Contains(TestDataSeeder.AcmeAdminEmail, payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TestDataSeeder.GlobexMemberEmail, payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Logout_WithValidToken_RevokesItSoASubsequentRefreshIsUnauthorized()
    {
        using var client = _factory.CreateClient();
        var loginBody = await LoginAsync(client, TestDataSeeder.GlobexMemberEmail);

        var logoutResponse = await client.PostAsJsonAsync("/api/auth/logout", new { refreshToken = loginBody.RefreshToken });
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);

        var refreshAfterLogout = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = loginBody.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refreshAfterLogout.StatusCode);
    }

    // An explicit Logout click from a tab holding an old, already-rotated token (e.g. it was asleep
    // through a sibling tab's refresh, or missed the BroadcastChannel update) must still end the
    // session for the currently-active descendant - logout means "kill this device's session," and the
    // stale local copy of one of its tokens is still a legitimate way to identify that lineage. This is
    // safe because the frontend's automatic reaction to a failed refresh never reaches this endpoint at
    // all (see AuthService.clearExpiredSession) - only a real, explicit Logout click does.
    [Fact]
    public async Task Logout_WithAlreadyRotatedToken_StillRevokesTheCurrentlyActiveSession()
    {
        using var client = _factory.CreateClient();
        var loginBody = await LoginAsync(client, TestDataSeeder.GlobexMemberEmail);

        var refreshResponse = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = loginBody.RefreshToken });
        var rotated = await refreshResponse.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);

        var logoutResponse = await client.PostAsJsonAsync("/api/auth/logout", new { refreshToken = loginBody.RefreshToken });
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);

        var refreshAfterLogout = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = rotated!.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refreshAfterLogout.StatusCode);
    }

    [Fact]
    public async Task Logout_WithUnknownToken_StillReturnsNoContent()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/logout", new { refreshToken = "not-a-real-token" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Logout_WithMissingToken_ReturnsBadRequestWithFieldErrors()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/logout", new { refreshToken = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemPayload>(JsonOptions);
        Assert.NotNull(problem);
        Assert.True(problem!.Errors.ContainsKey("RefreshToken"));
    }

    private static async Task<AuthResponse> LoginAsync(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email, password = TestDataSeeder.Password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions))!;
    }

    private sealed record AuthResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAtUtc, string RefreshToken, DateTimeOffset RefreshTokenExpiresAtUtc);

    private sealed record ValidationProblemPayload(string? Title, int? Status, Dictionary<string, string[]> Errors);

    private sealed record ProblemPayload(string? Title, string? Detail, int? Status, string? ErrorCode, string? CorrelationId);
}
