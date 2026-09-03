using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace BookSpace.Api.Tests.ResourceTypes;

// Seeding happens once in CustomWebApplicationFactory.InitializeAsync - see TestDataSeeder for the
// fixed Acme/Globex resource types this suite reads.
public sealed class ResourceTypesEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly CustomWebApplicationFactory _factory;

    public ResourceTypesEndpointsTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task CreateResourceType_AsTenantAdmin_ReturnsOkWithCreatedResourceType()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var name = $"Parking Spot {Guid.NewGuid()}";

        var response = await client.PostAsJsonAsync("/resource-types", new { name });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ResourceTypeResponse>(JsonOptions);
        Assert.Equal(name, body!.Name);
    }

    [Fact]
    public async Task CreateResourceType_AsSysAdmin_ReturnsOkWithCreatedResourceType()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeSysAdminEmail);

        var response = await client.PostAsJsonAsync("/resource-types", new { name = $"Parking Spot {Guid.NewGuid()}" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CreateResourceType_AsMember_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);

        var response = await client.PostAsJsonAsync("/resource-types", new { name = $"Parking Spot {Guid.NewGuid()}" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateResourceType_WithDuplicateNameInSameTenant_ReturnsConflict()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var name = $"Parking Spot {Guid.NewGuid()}";
        await CreateResourceTypeAsync(client, name);

        var response = await client.PostAsJsonAsync("/resource-types", new { name });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreateResourceType_WithEmptyName_ReturnsBadRequest()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.PostAsJsonAsync("/resource-types", new { name = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetResourceTypes_AsMember_ReturnsOk()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);

        var response = await client.GetAsync("/resource-types");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetResourceTypes_ReturnsOnlyCallersTenantResourceTypes()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.GetAsync("/resource-types");

        var body = await response.Content.ReadFromJsonAsync<List<ResourceTypeResponse>>(JsonOptions);
        Assert.Contains(body!, item => item.Id == TestDataSeeder.ResourceTypeId);
        Assert.DoesNotContain(body!, item => item.Id == TestDataSeeder.GlobexResourceTypeId);
    }

    [Fact]
    public async Task GetResourceType_ForAnotherTenantsResourceType_ReturnsNotFound()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.GetAsync($"/resource-types/{TestDataSeeder.GlobexResourceTypeId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateResourceType_AsTenantAdmin_RenamesSuccessfully()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var created = await CreateResourceTypeAsync(client, $"Parking Spot {Guid.NewGuid()}");
        var newName = $"Renamed {Guid.NewGuid()}";

        var response = await client.PutAsJsonAsync($"/resource-types/{created.Id}", new { name = newName });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ResourceTypeResponse>(JsonOptions);
        Assert.Equal(newName, body!.Name);
    }

    [Fact]
    public async Task UpdateResourceType_AsMember_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);

        var response = await client.PutAsJsonAsync($"/resource-types/{TestDataSeeder.ResourceTypeId}", new { name = "Whatever" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UpdateResourceType_WithUnknownId_ReturnsNotFound()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.PutAsJsonAsync($"/resource-types/{Guid.NewGuid()}", new { name = "Whatever" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteResourceType_Unused_ReturnsNoContent()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var created = await CreateResourceTypeAsync(client, $"Parking Spot {Guid.NewGuid()}");

        var response = await client.DeleteAsync($"/resource-types/{created.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task DeleteResourceType_StillReferencedByAResource_ReturnsConflict()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.DeleteAsync($"/resource-types/{TestDataSeeder.ResourceTypeId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task DeleteResourceType_AsMember_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);

        var response = await client.DeleteAsync($"/resource-types/{TestDataSeeder.ResourceTypeId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<ResourceTypeResponse> CreateResourceTypeAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/resource-types", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ResourceTypeResponse>(JsonOptions))!;
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

    private sealed record ResourceTypeResponse(Guid Id, string Name);
}
