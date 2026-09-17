using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BookSpace.Domain.Enums;
using Xunit;

namespace BookSpace.Api.Tests.Resources;

// Seeding happens once in CustomWebApplicationFactory.InitializeAsync - see TestDataSeeder for the
// fixed Acme resource/rules/blackout/bookings this suite reads.
public sealed class ResourcesEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly CustomWebApplicationFactory _factory;

    public ResourcesEndpointsTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task CreateResource_AsTenantAdmin_ReturnsCreatedWithCreatedResource()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.PostAsJsonAsync("/resources", new
        {
            resourceTypeId = TestDataSeeder.ResourceTypeId,
            name = $"New Resource {Guid.NewGuid()}",
            description = (string?)null,
            capacity = 4,
            requiresApproval = false,
            timeZoneId = "UTC",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ResourceResponse>(JsonOptions);
        Assert.Equal(4, body!.Capacity);
        Assert.Equal(ResourceStatus.Active, body.Status);
    }

    // [Authorize(Roles = "TenantAdmin,SysAdmin")] on every mutation in this controller had never
    // actually been exercised with a real SysAdmin login before this test - only TenantAdmin's half
    // of that role list was ever proven to work.
    [Fact]
    public async Task CreateResource_AsSysAdmin_ReturnsCreatedWithCreatedResource()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeSysAdminEmail);

        var response = await client.PostAsJsonAsync("/resources", new
        {
            resourceTypeId = TestDataSeeder.ResourceTypeId,
            name = $"New Resource {Guid.NewGuid()}",
            description = (string?)null,
            capacity = 4,
            requiresApproval = false,
            timeZoneId = "UTC",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateResource_AsMember_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);

        var response = await client.PostAsJsonAsync("/resources", new
        {
            resourceTypeId = TestDataSeeder.ResourceTypeId,
            name = $"New Resource {Guid.NewGuid()}",
            capacity = 4,
            requiresApproval = false,
            timeZoneId = "UTC",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ASP.NET Core's built-in Forbid()/Challenge() result has no JSON body at all, so there is no
    // ProblemDetails.Extensions to attach a correlationId to - but CorrelationIdMiddleware sets the
    // response header before UseAuthentication/UseAuthorization ever run (it's the outermost
    // middleware), so the header should still survive on a bare, bodyless 403. Verifies that claim
    // rather than assuming it from the middleware ordering in Program.cs.
    [Fact]
    public async Task CreateResource_AsMember_StillHasCorrelationIdHeaderOnTheBareForbiddenResponse()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);

        var response = await client.PostAsJsonAsync("/resources", new
        {
            resourceTypeId = TestDataSeeder.ResourceTypeId,
            name = $"New Resource {Guid.NewGuid()}",
            capacity = 4,
            requiresApproval = false,
            timeZoneId = "UTC",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var values = Assert.Single(response.Headers.GetValues(BookSpace.Api.Logging.CorrelationIdMiddleware.HeaderName));
        Assert.False(string.IsNullOrWhiteSpace(values));
    }

    // Same claim, for the bare 401 an anonymous caller gets from the fallback-auth-policy short circuit
    // (no exception is thrown here either, so ValidationExceptionHandler/etc. never run).
    [Fact]
    public async Task CreateResource_WithoutToken_StillHasCorrelationIdHeaderOnTheBareUnauthorizedResponse()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/resources", new
        {
            resourceTypeId = TestDataSeeder.ResourceTypeId,
            name = $"New Resource {Guid.NewGuid()}",
            capacity = 4,
            requiresApproval = false,
            timeZoneId = "UTC",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var values = Assert.Single(response.Headers.GetValues(BookSpace.Api.Logging.CorrelationIdMiddleware.HeaderName));
        Assert.False(string.IsNullOrWhiteSpace(values));
    }

    [Fact]
    public async Task UpdateResource_AsMember_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);

        var response = await client.PutAsJsonAsync($"/resources/{TestDataSeeder.AcmeResourceId}", new
        {
            resourceTypeId = TestDataSeeder.ResourceTypeId,
            name = "Renamed",
            capacity = 8,
            requiresApproval = false,
            timeZoneId = "UTC",
            status = ResourceStatus.Active,
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DeleteResource_AsMember_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);

        var response = await client.DeleteAsync($"/resources/{TestDataSeeder.AcmeResourceId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AssignResourceApprover_AsMember_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);

        var response = await client.PostAsJsonAsync(
            $"/resources/{TestDataSeeder.AcmeResourceId}/approvers", new { userId = TestDataSeeder.AcmeMemberUserId });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // --- Cross-tenant 404 on the MUTATION paths (the read paths already all have this below) ---
    // Every read route (GetResource, GetAvailabilityRules, GetBlackoutPeriods, GetResourceApprovers,
    // GetAvailability) already had a cross-tenant 404 test; none of the mutation routes that follow the
    // identical tenant-filtered-lookup pattern did.

    [Fact]
    public async Task UpdateResource_ForAnotherTenantsResource_ReturnsNotFound()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.PutAsJsonAsync($"/resources/{TestDataSeeder.GlobexResourceId}", new
        {
            resourceTypeId = TestDataSeeder.ResourceTypeId,
            name = "Should Not Apply",
            capacity = 4,
            requiresApproval = false,
            timeZoneId = "UTC",
            status = ResourceStatus.Active,
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteResource_ForAnotherTenantsResource_ReturnsNotFound()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.DeleteAsync($"/resources/{TestDataSeeder.GlobexResourceId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateAvailabilityRule_ForAnotherTenantsResource_ReturnsNotFound()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.PostAsJsonAsync($"/resources/{TestDataSeeder.GlobexResourceId}/availability-rules",
            new { dayOfWeek = DayOfWeek.Monday, startTime = "08:00:00", endTime = "10:00:00" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateBlackoutPeriod_ForAnotherTenantsResource_ReturnsNotFound()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var start = DateTimeOffset.UtcNow.AddDays(7);

        var response = await client.PostAsJsonAsync($"/resources/{TestDataSeeder.GlobexResourceId}/blackout-periods",
            new { startUtc = start, endUtc = start.AddHours(1), reason = "Should not apply" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AssignResourceApprover_ForAnotherTenantsResource_ReturnsNotFound()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.PostAsJsonAsync(
            $"/resources/{TestDataSeeder.GlobexResourceId}/approvers", new { userId = TestDataSeeder.AcmeMemberUserId });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // The second, distinct tenant-isolation check AssignResourceApproverCommandHandler performs: the
    // RESOURCE is Acme's own, but the target UserId being made an approver belongs to a different tenant.
    [Fact]
    public async Task AssignResourceApprover_WithATargetUserFromAnotherTenant_ReturnsNotFound()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.PostAsJsonAsync(
            $"/resources/{TestDataSeeder.AcmeResourceId}/approvers", new { userId = TestDataSeeder.GlobexMemberUserId });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RemoveResourceApprover_ForAnotherTenantsResource_ReturnsNotFound()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.DeleteAsync($"/resources/{TestDataSeeder.GlobexResourceId}/approvers/{TestDataSeeder.AcmeMemberUserId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- RemoveResourceApprover had ZERO tests of any kind before this - not even a happy path ---

    [Fact]
    public async Task RemoveResourceApprover_ReturnsNoContent()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var resource = await CreateResourceAsync(client, $"ApproverRemoveHost {Guid.NewGuid()}");
        await client.PostAsJsonAsync($"/resources/{resource.Id}/approvers", new { userId = TestDataSeeder.AcmeMemberUserId });

        var response = await client.DeleteAsync($"/resources/{resource.Id}/approvers/{TestDataSeeder.AcmeMemberUserId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // --- SysAdmin-Allowed sweep: every mutation in this controller is [Authorize(Roles =
    // "TenantAdmin,SysAdmin")], but only CreateResource had ever actually been exercised with a real
    // SysAdmin login (see the comment on CreateResource_AsSysAdmin_ReturnsOkWithCreatedResource above).
    // Since SysAdmin and TenantAdmin are meant to be treated identically everywhere, a regression that
    // accidentally dropped SysAdmin from any one of these role lists would go undetected without this. ---

    [Fact]
    public async Task UpdateResource_AsSysAdmin_ReturnsOk()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var resource = await CreateResourceAsync(client, $"SysAdminUpdate {Guid.NewGuid()}");
        using var sysAdminClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeSysAdminEmail);

        var response = await sysAdminClient.PutAsJsonAsync($"/resources/{resource.Id}", new
        {
            resourceTypeId = TestDataSeeder.ResourceTypeId,
            name = "Renamed By SysAdmin",
            capacity = 8,
            requiresApproval = false,
            timeZoneId = "UTC",
            status = ResourceStatus.Active,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DeleteResource_AsSysAdmin_ReturnsOk()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var resource = await CreateResourceAsync(client, $"SysAdminDelete {Guid.NewGuid()}");
        using var sysAdminClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeSysAdminEmail);

        var response = await sysAdminClient.DeleteAsync($"/resources/{resource.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CreateAvailabilityRule_AsSysAdmin_ReturnsCreated()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var resource = await CreateResourceAsync(client, $"SysAdminRule {Guid.NewGuid()}");
        using var sysAdminClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeSysAdminEmail);

        var response = await sysAdminClient.PostAsJsonAsync($"/resources/{resource.Id}/availability-rules",
            new { dayOfWeek = DayOfWeek.Friday, startTime = "08:00:00", endTime = "10:00:00" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task DeleteAvailabilityRule_AsSysAdmin_ReturnsNoContent()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var resource = await CreateResourceAsync(client, $"SysAdminRuleDelete {Guid.NewGuid()}");
        var createResponse = await client.PostAsJsonAsync($"/resources/{resource.Id}/availability-rules",
            new { dayOfWeek = DayOfWeek.Saturday, startTime = "08:00:00", endTime = "10:00:00" });
        var rule = await createResponse.Content.ReadFromJsonAsync<AvailabilityRuleResponse>(JsonOptions);
        using var sysAdminClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeSysAdminEmail);

        var response = await sysAdminClient.DeleteAsync($"/resources/{resource.Id}/availability-rules/{rule!.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task CreateBlackoutPeriod_AsSysAdmin_ReturnsCreated()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var resource = await CreateResourceAsync(client, $"SysAdminBlackout {Guid.NewGuid()}");
        using var sysAdminClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeSysAdminEmail);
        var start = DateTimeOffset.UtcNow.AddDays(8);

        var response = await sysAdminClient.PostAsJsonAsync($"/resources/{resource.Id}/blackout-periods",
            new { startUtc = start, endUtc = start.AddHours(1), reason = "SysAdmin maintenance" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task UpdateBlackoutPeriod_AsSysAdmin_ReturnsOk()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var resource = await CreateResourceAsync(client, $"SysAdminBlackoutUpdate {Guid.NewGuid()}");
        var start = DateTimeOffset.UtcNow.AddDays(9);
        var createResponse = await client.PostAsJsonAsync($"/resources/{resource.Id}/blackout-periods",
            new { startUtc = start, endUtc = start.AddHours(1), reason = "Original" });
        var period = await createResponse.Content.ReadFromJsonAsync<BlackoutPeriodResponse>(JsonOptions);
        using var sysAdminClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeSysAdminEmail);

        var response = await sysAdminClient.PutAsJsonAsync($"/resources/{resource.Id}/blackout-periods/{period!.Id}",
            new { startUtc = start, endUtc = start.AddHours(2), reason = "Changed by SysAdmin" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DeleteBlackoutPeriod_AsSysAdmin_ReturnsNoContent()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var resource = await CreateResourceAsync(client, $"SysAdminBlackoutDelete {Guid.NewGuid()}");
        var start = DateTimeOffset.UtcNow.AddDays(10);
        var createResponse = await client.PostAsJsonAsync($"/resources/{resource.Id}/blackout-periods",
            new { startUtc = start, endUtc = start.AddHours(1), reason = "To delete" });
        var period = await createResponse.Content.ReadFromJsonAsync<BlackoutPeriodResponse>(JsonOptions);
        using var sysAdminClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeSysAdminEmail);

        var response = await sysAdminClient.DeleteAsync($"/resources/{resource.Id}/blackout-periods/{period!.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task AssignResourceApprover_AsSysAdmin_ReturnsCreated()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var resource = await CreateResourceAsync(client, $"SysAdminApproverAssign {Guid.NewGuid()}");
        using var sysAdminClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeSysAdminEmail);

        var response = await sysAdminClient.PostAsJsonAsync($"/resources/{resource.Id}/approvers", new { userId = TestDataSeeder.AcmeMemberUserId });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task RemoveResourceApprover_AsSysAdmin_ReturnsNoContent()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var resource = await CreateResourceAsync(client, $"SysAdminApproverRemove {Guid.NewGuid()}");
        await client.PostAsJsonAsync($"/resources/{resource.Id}/approvers", new { userId = TestDataSeeder.AcmeMemberUserId });
        using var sysAdminClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeSysAdminEmail);

        var response = await sysAdminClient.DeleteAsync($"/resources/{resource.Id}/approvers/{TestDataSeeder.AcmeMemberUserId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // --- Member-Denied sweep: UpdateResource/DeleteResource/AssignResourceApprover already had this;
    // these 6 sub-resource mutation endpoints' [Authorize(Roles=...)] attributes had never been exercised
    // by any Member-forbidden test. ---

    [Fact]
    public async Task CreateAvailabilityRule_AsMember_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);

        var response = await client.PostAsJsonAsync($"/resources/{TestDataSeeder.AcmeResourceId}/availability-rules",
            new { dayOfWeek = DayOfWeek.Sunday, startTime = "08:00:00", endTime = "10:00:00" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DeleteAvailabilityRule_AsMember_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var resource = await CreateResourceAsync(client, $"MemberDeniedRuleDelete {Guid.NewGuid()}");
        var createResponse = await client.PostAsJsonAsync($"/resources/{resource.Id}/availability-rules",
            new { dayOfWeek = DayOfWeek.Sunday, startTime = "08:00:00", endTime = "10:00:00" });
        var rule = await createResponse.Content.ReadFromJsonAsync<AvailabilityRuleResponse>(JsonOptions);
        using var memberClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);

        var response = await memberClient.DeleteAsync($"/resources/{resource.Id}/availability-rules/{rule!.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateBlackoutPeriod_AsMember_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var start = DateTimeOffset.UtcNow.AddDays(11);

        var response = await client.PostAsJsonAsync($"/resources/{TestDataSeeder.AcmeResourceId}/blackout-periods",
            new { startUtc = start, endUtc = start.AddHours(1), reason = "Should be forbidden" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UpdateBlackoutPeriod_AsMember_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var resource = await CreateResourceAsync(client, $"MemberDeniedBlackoutUpdate {Guid.NewGuid()}");
        var start = DateTimeOffset.UtcNow.AddDays(12);
        var createResponse = await client.PostAsJsonAsync($"/resources/{resource.Id}/blackout-periods",
            new { startUtc = start, endUtc = start.AddHours(1), reason = "Original" });
        var period = await createResponse.Content.ReadFromJsonAsync<BlackoutPeriodResponse>(JsonOptions);
        using var memberClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);

        var response = await memberClient.PutAsJsonAsync($"/resources/{resource.Id}/blackout-periods/{period!.Id}",
            new { startUtc = start, endUtc = start.AddHours(2), reason = "Should be forbidden" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DeleteBlackoutPeriod_AsMember_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var resource = await CreateResourceAsync(client, $"MemberDeniedBlackoutDelete {Guid.NewGuid()}");
        var start = DateTimeOffset.UtcNow.AddDays(13);
        var createResponse = await client.PostAsJsonAsync($"/resources/{resource.Id}/blackout-periods",
            new { startUtc = start, endUtc = start.AddHours(1), reason = "To delete" });
        var period = await createResponse.Content.ReadFromJsonAsync<BlackoutPeriodResponse>(JsonOptions);
        using var memberClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);

        var response = await memberClient.DeleteAsync($"/resources/{resource.Id}/blackout-periods/{period!.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // --- Approver-Denied sweep: Approver is a real, seeded role distinct from Member, but every one of
    // these 10 TenantAdmin,SysAdmin-only endpoints only ever had a Member-forbidden test. Its exclusion
    // from these routes was previously only asserted by the [Authorize(Roles=...)] attribute itself. ---

    [Fact]
    public async Task CreateResource_AsApprover_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeApproverEmail);

        var response = await client.PostAsJsonAsync("/resources", new
        {
            resourceTypeId = TestDataSeeder.ResourceTypeId,
            name = $"Approver Denied {Guid.NewGuid()}",
            capacity = 4,
            requiresApproval = false,
            timeZoneId = "UTC",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UpdateResource_AsApprover_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeApproverEmail);

        var response = await client.PutAsJsonAsync($"/resources/{TestDataSeeder.AcmeResourceId}", new
        {
            resourceTypeId = TestDataSeeder.ResourceTypeId,
            name = "Should Be Forbidden",
            capacity = 8,
            requiresApproval = false,
            timeZoneId = "UTC",
            status = ResourceStatus.Active,
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DeleteResource_AsApprover_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeApproverEmail);

        var response = await client.DeleteAsync($"/resources/{TestDataSeeder.AcmeResourceId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateAvailabilityRule_AsApprover_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeApproverEmail);

        var response = await client.PostAsJsonAsync($"/resources/{TestDataSeeder.AcmeResourceId}/availability-rules",
            new { dayOfWeek = DayOfWeek.Monday, startTime = "08:00:00", endTime = "10:00:00" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DeleteAvailabilityRule_AsApprover_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var resource = await CreateResourceAsync(client, $"ApproverDeniedRuleDelete {Guid.NewGuid()}");
        var createResponse = await client.PostAsJsonAsync($"/resources/{resource.Id}/availability-rules",
            new { dayOfWeek = DayOfWeek.Monday, startTime = "08:00:00", endTime = "10:00:00" });
        var rule = await createResponse.Content.ReadFromJsonAsync<AvailabilityRuleResponse>(JsonOptions);
        using var approverClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeApproverEmail);

        var response = await approverClient.DeleteAsync($"/resources/{resource.Id}/availability-rules/{rule!.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateBlackoutPeriod_AsApprover_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeApproverEmail);
        var start = DateTimeOffset.UtcNow.AddDays(16);

        var response = await client.PostAsJsonAsync($"/resources/{TestDataSeeder.AcmeResourceId}/blackout-periods",
            new { startUtc = start, endUtc = start.AddHours(1), reason = "Should be forbidden" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UpdateBlackoutPeriod_AsApprover_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var resource = await CreateResourceAsync(client, $"ApproverDeniedBlackoutUpdate {Guid.NewGuid()}");
        var start = DateTimeOffset.UtcNow.AddDays(17);
        var createResponse = await client.PostAsJsonAsync($"/resources/{resource.Id}/blackout-periods",
            new { startUtc = start, endUtc = start.AddHours(1), reason = "Original" });
        var period = await createResponse.Content.ReadFromJsonAsync<BlackoutPeriodResponse>(JsonOptions);
        using var approverClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeApproverEmail);

        var response = await approverClient.PutAsJsonAsync($"/resources/{resource.Id}/blackout-periods/{period!.Id}",
            new { startUtc = start, endUtc = start.AddHours(2), reason = "Should be forbidden" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DeleteBlackoutPeriod_AsApprover_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var resource = await CreateResourceAsync(client, $"ApproverDeniedBlackoutDelete {Guid.NewGuid()}");
        var start = DateTimeOffset.UtcNow.AddDays(18);
        var createResponse = await client.PostAsJsonAsync($"/resources/{resource.Id}/blackout-periods",
            new { startUtc = start, endUtc = start.AddHours(1), reason = "To delete" });
        var period = await createResponse.Content.ReadFromJsonAsync<BlackoutPeriodResponse>(JsonOptions);
        using var approverClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeApproverEmail);

        var response = await approverClient.DeleteAsync($"/resources/{resource.Id}/blackout-periods/{period!.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AssignResourceApprover_AsApprover_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeApproverEmail);

        var response = await client.PostAsJsonAsync(
            $"/resources/{TestDataSeeder.AcmeResourceId}/approvers", new { userId = TestDataSeeder.AcmeMemberUserId });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RemoveResourceApprover_AsApprover_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var resource = await CreateResourceAsync(client, $"ApproverDeniedApproverRemove {Guid.NewGuid()}");
        await client.PostAsJsonAsync($"/resources/{resource.Id}/approvers", new { userId = TestDataSeeder.AcmeMemberUserId });
        using var approverClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeApproverEmail);

        var response = await approverClient.DeleteAsync($"/resources/{resource.Id}/approvers/{TestDataSeeder.AcmeMemberUserId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RemoveResourceApprover_AsMember_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var resource = await CreateResourceAsync(client, $"MemberDeniedApproverRemove {Guid.NewGuid()}");
        await client.PostAsJsonAsync($"/resources/{resource.Id}/approvers", new { userId = TestDataSeeder.AcmeMemberUserId });
        using var memberClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);

        var response = await memberClient.DeleteAsync($"/resources/{resource.Id}/approvers/{TestDataSeeder.AcmeMemberUserId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateResource_WithDuplicateName_ReturnsConflict()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var request = new
        {
            resourceTypeId = TestDataSeeder.ResourceTypeId,
            name = $"Duplicate {Guid.NewGuid()}",
            capacity = 2,
            requiresApproval = false,
            timeZoneId = "UTC",
        };
        await client.PostAsJsonAsync("/resources", request);

        var response = await client.PostAsJsonAsync("/resources", request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreateResource_WithUnknownResourceType_ReturnsNotFound()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.PostAsJsonAsync("/resources", new
        {
            resourceTypeId = Guid.NewGuid(),
            name = $"Orphan Resource {Guid.NewGuid()}",
            capacity = 2,
            requiresApproval = false,
            timeZoneId = "UTC",
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateResource_WithMissingName_ReturnsBadRequestWithFieldErrors()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.PostAsJsonAsync("/resources", new
        {
            resourceTypeId = TestDataSeeder.ResourceTypeId,
            name = "",
            capacity = 2,
            requiresApproval = false,
            timeZoneId = "UTC",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.Contains("Name", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetResources_ReturnsOnlyCallersTenantResources()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.GetAsync("/resources?pageSize=100");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.Contains("Integration Test Room", payload);
        Assert.DoesNotContain("Globex Only Room", payload);
    }

    // Approver has no special read permissions of its own in WP-3 (that only matters once booking
    // approval is built) - it should behave like any other authenticated tenant member for GET, not
    // accidentally get blocked or elevated by [Authorize] resolving an unexpected role.
    [Fact]
    public async Task GetResources_AsApprover_ReturnsOk()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeApproverEmail);

        var response = await client.GetAsync("/resources");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DeleteResource_ArchivesRatherThanRemoves()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var created = await CreateResourceAsync(client, $"Archivable {Guid.NewGuid()}");

        var deleteResponse = await client.DeleteAsync($"/resources/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
        var deleted = await deleteResponse.Content.ReadFromJsonAsync<ResourceResponse>(JsonOptions);
        Assert.Equal(ResourceStatus.Archived, deleted!.Status);

        var getResponse = await client.GetAsync($"/resources/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = await getResponse.Content.ReadFromJsonAsync<ResourceResponse>(JsonOptions);
        Assert.Equal(ResourceStatus.Archived, fetched!.Status);
    }

    [Fact]
    public async Task DeleteAvailabilityRule_ReturnsNoContent()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var resource = await CreateResourceAsync(client, $"RuleHost {Guid.NewGuid()}");
        var createRuleResponse = await client.PostAsJsonAsync($"/resources/{resource.Id}/availability-rules", new
        {
            dayOfWeek = DayOfWeek.Monday,
            startTime = "08:00:00",
            endTime = "10:00:00",
        });
        var rule = await createRuleResponse.Content.ReadFromJsonAsync<AvailabilityRuleResponse>(JsonOptions);

        var response = await client.DeleteAsync($"/resources/{resource.Id}/availability-rules/{rule!.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task DeleteBlackoutPeriod_ReturnsNoContent()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var resource = await CreateResourceAsync(client, $"BlackoutDeleteHappy {Guid.NewGuid()}");
        var start = DateTimeOffset.UtcNow.AddDays(14);
        var createResponse = await client.PostAsJsonAsync($"/resources/{resource.Id}/blackout-periods",
            new { startUtc = start, endUtc = start.AddHours(1), reason = "To delete" });
        var period = await createResponse.Content.ReadFromJsonAsync<BlackoutPeriodResponse>(JsonOptions);

        var response = await client.DeleteAsync($"/resources/{resource.Id}/blackout-periods/{period!.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // The "Archived is terminal, blocks new child configuration" rule exists identically in 4 handlers
    // (CreateAvailabilityRule, CreateBlackoutPeriod, AssignResourceApprover, UpdateResource) but had never
    // been exercised end-to-end through the real HTTP pipeline - only unit-tested via mocks.
    [Fact]
    public async Task CreateAvailabilityRule_ForArchivedResource_ReturnsConflict()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var resource = await CreateResourceAsync(client, $"ArchivedRuleTarget {Guid.NewGuid()}");
        await client.DeleteAsync($"/resources/{resource.Id}");

        var response = await client.PostAsJsonAsync($"/resources/{resource.Id}/availability-rules",
            new { dayOfWeek = DayOfWeek.Monday, startTime = "08:00:00", endTime = "10:00:00" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreateBlackoutPeriod_ForArchivedResource_ReturnsConflict()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var resource = await CreateResourceAsync(client, $"ArchivedBlackoutTarget {Guid.NewGuid()}");
        await client.DeleteAsync($"/resources/{resource.Id}");
        var start = DateTimeOffset.UtcNow.AddDays(15);

        var response = await client.PostAsJsonAsync($"/resources/{resource.Id}/blackout-periods",
            new { startUtc = start, endUtc = start.AddHours(1), reason = "Should be rejected" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task AssignResourceApprover_Twice_ReturnsConflict()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var resource = await CreateResourceAsync(client, $"ApproverHost {Guid.NewGuid()}");
        var assignRequest = new { userId = TestDataSeeder.AcmeMemberUserId };
        await client.PostAsJsonAsync($"/resources/{resource.Id}/approvers", assignRequest);

        var response = await client.PostAsJsonAsync($"/resources/{resource.Id}/approvers", assignRequest);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task GetResourceApprovers_AsMember_ReturnsOk()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);

        var response = await client.GetAsync($"/resources/{TestDataSeeder.AcmeResourceId}/approvers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetAvailability_ExcludesBlackoutAndOnlyCountsActiveBookings()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var date = DateOnly.FromDateTime(TestDataSeeder.AvailabilityAnchorUtc.UtcDateTime);

        var response = await client.GetAsync(
            $"/resources/{TestDataSeeder.AcmeResourceId}/availability?from={date:yyyy-MM-dd}&to={date:yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AvailabilityResponse>(JsonOptions);

        Assert.Contains(body!.Blackouts, blackout =>
            blackout.StartUtc == TestDataSeeder.AcmeBlackoutStartUtc && blackout.Reason == TestDataSeeder.AcmeBlackoutReason);

        // The blackout consumes full capacity, so no bookable slot may overlap it at all.
        Assert.DoesNotContain(body.BookableSlots, slot =>
            slot.StartUtc < TestDataSeeder.AcmeBlackoutEndUtc && slot.EndUtc > TestDataSeeder.AcmeBlackoutStartUtc);

        // The Confirmed booking reduces capacity for its window only (8 - 3 = 5), it does not block it outright.
        Assert.Contains(body.BookableSlots, slot =>
            slot.StartUtc == TestDataSeeder.AcmeBookingStartUtc && slot.EndUtc == TestDataSeeder.AcmeBookingEndUtc
            && slot.AvailableCapacity == 8 - TestDataSeeder.AcmeBookingQuantity);
        Assert.Contains(body.BusyPeriods, busy =>
            busy.StartUtc == TestDataSeeder.AcmeBookingStartUtc && busy.Quantity == TestDataSeeder.AcmeBookingQuantity);

        // The Cancelled booking must never show up as busy or reduce capacity - it contributes no
        // occupancy at all, so its window is simply covered by the surrounding full-capacity slot
        // rather than splitting off into a boundary of its own.
        Assert.DoesNotContain(body.BusyPeriods, busy => busy.StartUtc == TestDataSeeder.AcmeCancelledBookingStartUtc);
        Assert.Contains(body.BookableSlots, slot =>
            slot.StartUtc <= TestDataSeeder.AcmeCancelledBookingStartUtc
            && slot.EndUtc >= TestDataSeeder.AcmeCancelledBookingEndUtc
            && slot.AvailableCapacity == 8);
    }

    // Full 6-value BookingStatus matrix in one place - only Pending and Confirmed are "active" per
    // BookingAvailabilityRepository.ActiveStatuses; Rejected/Cancelled/Completed/NoShow must all be
    // fully transparent to availability, exactly like Cancelled already is above.
    [Fact]
    public async Task GetAvailability_AcrossAllBookingStatuses_OnlyPendingAndConfirmedBlockCapacity()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var date = DateOnly.FromDateTime(TestDataSeeder.AvailabilityAnchorUtc.UtcDateTime);

        var response = await client.GetAsync(
            $"/resources/{TestDataSeeder.AcmeResourceId}/availability?from={date:yyyy-MM-dd}&to={date:yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AvailabilityResponse>(JsonOptions);

        // Pending reduces capacity for its window, same as Confirmed.
        Assert.Contains(body!.BusyPeriods, busy =>
            busy.StartUtc == TestDataSeeder.AcmePendingBookingStartUtc && busy.Quantity == TestDataSeeder.AcmePendingBookingQuantity);
        Assert.Contains(body.BookableSlots, slot =>
            slot.StartUtc == TestDataSeeder.AcmePendingBookingStartUtc && slot.EndUtc == TestDataSeeder.AcmePendingBookingEndUtc
            && slot.AvailableCapacity == 8 - TestDataSeeder.AcmePendingBookingQuantity);

        // Rejected, Completed, and NoShow must never appear as busy periods or reduce capacity.
        foreach (var (start, end) in new[]
        {
            (TestDataSeeder.AcmeRejectedBookingStartUtc, TestDataSeeder.AcmeRejectedBookingEndUtc),
            (TestDataSeeder.AcmeCompletedBookingStartUtc, TestDataSeeder.AcmeCompletedBookingEndUtc),
            (TestDataSeeder.AcmeNoShowBookingStartUtc, TestDataSeeder.AcmeNoShowBookingEndUtc),
        })
        {
            Assert.DoesNotContain(body.BusyPeriods, busy => busy.StartUtc == start);
            Assert.Contains(body.BookableSlots, slot => slot.StartUtc <= start && slot.EndUtc >= end && slot.AvailableCapacity == 8);
        }
    }

    [Fact]
    public async Task GetAvailability_AsMemberRole_ReturnsOk()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var date = DateOnly.FromDateTime(TestDataSeeder.AvailabilityAnchorUtc.UtcDateTime);

        var response = await client.GetAsync(
            $"/resources/{TestDataSeeder.AcmeResourceId}/availability?from={date:yyyy-MM-dd}&to={date:yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetAvailability_WithUnknownResource_ReturnsNotFound()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var date = DateOnly.FromDateTime(TestDataSeeder.AvailabilityAnchorUtc.UtcDateTime);

        var response = await client.GetAsync($"/resources/{Guid.NewGuid()}/availability?from={date:yyyy-MM-dd}&to={date:yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetAvailability_WithToDateBeforeFromDate_ReturnsBadRequest()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var date = DateOnly.FromDateTime(TestDataSeeder.AvailabilityAnchorUtc.UtcDateTime);
        var earlier = date.AddDays(-1);

        var response = await client.GetAsync(
            $"/resources/{TestDataSeeder.AcmeResourceId}/availability?from={date:yyyy-MM-dd}&to={earlier:yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Cross-tenant sub-resource access: a Globex resource id used from an Acme-authenticated client
    // must 404 on every read route, not just the top-level GET /resources/{id} - each handler's own
    // FindByIdAsync is tenant-filtered via the global query filter, so this exercises that filter on
    // every distinct code path, not just one of them. (add-cqrs-feature's own tenant-isolation-review
    // skill flagged this as planned-but-not-yet-written for the Resources feature - it never landed.)
    [Fact]
    public async Task GetResource_ForAnotherTenantsResource_ReturnsNotFound()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.GetAsync($"/resources/{TestDataSeeder.GlobexResourceId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetAvailabilityRules_ForAnotherTenantsResource_ReturnsNotFound()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.GetAsync($"/resources/{TestDataSeeder.GlobexResourceId}/availability-rules");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetBlackoutPeriods_ForAnotherTenantsResource_ReturnsNotFound()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.GetAsync($"/resources/{TestDataSeeder.GlobexResourceId}/blackout-periods");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Resource.NotFound", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task GetResourceApprovers_ForAnotherTenantsResource_ReturnsNotFound()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.GetAsync($"/resources/{TestDataSeeder.GlobexResourceId}/approvers");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Resource.NotFound", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task GetAvailability_ForAnotherTenantsResource_ReturnsNotFound()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var date = DateOnly.FromDateTime(TestDataSeeder.AvailabilityAnchorUtc.UtcDateTime);

        var response = await client.GetAsync(
            $"/resources/{TestDataSeeder.GlobexResourceId}/availability?from={date:yyyy-MM-dd}&to={date:yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Resource.NotFound", await ReadErrorCodeAsync(response));
    }

    // An Archived resource is effectively deleted from the caller's point of view, so its name is
    // released for reuse rather than reserved forever - the Resource.Name unique index is filtered
    // to exclude Archived resources ("[Status] <> 'Archived'").
    [Fact]
    public async Task CreateResource_WithNameOfArchivedResource_Succeeds()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var name = $"Archived Reuse {Guid.NewGuid()}";
        var created = await CreateResourceAsync(client, name);
        var archiveResponse = await client.DeleteAsync($"/resources/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, archiveResponse.StatusCode);

        var response = await client.PostAsJsonAsync("/resources", new
        {
            resourceTypeId = TestDataSeeder.ResourceTypeId,
            name,
            capacity = 4,
            requiresApproval = false,
            timeZoneId = "UTC",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ExistsByNameAsync has no explicit tenant parameter - it relies entirely on the EF global query
    // filter. A regression that broadened it tenant-globally would silently block legitimate cross-tenant
    // creates; only same-tenant duplicate rejection was ever tested before this.
    [Fact]
    public async Task CreateResource_WithNameAlreadyUsedByAnotherTenant_Succeeds()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.PostAsJsonAsync("/resources", new
        {
            resourceTypeId = TestDataSeeder.ResourceTypeId,
            name = "Globex Only Room", // the exact name TestDataSeeder gives Globex's resource
            capacity = 2,
            requiresApproval = false,
            timeZoneId = "UTC",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // The flip side: two Active resources still cannot share a name - only Archived ones release it.
    [Fact]
    public async Task CreateResource_WithNameOfAnotherActiveResource_ReturnsConflict()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var name = $"Still Active {Guid.NewGuid()}";
        await CreateResourceAsync(client, name);

        var response = await client.PostAsJsonAsync("/resources", new
        {
            resourceTypeId = TestDataSeeder.ResourceTypeId,
            name,
            capacity = 4,
            requiresApproval = false,
            timeZoneId = "UTC",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreateAvailabilityRule_WithExactDuplicate_ReturnsConflict()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var resource = await CreateResourceAsync(client, $"RuleDup {Guid.NewGuid()}");
        var rule = new { dayOfWeek = DayOfWeek.Tuesday, startTime = "09:00:00", endTime = "11:00:00" };
        await client.PostAsJsonAsync($"/resources/{resource.Id}/availability-rules", rule);

        var response = await client.PostAsJsonAsync($"/resources/{resource.Id}/availability-rules", rule);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // Overlapping-but-not-identical rules for the same resource/day are deliberately allowed (merged
    // later by IntervalMath.Merge in the availability query) - only the exact-duplicate tuple is
    // rejected. Proves Create doesn't reject the overlap, not just that the merge algorithm handles it.
    [Fact]
    public async Task CreateAvailabilityRule_WithOverlappingButDifferentRule_BothSucceed()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var resource = await CreateResourceAsync(client, $"RuleOverlap {Guid.NewGuid()}");

        var first = await client.PostAsJsonAsync($"/resources/{resource.Id}/availability-rules",
            new { dayOfWeek = DayOfWeek.Wednesday, startTime = "08:00:00", endTime = "14:00:00" });
        var second = await client.PostAsJsonAsync($"/resources/{resource.Id}/availability-rules",
            new { dayOfWeek = DayOfWeek.Wednesday, startTime = "12:00:00", endTime = "18:00:00" });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
    }

    // Overlapping blackouts on the same resource are deliberately allowed at Create (see the code
    // comment in CreateBlackoutPeriodCommandRequest.cs) - proves it's actually allowed, not merely untested.
    [Fact]
    public async Task CreateBlackoutPeriod_Overlapping_BothSucceed()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var resource = await CreateResourceAsync(client, $"BlackoutOverlap {Guid.NewGuid()}");
        var start = DateTimeOffset.UtcNow.AddDays(10);

        var first = await client.PostAsJsonAsync($"/resources/{resource.Id}/blackout-periods",
            new { startUtc = start, endUtc = start.AddHours(4), reason = "First" });
        var second = await client.PostAsJsonAsync($"/resources/{resource.Id}/blackout-periods",
            new { startUtc = start.AddHours(2), endUtc = start.AddHours(6), reason = "Second" });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
    }

    [Fact]
    public async Task DeleteAvailabilityRule_ForWrongResourceId_ReturnsNotFound()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var resourceA = await CreateResourceAsync(client, $"RuleOwnerA {Guid.NewGuid()}");
        var resourceB = await CreateResourceAsync(client, $"RuleOwnerB {Guid.NewGuid()}");
        var createResponse = await client.PostAsJsonAsync($"/resources/{resourceA.Id}/availability-rules",
            new { dayOfWeek = DayOfWeek.Thursday, startTime = "08:00:00", endTime = "09:00:00" });
        var rule = await createResponse.Content.ReadFromJsonAsync<AvailabilityRuleResponse>(JsonOptions);

        var response = await client.DeleteAsync($"/resources/{resourceB.Id}/availability-rules/{rule!.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateBlackoutPeriod_ForWrongResourceId_ReturnsNotFound()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var resourceA = await CreateResourceAsync(client, $"BlackoutOwnerA {Guid.NewGuid()}");
        var resourceB = await CreateResourceAsync(client, $"BlackoutOwnerB {Guid.NewGuid()}");
        var start = DateTimeOffset.UtcNow.AddDays(5);
        var createResponse = await client.PostAsJsonAsync($"/resources/{resourceA.Id}/blackout-periods",
            new { startUtc = start, endUtc = start.AddHours(1), reason = "Owner-check" });
        var period = await createResponse.Content.ReadFromJsonAsync<BlackoutPeriodResponse>(JsonOptions);

        var response = await client.PutAsJsonAsync($"/resources/{resourceB.Id}/blackout-periods/{period!.Id}",
            new { startUtc = start, endUtc = start.AddHours(2), reason = "Changed" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("BlackoutPeriod.NotFound", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task DeleteBlackoutPeriod_ForWrongResourceId_ReturnsNotFound()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var resourceA = await CreateResourceAsync(client, $"BlackoutDelOwnerA {Guid.NewGuid()}");
        var resourceB = await CreateResourceAsync(client, $"BlackoutDelOwnerB {Guid.NewGuid()}");
        var start = DateTimeOffset.UtcNow.AddDays(6);
        var createResponse = await client.PostAsJsonAsync($"/resources/{resourceA.Id}/blackout-periods",
            new { startUtc = start, endUtc = start.AddHours(1), reason = "Owner-check" });
        var period = await createResponse.Content.ReadFromJsonAsync<BlackoutPeriodResponse>(JsonOptions);

        var response = await client.DeleteAsync($"/resources/{resourceB.Id}/blackout-periods/{period!.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("BlackoutPeriod.NotFound", await ReadErrorCodeAsync(response));
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("errorCode", out var value) ? value.GetString() : null;
    }

    private async Task<ResourceResponse> CreateResourceAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/resources", new
        {
            resourceTypeId = TestDataSeeder.ResourceTypeId,
            name,
            capacity = 4,
            requiresApproval = false,
            timeZoneId = "UTC",
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ResourceResponse>(JsonOptions))!;
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

    private sealed record ResourceResponse(
        Guid Id, Guid ResourceTypeId, string Name, string? Description, int Capacity,
        bool RequiresApproval, ResourceStatus Status, string TimeZoneId);

    private sealed record AvailabilityRuleResponse(Guid Id, Guid ResourceId, DayOfWeek DayOfWeek, TimeOnly StartTime, TimeOnly EndTime);

    private sealed record BlackoutPeriodResponse(Guid Id, Guid ResourceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Reason);

    private sealed record AvailabilityResponse(
        Guid ResourceId, DateOnly FromDate, DateOnly ToDate, string TimeZoneId, int Capacity,
        IReadOnlyList<OpenPeriodItem> OpenPeriods, IReadOnlyList<BlackoutItem> Blackouts,
        IReadOnlyList<BusyPeriodItem> BusyPeriods, IReadOnlyList<BookableSlotItem> BookableSlots);

    private sealed record OpenPeriodItem(DateTimeOffset StartUtc, DateTimeOffset EndUtc);

    private sealed record BlackoutItem(DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Reason);

    private sealed record BusyPeriodItem(DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Quantity);

    private sealed record BookableSlotItem(DateTimeOffset StartUtc, DateTimeOffset EndUtc, int AvailableCapacity);
}
