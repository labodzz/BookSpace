using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task CreateBooking_AsMember_WithAvailableSlot_ReturnsCreatedWithConfirmedBooking()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var start = AnchorPlusHours(1);

        var response = await client.PostAsJsonAsync("/api/bookings", new
        {
            resourceId = TestDataSeeder.AcmeResourceId, startUtc = start, endUtc = start.AddHours(1), quantity = 1,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions);
        Assert.Equal(BookingStatus.Confirmed, body!.Status);
        Assert.Equal(TestDataSeeder.AcmeResourceId, body.ResourceId);
    }

    [Fact]
    public async Task CreateBooking_OverlappingABlackoutPeriod_ReturnsConflictWithBlackoutConflictErrorCode()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);

        var response = await client.PostAsJsonAsync("/api/bookings", new
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
        var response = await client.PostAsJsonAsync("/api/bookings", new
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
        await adminClient.PutAsJsonAsync($"/api/resources/{resource.Id}", new
        {
            resourceTypeId = TestDataSeeder.ResourceTypeId, name = resource.Name, description = (string?)null,
            capacity = resource.Capacity, requiresApproval = false, timeZoneId = "UTC", status = ResourceStatus.Maintenance,
        });
        using var memberClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var start = AnchorPlusHours(1);

        var response = await memberClient.PostAsJsonAsync("/api/bookings", new
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

        var response = await client.PostAsJsonAsync("/api/bookings", new
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

        var response = await client.PostAsJsonAsync("/api/bookings", new
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

        var response = await client.PostAsJsonAsync("/api/bookings", new
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
        var memberCreate = await memberClient.PostAsJsonAsync("/api/bookings", new
        {
            resourceId = TestDataSeeder.AcmeResourceId, startUtc = memberStart, endUtc = memberStart.AddHours(1), quantity = 1,
        });
        var memberBooking = await memberCreate.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions);
        await adminClient.PostAsJsonAsync("/api/bookings", new
        {
            resourceId = TestDataSeeder.AcmeResourceId, startUtc = adminStart, endUtc = adminStart.AddHours(1), quantity = 1,
        });

        var response = await memberClient.GetAsync("/api/bookings?pageSize=100");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PagedResult<BookingResponse>>(JsonOptions);
        Assert.Contains(page!.Items, booking => booking.Id == memberBooking!.Id);
        Assert.DoesNotContain(page.Items, booking => booking.StartUtc == adminStart);
    }

    // fromUtc/toUtc exist for the frontend calendar - proves the range filter is wired end-to-end through
    // the controller, not just unit-tested at the handler/repository level.
    [Fact]
    public async Task GetOwnBookings_WithDateRangeFilter_OnlyReturnsBookingsOverlappingTheRange()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var insideStart = AnchorPlusHours(50);
        var outsideStart = AnchorPlusHours(100);
        var inside = await CreateBookingAsync(client, TestDataSeeder.AcmeResourceId, insideStart, insideStart.AddHours(1));
        var outside = await CreateBookingAsync(client, TestDataSeeder.AcmeResourceId, outsideStart, outsideStart.AddHours(1));
        var fromUtc = AnchorPlusHours(49);
        var toUtc = AnchorPlusHours(52);

        var response = await client.GetAsync(
            $"/api/bookings?pageSize=100&fromUtc={Uri.EscapeDataString(fromUtc.ToString("O"))}&toUtc={Uri.EscapeDataString(toUtc.ToString("O"))}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PagedResult<BookingResponse>>(JsonOptions);
        Assert.Contains(page!.Items, booking => booking.Id == inside.Id);
        Assert.DoesNotContain(page.Items, booking => booking.Id == outside.Id);
    }

    // "Most recently requested" means CreatedAtUtc order, not StartUtc order - `newer` is created SECOND
    // (so it has the later CreatedAtUtc) but deliberately starts EARLIER than `older`, so a StartUtc-based
    // sort (the bug this proves is fixed) would put them in the opposite order from what's asserted here.
    [Fact]
    public async Task GetOwnBookings_OrdersByCreationTimeDescending_NotByStartTime()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var olderRequestLaterStart = AnchorPlusHours(120);
        var newerRequestEarlierStart = AnchorPlusHours(110);
        var older = await CreateBookingAsync(client, TestDataSeeder.AcmeResourceId, olderRequestLaterStart, olderRequestLaterStart.AddHours(1));
        var newer = await CreateBookingAsync(client, TestDataSeeder.AcmeResourceId, newerRequestEarlierStart, newerRequestEarlierStart.AddHours(1));

        var response = await client.GetAsync("/api/bookings?pageSize=100");

        var page = await response.Content.ReadFromJsonAsync<PagedResult<BookingResponse>>(JsonOptions);
        var ids = page!.Items.Select(item => item.Id).ToList();
        Assert.True(ids.IndexOf(newer.Id) < ids.IndexOf(older.Id), "the more recently REQUESTED booking must be listed first, regardless of which one starts sooner");
    }

    // Global pagination correctness: an older page must never contain a more-recently-requested booking
    // than a newer page - each of these three is requested strictly after the previous, one page at a time.
    [Fact]
    public async Task GetOwnBookings_PreservesCreationOrderAcrossPages()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var first = await CreateBookingAsync(client, TestDataSeeder.AcmeResourceId, AnchorPlusHours(130), AnchorPlusHours(131));
        var second = await CreateBookingAsync(client, TestDataSeeder.AcmeResourceId, AnchorPlusHours(132), AnchorPlusHours(133));
        var third = await CreateBookingAsync(client, TestDataSeeder.AcmeResourceId, AnchorPlusHours(134), AnchorPlusHours(135));

        var page1 = await (await client.GetAsync("/api/bookings?page=1&pageSize=1")).Content.ReadFromJsonAsync<PagedResult<BookingResponse>>(JsonOptions);
        var page2 = await (await client.GetAsync("/api/bookings?page=2&pageSize=1")).Content.ReadFromJsonAsync<PagedResult<BookingResponse>>(JsonOptions);
        var page3 = await (await client.GetAsync("/api/bookings?page=3&pageSize=1")).Content.ReadFromJsonAsync<PagedResult<BookingResponse>>(JsonOptions);

        Assert.Equal(third.Id, Assert.Single(page1!.Items).Id);
        Assert.Equal(second.Id, Assert.Single(page2!.Items).Id);
        Assert.Equal(first.Id, Assert.Single(page3!.Items).Id);
    }

    // Two requests can legitimately land on the same CreatedAtUtc at typical timestamp precision - Id
    // (guaranteed unique) is the documented tiebreaker, so ordering must still be deterministic rather
    // than however the database happens to return the tied rows. Ids are random (Guid.NewGuid()), not
    // creation-ordered, so "which one sorts first" is computed from the actual Ids below rather than
    // assumed from creation order.
    [Fact]
    public async Task GetOwnBookings_WithEqualCreationTimestamps_OrdersDeterministicallyById()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var bookingA = await CreateBookingAsync(client, TestDataSeeder.AcmeResourceId, AnchorPlusHours(140), AnchorPlusHours(141));
        var bookingB = await CreateBookingAsync(client, TestDataSeeder.AcmeResourceId, AnchorPlusHours(142), AnchorPlusHours(143));

        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();
            var tiedTimestamp = DateTimeOffset.UtcNow;
            foreach (var id in new[] { bookingA.Id, bookingB.Id })
            {
                var booking = await dbContext.Bookings.IgnoreQueryFilters().FirstAsync(b => b.Id == id);
                booking.CreatedAtUtc = tiedTimestamp;
            }
            await dbContext.SaveChangesAsync();
        }
        var expectedFirst = bookingA.Id.CompareTo(bookingB.Id) > 0 ? bookingA.Id : bookingB.Id;
        var expectedSecond = expectedFirst == bookingA.Id ? bookingB.Id : bookingA.Id;

        var response = await client.GetAsync("/api/bookings?pageSize=100");

        var page = await response.Content.ReadFromJsonAsync<PagedResult<BookingResponse>>(JsonOptions);
        var ids = page!.Items.Select(item => item.Id).ToList();
        Assert.True(
            ids.IndexOf(expectedFirst) < ids.IndexOf(expectedSecond),
            "with tied CreatedAtUtc, the booking with the larger Id must be listed first, deterministically");
    }

    // Cancelling a booking updates its Status, not its CreatedAtUtc - the list's ordering must be
    // unaffected by a cancellation happening in between.
    [Fact]
    public async Task GetOwnBookings_AfterCancellingOneBooking_PreservesCreationOrderOfTheRest()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var older = await CreateBookingAsync(client, TestDataSeeder.AcmeResourceId, AnchorPlusHours(150), AnchorPlusHours(151));
        var newer = await CreateBookingAsync(client, TestDataSeeder.AcmeResourceId, AnchorPlusHours(152), AnchorPlusHours(153));
        await client.DeleteAsync($"/api/bookings/{older.Id}");

        var response = await client.GetAsync("/api/bookings?pageSize=100");

        var page = await response.Content.ReadFromJsonAsync<PagedResult<BookingResponse>>(JsonOptions);
        var ids = page!.Items.Select(item => item.Id).ToList();
        Assert.True(ids.IndexOf(newer.Id) < ids.IndexOf(older.Id));
    }

    [Fact]
    public async Task CancelBooking_AsOwner_ReturnsOkWithCancelledStatus()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var start = AnchorPlusHours(4);
        var created = await CreateBookingAsync(client, TestDataSeeder.AcmeResourceId, start, start.AddHours(1));

        var response = await client.DeleteAsync($"/api/bookings/{created.Id}");

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
        await client.DeleteAsync($"/api/bookings/{created.Id}");

        var response = await client.DeleteAsync($"/api/bookings/{created.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions);
        Assert.Equal(BookingStatus.Cancelled, body!.Status);
    }

    [Fact]
    public async Task CancelBooking_BelongingToAnotherUserWithNoAdminRole_ReturnsNotFound()
    {
        using var memberClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        // A plain Approver - some role, just not TenantAdmin/SysAdmin - to prove holding SOME role
        // isn't enough to bypass ownership; only the privileged admin roles are (see the next test).
        using var approverClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeApproverEmail);
        var start = AnchorPlusHours(6);
        var created = await CreateBookingAsync(memberClient, TestDataSeeder.AcmeResourceId, start, start.AddHours(1));

        var response = await approverClient.DeleteAsync($"/api/bookings/{created.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // TenantAdmin/SysAdmin override - see docs/open-questions.md, "TenantAdmin Cancellation of Another
    // User's Booking." CancelledByUserId ends up as the admin, not the booking's own owner, which is
    // exactly the signal GetOwnBookings' CancelledByAdmin flag is built on.
    [Fact]
    public async Task CancelBooking_AsTenantAdmin_ForAnotherUsersBooking_CancelsItWithAReason()
    {
        using var memberClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        using var adminClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var start = AnchorPlusHours(7);
        var created = await CreateBookingAsync(memberClient, TestDataSeeder.AcmeResourceId, start, start.AddHours(1));

        var response = await adminClient.DeleteAsync($"/api/bookings/{created.Id}?reason=Freeing%20the%20resource");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions);
        Assert.Equal(BookingStatus.Cancelled, body!.Status);
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

        var response = await globexClient.DeleteAsync($"/api/bookings/{created.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CancelBooking_ThenAvailability_ShowsCapacityRestoredForThatSlot()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var start = AnchorPlusHours(9);
        var created = await CreateBookingAsync(client, TestDataSeeder.AcmeResourceId, start, start.AddHours(1), quantity: 8);
        var date = DateOnly.FromDateTime(start.UtcDateTime);

        var beforeCancel = await client.GetAsync($"/api/resources/{TestDataSeeder.AcmeResourceId}/availability?from={date:yyyy-MM-dd}&to={date:yyyy-MM-dd}");
        var beforeBody = await beforeCancel.Content.ReadFromJsonAsync<AvailabilityResponse>(JsonOptions);
        Assert.DoesNotContain(beforeBody!.BookableSlots, slot => slot.StartUtc < start.AddHours(1) && slot.EndUtc > start);

        await client.DeleteAsync($"/api/bookings/{created.Id}");

        var afterCancel = await client.GetAsync($"/api/resources/{TestDataSeeder.AcmeResourceId}/availability?from={date:yyyy-MM-dd}&to={date:yyyy-MM-dd}");
        var afterBody = await afterCancel.Content.ReadFromJsonAsync<AvailabilityResponse>(JsonOptions);
        Assert.Contains(afterBody!.BookableSlots, slot => slot.StartUtc <= start && slot.EndUtc >= start.AddHours(1) && slot.AvailableCapacity == 8);
    }

    private async Task<BookingResponse> CreateBookingAsync(HttpClient client, Guid resourceId, DateTimeOffset start, DateTimeOffset end, int quantity = 1)
    {
        var response = await client.PostAsJsonAsync("/api/bookings", new { resourceId, startUtc = start, endUtc = end, quantity });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions))!;
    }

    private async Task<ResourceResponse> CreateResourceAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/resources", new
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
        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new { email, password = TestDataSeeder.Password });
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
