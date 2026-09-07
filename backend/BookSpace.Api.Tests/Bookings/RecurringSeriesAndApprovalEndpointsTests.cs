using System.Net;
using System.Net.Http.Json;
using BookSpace.Domain.Enums;
using Xunit;

namespace BookSpace.Api.Tests.Bookings;

// Seeding happens once in CustomWebApplicationFactory.InitializeAsync - see TestDataSeeder for
// AcmeApprovalRequiredResourceId (RequiresApproval=true) and AcmeApproverUserId (its ResourceApprover).
public sealed class RecurringSeriesAndApprovalEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly CustomWebApplicationFactory _factory;

    public RecurringSeriesAndApprovalEndpointsTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static DateOnly SeriesStartDate(int offsetDays) =>
        DateOnly.FromDateTime(TestDataSeeder.AvailabilityAnchorUtc.UtcDateTime.AddDays(offsetDays));

    [Fact]
    public async Task CreateRecurringSeries_WithAllOccurrencesValid_ReturnsOkWithEveryOccurrenceConfirmedAndNoConflicts()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var startDate = SeriesStartDate(30); // far from any seeded fixture window on AcmeResourceId

        var response = await client.PostAsJsonAsync("/bookings/series", new
        {
            resourceId = TestDataSeeder.AcmeResourceId, startDate, startTime = "09:00:00", endTime = "10:00:00",
            frequency = RecurrenceFrequency.Daily, interval = 1, endDate = (DateOnly?)null, occurrenceCount = 3, quantity = 1,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CreateSeriesResponse>(JsonOptions);
        Assert.Equal(3, body!.RequestedOccurrenceCount);
        Assert.Equal(3, body.CreatedOccurrences.Count);
        Assert.Empty(body.Conflicts);
        Assert.All(body.CreatedOccurrences, occurrence => Assert.Equal(BookingStatus.Confirmed, occurrence.Status));
    }

    [Fact]
    public async Task CreateRecurringSeries_WithOneOccurrenceOverlappingTheSeededBlackout_ReportsExactlyThatConflict()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        // Anchor the series so its SECOND daily occurrence lands exactly on the seeded blackout window
        // (AcmeBlackoutStartUtc..EndUtc, which is AvailabilityAnchorUtc + 10h..11h).
        var blackoutDate = DateOnly.FromDateTime(TestDataSeeder.AcmeBlackoutStartUtc.UtcDateTime);
        var startDate = blackoutDate.AddDays(-1);

        var response = await client.PostAsJsonAsync("/bookings/series", new
        {
            resourceId = TestDataSeeder.AcmeResourceId, startDate, startTime = "10:00:00", endTime = "11:00:00",
            frequency = RecurrenceFrequency.Daily, interval = 1, endDate = (DateOnly?)null, occurrenceCount = 2, quantity = 1,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CreateSeriesResponse>(JsonOptions);
        Assert.Equal(2, body!.RequestedOccurrenceCount);
        Assert.Single(body.CreatedOccurrences);
        var conflict = Assert.Single(body.Conflicts);
        Assert.Equal(blackoutDate, conflict.Date);
        Assert.Equal("Booking.BlackoutConflict", conflict.Reason);
    }

    [Fact]
    public async Task CreateRecurringSeries_ForAnotherTenantsResource_ReturnsNotFound()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);

        var response = await client.PostAsJsonAsync("/bookings/series", new
        {
            resourceId = TestDataSeeder.GlobexResourceId, startDate = SeriesStartDate(30), startTime = "09:00:00", endTime = "10:00:00",
            frequency = RecurrenceFrequency.Daily, interval = 1, endDate = (DateOnly?)null, occurrenceCount = 2, quantity = 1,
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetRecurringSeries_AsOwner_ReturnsTheSeriesWithAllItsOccurrences()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var createResponse = await client.PostAsJsonAsync("/bookings/series", new
        {
            resourceId = TestDataSeeder.AcmeResourceId, startDate = SeriesStartDate(40), startTime = "09:00:00", endTime = "10:00:00",
            frequency = RecurrenceFrequency.Weekly, interval = 1, endDate = (DateOnly?)null, occurrenceCount = 2, quantity = 1,
        });
        var created = await createResponse.Content.ReadFromJsonAsync<CreateSeriesResponse>(JsonOptions);

        var response = await client.GetAsync($"/bookings/series/{created!.SeriesId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<GetSeriesResponse>(JsonOptions);
        Assert.Equal(2, body!.Occurrences.Count);
    }

    [Fact]
    public async Task GetRecurringSeries_AsAnotherUser_ReturnsNotFound()
    {
        using var ownerClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var createResponse = await ownerClient.PostAsJsonAsync("/bookings/series", new
        {
            resourceId = TestDataSeeder.AcmeResourceId, startDate = SeriesStartDate(45), startTime = "09:00:00", endTime = "10:00:00",
            frequency = RecurrenceFrequency.Daily, interval = 1, endDate = (DateOnly?)null, occurrenceCount = 2, quantity = 1,
        });
        var created = await createResponse.Content.ReadFromJsonAsync<CreateSeriesResponse>(JsonOptions);

        using var otherClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var response = await otherClient.GetAsync($"/bookings/series/{created!.SeriesId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CancelBooking_WithCancelRemainingSeriesTrue_CancelsThisAndLaterOccurrencesOnly()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var createResponse = await client.PostAsJsonAsync("/bookings/series", new
        {
            resourceId = TestDataSeeder.AcmeResourceId, startDate = SeriesStartDate(50), startTime = "09:00:00", endTime = "10:00:00",
            frequency = RecurrenceFrequency.Daily, interval = 1, endDate = (DateOnly?)null, occurrenceCount = 3, quantity = 1,
        });
        var created = await createResponse.Content.ReadFromJsonAsync<CreateSeriesResponse>(JsonOptions);
        var occurrences = created!.CreatedOccurrences.OrderBy(occurrence => occurrence.StartUtc).ToList();

        var response = await client.DeleteAsync($"/bookings/{occurrences[0].Id}?cancelRemainingSeries=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CancelResponse>(JsonOptions);
        Assert.Equal(BookingStatus.Cancelled, body!.Status);
        Assert.Equal(2, body.CascadedOccurrenceIds.Count);
        Assert.Contains(occurrences[1].Id, body.CascadedOccurrenceIds);
        Assert.Contains(occurrences[2].Id, body.CascadedOccurrenceIds);
    }

    [Fact]
    public async Task CreateBooking_ForResourceRequiringApproval_ReturnsOkWithPendingStatus()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var start = TestDataSeeder.AvailabilityAnchorUtc.AddHours(2).AddDays(60);

        var response = await client.PostAsJsonAsync("/bookings", new
        {
            resourceId = TestDataSeeder.AcmeApprovalRequiredResourceId, startUtc = start, endUtc = start.AddHours(1), quantity = 1,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions);
        Assert.Equal(BookingStatus.Pending, body!.Status);
    }

    [Fact]
    public async Task GetPendingApprovals_AsAssignedApprover_ReturnsTheBooking()
    {
        using var memberClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var start = TestDataSeeder.AvailabilityAnchorUtc.AddHours(2).AddDays(61);
        var createResponse = await memberClient.PostAsJsonAsync("/bookings", new
        {
            resourceId = TestDataSeeder.AcmeApprovalRequiredResourceId, startUtc = start, endUtc = start.AddHours(1), quantity = 1,
        });
        var created = await createResponse.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions);

        using var approverClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeApproverEmail);
        var response = await approverClient.GetAsync("/bookings/pending-approval");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<List<PendingApprovalItem>>(JsonOptions);
        Assert.Contains(body!, item => item.BookingId == created!.Id);
    }

    [Fact]
    public async Task GetPendingApprovals_AsPlainMember_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);

        var response = await client.GetAsync("/bookings/pending-approval");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ApproveBooking_AsAssignedApprover_ConfirmsTheBooking()
    {
        using var memberClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var start = TestDataSeeder.AvailabilityAnchorUtc.AddHours(2).AddDays(62);
        var createResponse = await memberClient.PostAsJsonAsync("/bookings", new
        {
            resourceId = TestDataSeeder.AcmeApprovalRequiredResourceId, startUtc = start, endUtc = start.AddHours(1), quantity = 1,
        });
        var created = await createResponse.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions);

        using var approverClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeApproverEmail);
        var response = await approverClient.PostAsJsonAsync($"/bookings/{created!.Id}/approve", new { decisionNote = "Approved" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions);
        Assert.Equal(BookingStatus.Confirmed, body!.Status);
    }

    [Fact]
    public async Task RejectBooking_AsAssignedApprover_RejectsTheBooking()
    {
        using var memberClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var start = TestDataSeeder.AvailabilityAnchorUtc.AddHours(2).AddDays(63);
        var createResponse = await memberClient.PostAsJsonAsync("/bookings", new
        {
            resourceId = TestDataSeeder.AcmeApprovalRequiredResourceId, startUtc = start, endUtc = start.AddHours(1), quantity = 1,
        });
        var created = await createResponse.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions);

        using var approverClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeApproverEmail);
        var response = await approverClient.PostAsJsonAsync($"/bookings/{created!.Id}/reject", new { decisionNote = "No longer needed" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions);
        Assert.Equal(BookingStatus.Rejected, body!.Status);
    }

    [Fact]
    public async Task ApproveBooking_ForAnotherTenantsBooking_ReturnsNotFound()
    {
        using var globexClient = await AuthenticatedClientAsync(TestDataSeeder.GlobexMemberEmail);
        var start = TestDataSeeder.AvailabilityAnchorUtc.AddHours(2).AddDays(64);
        var createResponse = await globexClient.PostAsJsonAsync("/bookings", new
        {
            resourceId = TestDataSeeder.GlobexResourceId, startUtc = start, endUtc = start.AddHours(1), quantity = 1,
        });
        var created = await createResponse.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions);

        using var acmeApproverClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeApproverEmail);
        var response = await acmeApproverClient.PostAsJsonAsync($"/bookings/{created!.Id}/approve", new { decisionNote = (string?)null });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ApproveBooking_AlreadyConfirmed_ReturnsConflict()
    {
        using var memberClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var start = TestDataSeeder.AvailabilityAnchorUtc.AddHours(2).AddDays(65);
        var createResponse = await memberClient.PostAsJsonAsync("/bookings", new
        {
            resourceId = TestDataSeeder.AcmeApprovalRequiredResourceId, startUtc = start, endUtc = start.AddHours(1), quantity = 1,
        });
        var created = await createResponse.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions);
        using var approverClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeApproverEmail);
        await approverClient.PostAsJsonAsync($"/bookings/{created!.Id}/approve", new { decisionNote = (string?)null });

        var response = await approverClient.PostAsJsonAsync($"/bookings/{created.Id}/approve", new { decisionNote = (string?)null });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
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

    private sealed record BookingResponse(Guid Id, Guid ResourceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Quantity, BookingStatus Status);

    private sealed record CancelResponse(
        Guid Id, Guid ResourceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Quantity, BookingStatus Status,
        IReadOnlyList<Guid> CascadedOccurrenceIds);

    private sealed record CreateSeriesOccurrence(Guid Id, DateTimeOffset StartUtc, DateTimeOffset EndUtc, BookingStatus Status);

    private sealed record CreateSeriesConflict(DateOnly Date, string Reason);

    private sealed record CreateSeriesResponse(
        Guid SeriesId, Guid ResourceId, int RequestedOccurrenceCount,
        IReadOnlyList<CreateSeriesOccurrence> CreatedOccurrences, IReadOnlyList<CreateSeriesConflict> Conflicts);

    private sealed record GetSeriesOccurrence(Guid Id, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Quantity, BookingStatus Status);

    private sealed record GetSeriesResponse(
        Guid Id, Guid ResourceId, DateOnly StartDate, TimeOnly StartTime, TimeOnly EndTime,
        RecurrenceFrequency Frequency, int Interval, DateOnly? EndDate, int? OccurrenceCount, int Quantity,
        IReadOnlyList<GetSeriesOccurrence> Occurrences);

    private sealed record PendingApprovalItem(
        Guid BookingId, Guid ResourceId, Guid UserId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Quantity, DateTimeOffset ExpiresAtUtc);
}
