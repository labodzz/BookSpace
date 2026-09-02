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
    public async Task CreateResource_AsTenantAdmin_ReturnsOkWithCreatedResource()
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

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ResourceResponse>(JsonOptions);
        Assert.Equal(4, body!.Capacity);
        Assert.Equal(ResourceStatus.Active, body.Status);
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

    private sealed record AvailabilityResponse(
        Guid ResourceId, DateOnly FromDate, DateOnly ToDate, string TimeZoneId, int Capacity,
        IReadOnlyList<OpenPeriodItem> OpenPeriods, IReadOnlyList<BlackoutItem> Blackouts,
        IReadOnlyList<BusyPeriodItem> BusyPeriods, IReadOnlyList<BookableSlotItem> BookableSlots);

    private sealed record OpenPeriodItem(DateTimeOffset StartUtc, DateTimeOffset EndUtc);

    private sealed record BlackoutItem(DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Reason);

    private sealed record BusyPeriodItem(DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Quantity);

    private sealed record BookableSlotItem(DateTimeOffset StartUtc, DateTimeOffset EndUtc, int AvailableCapacity);
}
