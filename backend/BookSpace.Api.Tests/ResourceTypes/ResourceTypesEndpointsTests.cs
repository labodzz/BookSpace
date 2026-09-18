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
    public async Task CreateResourceType_AsTenantAdmin_ReturnsCreatedWithCreatedResourceType()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var name = $"Parking Spot {Guid.NewGuid()}";

        var response = await client.PostAsJsonAsync("/resource-types", new { name });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ResourceTypeResponse>(JsonOptions);
        Assert.Equal(name, body!.Name);
    }

    [Fact]
    public async Task CreateResourceType_AsSysAdmin_ReturnsCreatedWithCreatedResourceType()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeSysAdminEmail);

        var response = await client.PostAsJsonAsync("/resource-types", new { name = $"Parking Spot {Guid.NewGuid()}" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateResourceType_AsMember_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);

        var response = await client.PostAsJsonAsync("/resource-types", new { name = $"Parking Spot {Guid.NewGuid()}" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateResourceType_AsApprover_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeApproverEmail);

        var response = await client.PostAsJsonAsync("/resource-types", new { name = $"Approver Denied {Guid.NewGuid()}" });

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

    // ExistsByNameAsync has no explicit tenant parameter - relies entirely on the EF global query filter.
    // Only same-tenant duplicate rejection was tested before; a regression that broadened this check
    // tenant-globally would silently block legitimate cross-tenant creates and nothing would fail.
    [Fact]
    public async Task CreateResourceType_WithNameAlreadyUsedByAnotherTenant_Succeeds()
    {
        var name = $"Cross Tenant Type {Guid.NewGuid()}";
        using var globexClient = await AuthenticatedClientAsync(TestDataSeeder.GlobexAdminEmail);
        await CreateResourceTypeAsync(globexClient, name);

        using var acmeClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var response = await acmeClient.PostAsJsonAsync("/resource-types", new { name });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
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
    public async Task UpdateResourceType_ForAnotherTenantsResourceType_ReturnsNotFound()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.PutAsJsonAsync($"/resource-types/{TestDataSeeder.GlobexResourceTypeId}", new { name = "Should Not Apply" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateResourceType_AsSysAdmin_ReturnsOk()
    {
        using var adminClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var created = await CreateResourceTypeAsync(adminClient, $"SysAdminUpdateTarget {Guid.NewGuid()}");
        using var sysAdminClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeSysAdminEmail);

        var response = await sysAdminClient.PutAsJsonAsync($"/resource-types/{created.Id}", new { name = $"Renamed By SysAdmin {Guid.NewGuid()}" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UpdateResourceType_AsApprover_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeApproverEmail);

        var response = await client.PutAsJsonAsync($"/resource-types/{TestDataSeeder.ResourceTypeId}", new { name = "Should Be Forbidden" });

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
    public async Task DeleteResourceType_AsApprover_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeApproverEmail);

        var response = await client.DeleteAsync($"/resource-types/{TestDataSeeder.ResourceTypeId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DeleteResourceType_AsMember_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);

        var response = await client.DeleteAsync($"/resource-types/{TestDataSeeder.ResourceTypeId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DeleteResourceType_ForAnotherTenantsResourceType_ReturnsNotFound()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.DeleteAsync($"/resource-types/{TestDataSeeder.GlobexResourceTypeId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteResourceType_AsSysAdmin_ReturnsNoContent()
    {
        using var adminClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var created = await CreateResourceTypeAsync(adminClient, $"SysAdminDeleteTarget {Guid.NewGuid()}");
        using var sysAdminClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeSysAdminEmail);

        var response = await sysAdminClient.DeleteAsync($"/resource-types/{created.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
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
