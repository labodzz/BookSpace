using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BookSpace.Domain.Enums;
using Xunit;

namespace BookSpace.Api.Tests.Bookings;

// Seeding happens once in CustomWebApplicationFactory.InitializeAsync - see TestDataSeeder for the fixed
// Acme resource/rules/blackout/bookings this suite reads. Tests that create their own bookings use
// distinct hour offsets from AvailabilityAnchorUtc, disjoint from both the seeded fixture windows and
// each other, since the whole class shares one database instance for its run.
public sealed class BookingsEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly CustomWebApplicationFactory _factory;

    public BookingsEndpointsTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static DateTimeOffset AnchorPlusHours(int hours) => TestDataSeeder.AvailabilityAnchorUtc.AddHours(hours);

    [Fact]
    public async Task CreateBooking_AsMember_WithAvailableSlot_ReturnsOkWithConfirmedBooking()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var start = AnchorPlusHours(1);

        var response = await client.PostAsJsonAsync("/bookings", new
        {
            resourceId = TestDataSeeder.AcmeResourceId, startUtc = start, endUtc = start.AddHours(1), quantity = 1,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions);
        Assert.Equal(BookingStatus.Confirmed, body!.Status);
        Assert.Equal(TestDataSeeder.AcmeResourceId, body.ResourceId);
    }

    [Fact]
    public async Task CreateBooking_OverlappingABlackoutPeriod_ReturnsConflictWithBlackoutConflictErrorCode()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);

        var response = await client.PostAsJsonAsync("/bookings", new
        {
            resourceId = TestDataSeeder.AcmeResourceId,
            startUtc = TestDataSeeder.AcmeBlackoutStartUtc,
            endUtc = TestDataSeeder.AcmeBlackoutEndUtc,
            quantity = 1,
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Booking.BlackoutConflict", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task CreateBooking_ExceedingRemainingCapacity_ReturnsConflictWithCapacityExceededErrorCode()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);

        // Capacity 8, the seeded Confirmed booking already uses 3 in this exact window - requesting 6
        // more would push it to 9.
        var response = await client.PostAsJsonAsync("/bookings", new
        {
            resourceId = TestDataSeeder.AcmeResourceId,
            startUtc = TestDataSeeder.AcmeBookingStartUtc,
            endUtc = TestDataSeeder.AcmeBookingEndUtc,
            quantity = 6,
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Booking.CapacityExceeded", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task CreateBooking_ForResourceInMaintenance_ReturnsConflictWithResourceUnavailableErrorCode()
    {
        using var adminClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var resource = await CreateResourceAsync(adminClient, $"Under Maintenance {Guid.NewGuid()}");
        await adminClient.PutAsJsonAsync($"/resources/{resource.Id}", new
        {
            resourceTypeId = TestDataSeeder.ResourceTypeId, name = resource.Name, description = (string?)null,
            capacity = resource.Capacity, requiresApproval = false, timeZoneId = "UTC", status = ResourceStatus.Maintenance,
        });
        using var memberClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var start = AnchorPlusHours(1);

        var response = await memberClient.PostAsJsonAsync("/bookings", new
        {
            resourceId = resource.Id, startUtc = start, endUtc = start.AddHours(1), quantity = 1,
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Booking.ResourceUnavailable", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task CreateBooking_WithEndNotAfterStart_ReturnsValidationProblem()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var start = AnchorPlusHours(1);

        var response = await client.PostAsJsonAsync("/bookings", new
        {
            resourceId = TestDataSeeder.AcmeResourceId, startUtc = start, endUtc = start, quantity = 1,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Tenant isolation on create: from Acme's tenant context, GlobexResourceId simply cannot be found -
    // the same "looks identical to not-found" shape as any other cross-tenant lookup in this codebase.
    [Fact]
    public async Task CreateBooking_ForAnotherTenantsResource_ReturnsNotFound()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var start = AnchorPlusHours(1);

        var response = await client.PostAsJsonAsync("/bookings", new
        {
            resourceId = TestDataSeeder.GlobexResourceId, startUtc = start, endUtc = start.AddHours(1), quantity = 1,
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateBooking_WithoutToken_ReturnsUnauthorized()
    {
        using var client = _factory.CreateClient();
        var start = AnchorPlusHours(1);

        var response = await client.PostAsJsonAsync("/bookings", new
        {
            resourceId = TestDataSeeder.AcmeResourceId, startUtc = start, endUtc = start.AddHours(1), quantity = 1,
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetOwnBookings_DoesNotIncludeAnotherUsersBooking()
    {
        using var memberClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        using var adminClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var memberStart = AnchorPlusHours(2);
        var adminStart = AnchorPlusHours(3);
        var memberCreate = await memberClient.PostAsJsonAsync("/bookings", new
        {
            resourceId = TestDataSeeder.AcmeResourceId, startUtc = memberStart, endUtc = memberStart.AddHours(1), quantity = 1,
        });
        var memberBooking = await memberCreate.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions);
        await adminClient.PostAsJsonAsync("/bookings", new
        {
            resourceId = TestDataSeeder.AcmeResourceId, startUtc = adminStart, endUtc = adminStart.AddHours(1), quantity = 1,
        });

        var response = await memberClient.GetAsync("/bookings?pageSize=100");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PagedResult<BookingResponse>>(JsonOptions);
        Assert.Contains(page!.Items, booking => booking.Id == memberBooking!.Id);
        Assert.DoesNotContain(page.Items, booking => booking.StartUtc == adminStart);
    }

    [Fact]
    public async Task CancelBooking_AsOwner_ReturnsOkWithCancelledStatus()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var start = AnchorPlusHours(4);
        var created = await CreateBookingAsync(client, TestDataSeeder.AcmeResourceId, start, start.AddHours(1));

        var response = await client.DeleteAsync($"/bookings/{created.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions);
        Assert.Equal(BookingStatus.Cancelled, body!.Status);
    }

    [Fact]
    public async Task CancelBooking_AlreadyCancelled_IsIdempotentAndStillReturnsOk()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var start = AnchorPlusHours(5);
        var created = await CreateBookingAsync(client, TestDataSeeder.AcmeResourceId, start, start.AddHours(1));
        await client.DeleteAsync($"/bookings/{created.Id}");

        var response = await client.DeleteAsync($"/bookings/{created.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions);
        Assert.Equal(BookingStatus.Cancelled, body!.Status);
    }

    [Fact]
    public async Task CancelBooking_BelongingToAnotherUser_ReturnsNotFound()
    {
        using var memberClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        using var adminClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var start = AnchorPlusHours(6);
        var created = await CreateBookingAsync(memberClient, TestDataSeeder.AcmeResourceId, start, start.AddHours(1));

        var response = await adminClient.DeleteAsync($"/bookings/{created.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Tenant isolation on cancel: a Globex user can't even see an Acme booking exists, let alone cancel
    // it - the global query filter makes FindByIdAsync return null before ownership is ever checked.
    [Fact]
    public async Task CancelBooking_BelongingToAnotherTenant_ReturnsNotFound()
    {
        using var memberClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        using var globexClient = await AuthenticatedClientAsync(TestDataSeeder.GlobexMemberEmail);
        var start = AnchorPlusHours(7);
        var created = await CreateBookingAsync(memberClient, TestDataSeeder.AcmeResourceId, start, start.AddHours(1));

        var response = await globexClient.DeleteAsync($"/bookings/{created.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CancelBooking_ThenAvailability_ShowsCapacityRestoredForThatSlot()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var start = AnchorPlusHours(9);
        var created = await CreateBookingAsync(client, TestDataSeeder.AcmeResourceId, start, start.AddHours(1), quantity: 8);
        var date = DateOnly.FromDateTime(start.UtcDateTime);

        var beforeCancel = await client.GetAsync($"/resources/{TestDataSeeder.AcmeResourceId}/availability?from={date:yyyy-MM-dd}&to={date:yyyy-MM-dd}");
        var beforeBody = await beforeCancel.Content.ReadFromJsonAsync<AvailabilityResponse>(JsonOptions);
        Assert.DoesNotContain(beforeBody!.BookableSlots, slot => slot.StartUtc < start.AddHours(1) && slot.EndUtc > start);

        await client.DeleteAsync($"/bookings/{created.Id}");

        var afterCancel = await client.GetAsync($"/resources/{TestDataSeeder.AcmeResourceId}/availability?from={date:yyyy-MM-dd}&to={date:yyyy-MM-dd}");
        var afterBody = await afterCancel.Content.ReadFromJsonAsync<AvailabilityResponse>(JsonOptions);
        Assert.Contains(afterBody!.BookableSlots, slot => slot.StartUtc <= start && slot.EndUtc >= start.AddHours(1) && slot.AvailableCapacity == 8);
    }

    private async Task<BookingResponse> CreateBookingAsync(HttpClient client, Guid resourceId, DateTimeOffset start, DateTimeOffset end, int quantity = 1)
    {
        var response = await client.PostAsJsonAsync("/bookings", new { resourceId, startUtc = start, endUtc = end, quantity });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions))!;
    }

    private async Task<ResourceResponse> CreateResourceAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/resources", new
        {
            resourceTypeId = TestDataSeeder.ResourceTypeId, name, capacity = 4, requiresApproval = false, timeZoneId = "UTC",
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ResourceResponse>(JsonOptions))!;
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("errorCode", out var value) ? value.GetString() : null;
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

    private sealed record BookingResponse(Guid Id, Guid ResourceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Quantity, BookingStatus Status);

    private sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

    private sealed record AvailabilityResponse(
        Guid ResourceId, DateOnly FromDate, DateOnly ToDate, string TimeZoneId, int Capacity,
        IReadOnlyList<OpenPeriodItem> OpenPeriods, IReadOnlyList<BlackoutItem> Blackouts,
        IReadOnlyList<BusyPeriodItem> BusyPeriods, IReadOnlyList<BookableSlotItem> BookableSlots);

    private sealed record OpenPeriodItem(DateTimeOffset StartUtc, DateTimeOffset EndUtc);

    private sealed record BlackoutItem(DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Reason);

    private sealed record BusyPeriodItem(DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Quantity);

    private sealed record BookableSlotItem(DateTimeOffset StartUtc, DateTimeOffset EndUtc, int AvailableCapacity);
}
