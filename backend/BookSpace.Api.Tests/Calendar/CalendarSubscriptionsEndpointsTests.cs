using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace BookSpace.Api.Tests.Calendar;

// Every endpoint here manages the CALLER's own subscription only - see CalendarSubscriptionsController.
// The anonymous feed endpoint itself (CalendarFeedController) is covered separately in
// CalendarFeedEndpointsTests, since it has a completely different auth model (a token, not a JWT).
public sealed class CalendarSubscriptionsEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly CustomWebApplicationFactory _factory;

    public CalendarSubscriptionsEndpointsTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task GetStatus_WithNoSubscriptionYet_ReturnsHasActiveSubscriptionFalse()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeApproverEmail);

        var response = await client.GetAsync("/api/calendar-subscriptions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<StatusResponse>(JsonOptions);
        Assert.False(body!.HasActiveSubscription);
        Assert.Null(body.CreatedAtUtc);
    }

    [Fact]
    public async Task Create_AsAMemberWithNoExistingSubscription_ReturnsAnAbsoluteFeedUrlContainingAToken()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeUnassignedApproverEmail);

        var response = await client.PostAsync("/api/calendar-subscriptions", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions);
        Assert.StartsWith("https://bookspace.tests.local/api/calendar-feed?token=", body!.FeedUrl);
    }

    [Fact]
    public async Task Create_ThenGetStatus_ReportsAnActiveSubscriptionWithoutExposingTheTokenOrUrl()
    {
        // Not the global SysAdmin - it has no TenantId and calendar subscriptions require tenant
        // membership (see CreateCalendarSubscriptionCommandHandler) - covered separately below.
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        await client.PostAsync("/api/calendar-subscriptions", content: null);

        var statusResponse = await client.GetAsync("/api/calendar-subscriptions");

        var body = await statusResponse.Content.ReadFromJsonAsync<StatusResponse>(JsonOptions);
        Assert.True(body!.HasActiveSubscription);
        Assert.NotNull(body.CreatedAtUtc);
        var rawJson = await statusResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("token", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("feedUrl", rawJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_WhenAnActiveSubscriptionAlreadyExists_ReturnsConflict()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.GlobexAdminEmail);
        await client.PostAsync("/api/calendar-subscriptions", content: null);

        var response = await client.PostAsync("/api/calendar-subscriptions", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("CalendarSubscription.AlreadyExists", await ReadErrorCodeAsync(response));
    }

    // The global SysAdmin has no TenantId and can never have bookings to export - see
    // CreateCalendarSubscriptionCommandHandler.
    [Fact]
    public async Task Create_AsTheGlobalSysAdmin_ReturnsConflict()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.GlobalSysAdminEmail);

        var response = await client.PostAsync("/api/calendar-subscriptions", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("CalendarSubscription.RequiresTenantMembership", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task Regenerate_ReturnsADifferentFeedUrl_AndTheOldOneStopsWorking()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.GlobexMemberEmail);
        var created = await (await client.PostAsync("/api/calendar-subscriptions", content: null)).Content.ReadFromJsonAsync<TokenResponse>(JsonOptions);

        var regenerateResponse = await client.PostAsync("/api/calendar-subscriptions/regenerate", content: null);

        Assert.Equal(HttpStatusCode.OK, regenerateResponse.StatusCode);
        var regenerated = await regenerateResponse.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions);
        Assert.NotEqual(created!.FeedUrl, regenerated!.FeedUrl);

        using var anonymousClient = _factory.CreateClient();
        var oldFeedResponse = await anonymousClient.GetAsync(ToRelativePath(created.FeedUrl));
        var newFeedResponse = await anonymousClient.GetAsync(ToRelativePath(regenerated.FeedUrl));
        Assert.Equal(HttpStatusCode.NotFound, oldFeedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, newFeedResponse.StatusCode);
    }

    [Fact]
    public async Task Regenerate_WithNoExistingSubscription_ReturnsNotFound()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        // Acme admin may already have one from a prior test in this class sharing the same database - make
        // sure none is active first, so this test genuinely exercises the "nothing to regenerate" path.
        await client.DeleteAsync("/api/calendar-subscriptions");

        var response = await client.PostAsync("/api/calendar-subscriptions/regenerate", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("CalendarSubscription.NotFound", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task Revoke_ReturnsNoContent_AndTheFeedUrlStopsWorkingImmediately()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var created = await (await client.PostAsync("/api/calendar-subscriptions", content: null)).Content.ReadFromJsonAsync<TokenResponse>(JsonOptions);

        var revokeResponse = await client.DeleteAsync("/api/calendar-subscriptions");

        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);
        var statusAfterRevoke = await (await client.GetAsync("/api/calendar-subscriptions")).Content.ReadFromJsonAsync<StatusResponse>(JsonOptions);
        Assert.False(statusAfterRevoke!.HasActiveSubscription);

        using var anonymousClient = _factory.CreateClient();
        var feedResponse = await anonymousClient.GetAsync(ToRelativePath(created!.FeedUrl));
        Assert.Equal(HttpStatusCode.NotFound, feedResponse.StatusCode);
    }

    [Fact]
    public async Task Revoke_WithNoExistingSubscription_ReturnsNotFound()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeApproverEmail);
        await client.DeleteAsync("/api/calendar-subscriptions"); // ensure none active

        var response = await client.DeleteAsync("/api/calendar-subscriptions");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("CalendarSubscription.NotFound", await ReadErrorCodeAsync(response));
    }

    [Theory]
    [InlineData("GET", "/api/calendar-subscriptions")]
    [InlineData("POST", "/api/calendar-subscriptions")]
    [InlineData("POST", "/api/calendar-subscriptions/regenerate")]
    [InlineData("DELETE", "/api/calendar-subscriptions")]
    public async Task ManagementEndpoints_WithoutAToken_ReturnUnauthorized(string method, string path)
    {
        using var client = _factory.CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static string ToRelativePath(string absoluteUrl) => new Uri(absoluteUrl).PathAndQuery;

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("errorCode", out var value) ? value.GetString() : null;
    }

    private async Task<HttpClient> AuthenticatedClientAsync(string email)
    {
        var client = _factory.CreateClient();
        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new { email, password = TestDataSeeder.Password });
        loginResponse.EnsureSuccessStatusCode();
        var loginBody = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>(JsonOptions);
        client.DefaultRequestHeaders.Authorization = new("Bearer", loginBody!.AccessToken);
        return client;
    }

    private sealed record LoginResponse(string AccessToken);

    private sealed record TokenResponse(string FeedUrl, DateTimeOffset CreatedAtUtc);

    private sealed record StatusResponse(bool HasActiveSubscription, DateTimeOffset? CreatedAtUtc);
}
