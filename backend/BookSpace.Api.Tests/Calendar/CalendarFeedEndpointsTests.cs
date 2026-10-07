using System.Net;
using System.Net.Http.Json;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BookSpace.Api.Tests.Calendar;

// CalendarFeedController is anonymous by design (see its own doc comment) - every test here calls it
// WITHOUT any Authorization header, proving the token itself (not a JWT) is what gates access.
public sealed class CalendarFeedEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public CalendarFeedEndpointsTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task GetFeed_WithAValidToken_ReturnsOkWithTheCalendarContentType()
    {
        var feedUrl = await CreateSubscriptionAsync(TestDataSeeder.AcmeMemberEmail);
        using var anonymousClient = _factory.CreateClient();

        var response = await anonymousClient.GetAsync(ToRelativePath(feedUrl));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/calendar", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType!.CharSet);
        var ics = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("BEGIN:VCALENDAR", ics);
        Assert.Contains("END:VCALENDAR", ics);
    }

    [Fact]
    public async Task GetFeed_ResponseCarriesANoStoreCacheControlHeader()
    {
        var feedUrl = await CreateSubscriptionAsync(TestDataSeeder.AcmeAdminEmail);
        using var anonymousClient = _factory.CreateClient();

        var response = await anonymousClient.GetAsync(ToRelativePath(feedUrl));

        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task GetFeed_WithAnInvalidToken_ReturnsNotFound()
    {
        using var anonymousClient = _factory.CreateClient();

        var response = await anonymousClient.GetAsync("/api/calendar-feed?token=this-was-never-issued");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetFeed_WithNoTokenAtAll_ReturnsNotFound()
    {
        using var anonymousClient = _factory.CreateClient();

        var response = await anonymousClient.GetAsync("/api/calendar-feed");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetFeed_ForAUserWhoWasSubsequentlyDeactivated_ReturnsNotFound()
    {
        var feedUrl = await CreateSubscriptionAsync(TestDataSeeder.AcmeUnassignedApproverEmail);
        await MutateDatabaseAsync(async dbContext =>
        {
            var user = await dbContext.Users.IgnoreQueryFilters().SingleAsync(u => u.Email == TestDataSeeder.AcmeUnassignedApproverEmail);
            user.Status = UserStatus.Inactive;
        });
        using var anonymousClient = _factory.CreateClient();

        var response = await anonymousClient.GetAsync(ToRelativePath(feedUrl));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetFeed_ForATenantThatWasSubsequentlySuspended_ReturnsNotFound()
    {
        var feedUrl = await CreateSubscriptionAsync(TestDataSeeder.GlobexMemberEmail);
        await MutateDatabaseAsync(async dbContext =>
        {
            var tenant = await dbContext.Tenants.SingleAsync(t => t.Id == TestDataSeeder.GlobexTenantId);
            tenant.Status = TenantStatus.Suspended;
        });
        using var anonymousClient = _factory.CreateClient();

        var response = await anonymousClient.GetAsync(ToRelativePath(feedUrl));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // Restore so this shared-database suite's other tests (and any Globex login) are unaffected.
        await MutateDatabaseAsync(async dbContext =>
        {
            var tenant = await dbContext.Tenants.SingleAsync(t => t.Id == TestDataSeeder.GlobexTenantId);
            tenant.Status = TenantStatus.Active;
        });
    }

    [Fact]
    public async Task GetFeed_OnlyEverIncludesTheSubscriptionOwnersOwnBookings_NeverAnotherUsersOrTenants()
    {
        using var ownerClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        using var otherAcmeClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeApproverEmail);

        var ownBooking = await CreateConfirmedBookingAsync(ownerClient, TestDataSeeder.AcmeResourceId, hoursFromAnchor: 300);
        var otherAcmeBooking = await CreateConfirmedBookingAsync(otherAcmeClient, TestDataSeeder.AcmeResourceId, hoursFromAnchor: 302);
        // GlobexResourceId is deliberately seeded with no AvailabilityRules at all (see TestDataSeeder), so
        // a real cross-tenant booking is seeded directly rather than through the actual booking flow, which
        // this isolation assertion doesn't need anyway - it only needs a Confirmed row to exist for a
        // DIFFERENT tenant in the window.
        var globexBookingId = Guid.NewGuid();
        await MutateDatabaseAsync(async dbContext =>
        {
            dbContext.Bookings.Add(new BookSpace.Domain.Entities.Booking
            {
                Id = globexBookingId, TenantId = TestDataSeeder.GlobexTenantId, ResourceId = TestDataSeeder.GlobexResourceId, UserId = TestDataSeeder.GlobexMemberUserId,
                StartUtc = TestDataSeeder.AvailabilityAnchorUtc.AddHours(304), EndUtc = TestDataSeeder.AvailabilityAnchorUtc.AddHours(305),
                Quantity = 1, Status = BookingStatus.Confirmed, CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });

        var feedUrl = await CreateSubscriptionAsync(ownerClient);
        using var anonymousClient = _factory.CreateClient();
        var ics = await (await anonymousClient.GetAsync(ToRelativePath(feedUrl))).Content.ReadAsStringAsync();

        // Asserted by exact UID, not total VEVENT count: this class shares one database across every test
        // method, and other methods may independently give AcmeMemberEmail further Confirmed bookings of
        // their own within the same default feed window - this isolation check only cares that THESE two
        // other owners' bookings specifically never leak in, not that the owner has no other bookings at all.
        Assert.Contains($"UID:{ownBooking.Id:D}@bookspace", ics);
        Assert.DoesNotContain($"UID:{otherAcmeBooking.Id:D}@bookspace", ics);
        Assert.DoesNotContain($"UID:{globexBookingId:D}@bookspace", ics);
    }

    [Fact]
    public async Task GetFeed_ExcludesPendingCancelledAndNoShowBookings_IncludesOnlyConfirmed()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);

        var confirmed = await CreateConfirmedBookingAsync(client, TestDataSeeder.AcmeResourceId, hoursFromAnchor: 320);
        var toCancel = await CreateConfirmedBookingAsync(client, TestDataSeeder.AcmeResourceId, hoursFromAnchor: 322);
        await client.DeleteAsync($"/api/bookings/{toCancel.Id}");
        var pendingResponse = await client.PostAsJsonAsync("/api/bookings", new
        {
            resourceId = TestDataSeeder.AcmeApprovalRequiredResourceId,
            startUtc = TestDataSeeder.AvailabilityAnchorUtc.AddHours(324),
            endUtc = TestDataSeeder.AvailabilityAnchorUtc.AddHours(325),
            quantity = 1,
        });
        pendingResponse.EnsureSuccessStatusCode();
        var pending = await pendingResponse.Content.ReadFromJsonAsync<BookingIdResponse>();
        var noShowId = Guid.NewGuid();
        await MutateDatabaseAsync(async dbContext =>
        {
            dbContext.Bookings.Add(new BookSpace.Domain.Entities.Booking
            {
                Id = noShowId, TenantId = TestDataSeeder.AcmeTenantId, ResourceId = TestDataSeeder.AcmeResourceId, UserId = TestDataSeeder.AcmeMemberUserId,
                StartUtc = TestDataSeeder.AvailabilityAnchorUtc.AddHours(326), EndUtc = TestDataSeeder.AvailabilityAnchorUtc.AddHours(327),
                Quantity = 1, Status = BookingStatus.NoShow, CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });

        var feedUrl = await CreateSubscriptionAsync(client);
        using var anonymousClient = _factory.CreateClient();
        var ics = await (await anonymousClient.GetAsync(ToRelativePath(feedUrl))).Content.ReadAsStringAsync();

        Assert.Contains($"UID:{confirmed.Id:D}@bookspace", ics);
        Assert.DoesNotContain($"UID:{toCancel.Id:D}@bookspace", ics);
        Assert.DoesNotContain($"UID:{pending!.Id:D}@bookspace", ics);
        Assert.DoesNotContain($"UID:{noShowId:D}@bookspace", ics);
    }

    [Fact]
    public async Task GetFeed_EmitsASeparateVEventForEachOccurrenceOfARecurringSeries_WithoutAnRRule()
    {
        // GlobexResourceId is deliberately seeded with no AvailabilityRules (see TestDataSeeder) - an Acme
        // resource is used here instead since this test's own point (one VEVENT per occurrence, no RRULE)
        // has nothing to do with which tenant owns the resource.
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var seriesResponse = await client.PostAsJsonAsync("/api/bookings/series", new
        {
            resourceId = TestDataSeeder.AcmeResourceId,
            startDate = DateOnly.FromDateTime(TestDataSeeder.AvailabilityAnchorUtc.AddDays(60).UtcDateTime),
            startTime = new TimeOnly(9, 0),
            endTime = new TimeOnly(10, 0),
            frequency = RecurrenceFrequency.Daily,
            interval = 1,
            occurrenceCount = 3,
            quantity = 1,
        });
        seriesResponse.EnsureSuccessStatusCode();
        var series = await seriesResponse.Content.ReadFromJsonAsync<SeriesResponse>();
        var occurrencesResponse = await client.GetAsync($"/api/bookings?seriesId={series!.SeriesId}&pageSize=10");
        var occurrences = await occurrencesResponse.Content.ReadFromJsonAsync<PagedResult<BookingIdResponse>>();

        var feedUrl = await CreateSubscriptionAsync(client);
        using var anonymousClient = _factory.CreateClient();
        var ics = await (await anonymousClient.GetAsync(ToRelativePath(feedUrl))).Content.ReadAsStringAsync();

        Assert.DoesNotContain("RRULE", ics);
        foreach (var occurrence in occurrences!.Items)
        {
            Assert.Contains($"UID:{occurrence.Id:D}@bookspace", ics);
        }
    }

    [Fact]
    public async Task GetFeed_UtcInstantsMatchTheStoredBookingExactly()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var start = TestDataSeeder.AvailabilityAnchorUtc.AddHours(340);
        var booking = await CreateConfirmedBookingAsync(client, TestDataSeeder.AcmeResourceId, hoursFromAnchor: 340);

        var feedUrl = await CreateSubscriptionAsync(client);
        using var anonymousClient = _factory.CreateClient();
        var ics = await (await anonymousClient.GetAsync(ToRelativePath(feedUrl))).Content.ReadAsStringAsync();

        var expectedStart = start.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'");
        var expectedEnd = start.AddHours(1).UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'");
        Assert.Contains($"DTSTART:{expectedStart}", ics);
        Assert.Contains($"DTEND:{expectedEnd}", ics);
        _ = booking;
    }

    [Fact]
    public async Task GetFeed_CalledTwiceWithoutAnyBookingChange_ReturnsTheSameUidBothTimes()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeApproverEmail);
        var booking = await CreateConfirmedBookingAsync(client, TestDataSeeder.AcmeResourceId, hoursFromAnchor: 350);
        var feedUrl = await CreateSubscriptionAsync(client);
        using var anonymousClient = _factory.CreateClient();

        var first = await (await anonymousClient.GetAsync(ToRelativePath(feedUrl))).Content.ReadAsStringAsync();
        var second = await (await anonymousClient.GetAsync(ToRelativePath(feedUrl))).Content.ReadAsStringAsync();

        var expectedUid = $"UID:{booking.Id:D}@bookspace";
        Assert.Contains(expectedUid, first);
        Assert.Contains(expectedUid, second);
    }

    [Fact]
    public async Task GetFeed_ForAUserWithNoConfirmedBookingsInTheWindow_ReturnsAValidButEmptyCalendar()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeUnassignedApproverEmail);
        var feedUrl = await CreateSubscriptionAsync(client);
        using var anonymousClient = _factory.CreateClient();

        var ics = await (await anonymousClient.GetAsync(ToRelativePath(feedUrl))).Content.ReadAsStringAsync();

        Assert.StartsWith("BEGIN:VCALENDAR", ics);
        Assert.Contains("END:VCALENDAR", ics);
        Assert.DoesNotContain("BEGIN:VEVENT", ics);
    }

    [Fact]
    public async Task GetFeed_WithAUnicodeResourceName_IncludesItEscapedButRecognizable()
    {
        using var adminClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var resourceName = $"Конференц-зала {Guid.NewGuid():N}";
        var resourceResponse = await adminClient.PostAsJsonAsync("/api/resources", new
        {
            resourceTypeId = TestDataSeeder.ResourceTypeId, name = resourceName, capacity = 2, requiresApproval = false, timeZoneId = "UTC",
        });
        resourceResponse.EnsureSuccessStatusCode();
        var resource = await resourceResponse.Content.ReadFromJsonAsync<BookingIdResponse>();

        using var memberClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        // The feed only needs this resource's Name, not for it to actually be bookable (which would
        // require an AvailabilityRule this test doesn't otherwise care about) - seed the booking directly.
        await MutateDatabaseAsync(async dbContext =>
        {
            dbContext.Bookings.Add(new BookSpace.Domain.Entities.Booking
            {
                Id = Guid.NewGuid(), TenantId = TestDataSeeder.AcmeTenantId, ResourceId = resource!.Id, UserId = TestDataSeeder.AcmeMemberUserId,
                StartUtc = TestDataSeeder.AvailabilityAnchorUtc.AddHours(360), EndUtc = TestDataSeeder.AvailabilityAnchorUtc.AddHours(361),
                Quantity = 1, Status = BookingStatus.Confirmed, CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });

        var feedUrl = await CreateSubscriptionAsync(memberClient);
        using var anonymousClient = _factory.CreateClient();
        var ics = await (await anonymousClient.GetAsync(ToRelativePath(feedUrl))).Content.ReadAsStringAsync();

        // Unfold continuation lines before substring-matching a name that may straddle a fold boundary.
        var unfolded = ics.Replace("\r\n ", string.Empty);
        Assert.Contains(resourceName, unfolded);
    }

    private sealed record BookingIdResponse(Guid Id);

    private sealed record SeriesResponse(Guid SeriesId);

    private sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

    private static string ToRelativePath(string absoluteUrl) => new Uri(absoluteUrl).PathAndQuery;

    private async Task<BookingIdResponse> CreateConfirmedBookingAsync(HttpClient client, Guid resourceId, int hoursFromAnchor)
    {
        var start = TestDataSeeder.AvailabilityAnchorUtc.AddHours(hoursFromAnchor);
        var response = await client.PostAsJsonAsync("/api/bookings", new { resourceId, startUtc = start, endUtc = start.AddHours(1), quantity = 1 });
        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Create booking failed: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }
        return (await response.Content.ReadFromJsonAsync<BookingIdResponse>())!;
    }

    private async Task<string> CreateSubscriptionAsync(string email)
    {
        using var client = await AuthenticatedClientAsync(email);
        return await CreateSubscriptionAsync(client);
    }

    private static async Task<string> CreateSubscriptionAsync(HttpClient authenticatedClient)
    {
        await authenticatedClient.DeleteAsync("/api/calendar-subscriptions"); // ensure a clean slate for this call
        var response = await authenticatedClient.PostAsync("/api/calendar-subscriptions", content: null);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<TokenResponse>();
        return body!.FeedUrl;
    }

    private sealed record TokenResponse(string FeedUrl, DateTimeOffset CreatedAtUtc);

    private async Task MutateDatabaseAsync(Func<BookSpaceDbContext, Task> mutate)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();
        await mutate(dbContext);
        await dbContext.SaveChangesAsync();
    }

    private async Task<HttpClient> AuthenticatedClientAsync(string email)
    {
        var client = _factory.CreateClient();
        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new { email, password = TestDataSeeder.Password });
        loginResponse.EnsureSuccessStatusCode();
        var loginBody = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();
        client.DefaultRequestHeaders.Authorization = new("Bearer", loginBody!.AccessToken);
        return client;
    }

    private sealed record LoginResponse(string AccessToken);
}
