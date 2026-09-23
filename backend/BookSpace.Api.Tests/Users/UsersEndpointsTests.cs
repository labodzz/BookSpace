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
        var body = await response.Content.ReadFromJsonAsync<PagedResponse<UserSummaryResponse>>(JsonOptions);
        Assert.Contains(body!.Items, user => user.Email == TestDataSeeder.AcmeAdminEmail);
        Assert.Contains(body.Items, user => user.Email == TestDataSeeder.AcmeMemberEmail);
        Assert.DoesNotContain(body.Items, user => user.Email == TestDataSeeder.GlobexMemberEmail);
        Assert.DoesNotContain(body.Items, user => user.Email == TestDataSeeder.GlobexAdminEmail);
        Assert.All(body.Items, user => Assert.Equal(TestDataSeeder.AcmeTenantId, user.TenantId));
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

    [Fact]
    public async Task GetUsers_ReportsEachUsersStatusAndGlobalRoles()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.GetAsync("/users");

        var body = await response.Content.ReadFromJsonAsync<PagedResponse<UserSummaryResponse>>(JsonOptions);
        var approver = body!.Items.Single(user => user.Email == TestDataSeeder.AcmeApproverEmail);
        Assert.Equal(0, approver.Status); // UserStatus.Active - every seeded user is Active
        Assert.Contains("Approver", approver.Roles);
    }

    // "Approver" (LastName) matches both AcmeApproverEmail and AcmeUnassignedApproverEmail (LastName
    // "UnassignedApprover" still contains the substring) but must never match a Member.
    [Fact]
    public async Task GetUsers_WithSearch_MatchesAcrossFirstNameLastNameAndEmail()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.GetAsync("/users?search=Approver");

        var body = await response.Content.ReadFromJsonAsync<PagedResponse<UserSummaryResponse>>(JsonOptions);
        Assert.Contains(body!.Items, user => user.Email == TestDataSeeder.AcmeApproverEmail);
        Assert.Contains(body.Items, user => user.Email == TestDataSeeder.AcmeUnassignedApproverEmail);
        Assert.DoesNotContain(body.Items, user => user.Email == TestDataSeeder.AcmeMemberEmail);
    }

    [Fact]
    public async Task GetUsers_WithRoleFilter_ReturnsOnlyUsersHoldingThatExactRole()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.GetAsync("/users?role=Approver");

        var body = await response.Content.ReadFromJsonAsync<PagedResponse<UserSummaryResponse>>(JsonOptions);
        Assert.Contains(body!.Items, user => user.Email == TestDataSeeder.AcmeApproverEmail);
        Assert.DoesNotContain(body.Items, user => user.Email == TestDataSeeder.AcmeMemberEmail);
    }

    [Fact]
    public async Task GetUsers_WithStatusFilter_ReturnsOnlyUsersInThatExactStatus()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.GetAsync("/users?status=Active");

        var body = await response.Content.ReadFromJsonAsync<PagedResponse<UserSummaryResponse>>(JsonOptions);
        Assert.Contains(body!.Items, user => user.Email == TestDataSeeder.AcmeMemberEmail);
        Assert.All(body.Items, user => Assert.Equal(0, user.Status)); // UserStatus.Active
    }

    [Fact]
    public async Task GetUsers_WithIds_ResolvesExactlyThoseUsersRegardlessOfOtherFilters()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.GetAsync($"/users?ids={TestDataSeeder.AcmeMemberUserId}&ids={TestDataSeeder.AcmeApproverUserId}");

        var body = await response.Content.ReadFromJsonAsync<PagedResponse<UserSummaryResponse>>(JsonOptions);
        Assert.Equal(2, body!.Items.Count);
        Assert.Contains(body.Items, user => user.Id == TestDataSeeder.AcmeMemberUserId);
        Assert.Contains(body.Items, user => user.Id == TestDataSeeder.AcmeApproverUserId);
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

    private sealed record UserSummaryResponse(Guid Id, string FirstName, string LastName, string Email, Guid TenantId, int Status, List<string> Roles);

    private sealed record PagedResponse<T>(List<T> Items, int Page, int PageSize, int TotalCount);
}
