using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
    public async Task CreateRecurringSeries_WithAllOccurrencesValid_ReturnsCreatedWithEveryOccurrenceConfirmedAndNoConflicts()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var startDate = SeriesStartDate(30); // far from any seeded fixture window on AcmeResourceId

        var response = await client.PostAsJsonAsync("/bookings/series", new
        {
            resourceId = TestDataSeeder.AcmeResourceId, startDate, startTime = "09:00:00", endTime = "10:00:00",
            frequency = RecurrenceFrequency.Daily, interval = 1, endDate = (DateOnly?)null, occurrenceCount = 3, quantity = 1,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
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

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
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

    // The test above only cascades from the FIRST occurrence - cascading from a MIDDLE or LAST occurrence
    // was never exercised at the API level (only proven at the Application-test/mock level).
    [Fact]
    public async Task CancelBooking_FromTheMiddleOccurrenceWithCancelRemainingSeriesTrue_CancelsOnlyItselfAndTheLastOccurrence()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var createResponse = await client.PostAsJsonAsync("/bookings/series", new
        {
            resourceId = TestDataSeeder.AcmeResourceId, startDate = SeriesStartDate(53), startTime = "09:00:00", endTime = "10:00:00",
            frequency = RecurrenceFrequency.Daily, interval = 1, endDate = (DateOnly?)null, occurrenceCount = 3, quantity = 1,
        });
        var created = await createResponse.Content.ReadFromJsonAsync<CreateSeriesResponse>(JsonOptions);
        var occurrences = created!.CreatedOccurrences.OrderBy(occurrence => occurrence.StartUtc).ToList();

        var response = await client.DeleteAsync($"/bookings/{occurrences[1].Id}?cancelRemainingSeries=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CancelResponse>(JsonOptions);
        Assert.Equal(BookingStatus.Cancelled, body!.Status);
        var cascaded = Assert.Single(body.CascadedOccurrenceIds);
        Assert.Equal(occurrences[2].Id, cascaded);

        var firstOccurrenceResponse = await client.GetAsync($"/bookings/series/{created.SeriesId}");
        var series = await firstOccurrenceResponse.Content.ReadFromJsonAsync<GetSeriesResponse>(JsonOptions);
        var firstOccurrenceStatus = series!.Occurrences.Single(occurrence => occurrence.Id == occurrences[0].Id).Status;
        Assert.Equal(BookingStatus.Confirmed, firstOccurrenceStatus); // the earlier occurrence is never touched
    }

    [Fact]
    public async Task CancelBooking_FromTheLastOccurrenceWithCancelRemainingSeriesTrue_CascadesNothing()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var createResponse = await client.PostAsJsonAsync("/bookings/series", new
        {
            resourceId = TestDataSeeder.AcmeResourceId, startDate = SeriesStartDate(56), startTime = "09:00:00", endTime = "10:00:00",
            frequency = RecurrenceFrequency.Daily, interval = 1, endDate = (DateOnly?)null, occurrenceCount = 3, quantity = 1,
        });
        var created = await createResponse.Content.ReadFromJsonAsync<CreateSeriesResponse>(JsonOptions);
        var occurrences = created!.CreatedOccurrences.OrderBy(occurrence => occurrence.StartUtc).ToList();

        var response = await client.DeleteAsync($"/bookings/{occurrences[2].Id}?cancelRemainingSeries=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CancelResponse>(JsonOptions);
        Assert.Equal(BookingStatus.Cancelled, body!.Status);
        Assert.Empty(body.CascadedOccurrenceIds); // nothing left after the last occurrence
    }

    // Cascading a series that includes a Pending (approval-required) occurrence had never been exercised
    // at the API level - CancellableStatuses includes Pending, so it must be cascaded like a Confirmed one.
    [Fact]
    public async Task CancelBooking_WithCancelRemainingSeriesTrue_CascadesAPendingOccurrenceRequiringApproval()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var createResponse = await client.PostAsJsonAsync("/bookings/series", new
        {
            resourceId = TestDataSeeder.AcmeApprovalRequiredResourceId, startDate = SeriesStartDate(59), startTime = "09:00:00", endTime = "10:00:00",
            frequency = RecurrenceFrequency.Daily, interval = 1, endDate = (DateOnly?)null, occurrenceCount = 2, quantity = 1,
        });
        var created = await createResponse.Content.ReadFromJsonAsync<CreateSeriesResponse>(JsonOptions);
        var occurrences = created!.CreatedOccurrences.OrderBy(occurrence => occurrence.StartUtc).ToList();
        Assert.All(occurrences, occurrence => Assert.Equal(BookingStatus.Pending, occurrence.Status));

        var response = await client.DeleteAsync($"/bookings/{occurrences[0].Id}?cancelRemainingSeries=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CancelResponse>(JsonOptions);
        var cascaded = Assert.Single(body!.CascadedOccurrenceIds);
        Assert.Equal(occurrences[1].Id, cascaded);
    }

    [Fact]
    public async Task CreateBooking_ForResourceRequiringApproval_ReturnsCreatedWithPendingStatus()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var start = TestDataSeeder.AvailabilityAnchorUtc.AddHours(2).AddDays(60);

        var response = await client.PostAsJsonAsync("/bookings", new
        {
            resourceId = TestDataSeeder.AcmeApprovalRequiredResourceId, startUtc = start, endUtc = start.AddHours(1), quantity = 1,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
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

    // SeriesId lets the client group an approver's queue by recurring series (e.g. offer "approve all
    // pending in this series") - a one-off booking (this fixture) must report it as null, never a
    // placeholder/empty guid that could be mistaken for a real series.
    [Fact]
    public async Task GetPendingApprovals_ForAOneOffBooking_ReportsNullSeriesId()
    {
        using var memberClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var start = TestDataSeeder.AvailabilityAnchorUtc.AddHours(2).AddDays(95);
        var createResponse = await memberClient.PostAsJsonAsync("/bookings", new
        {
            resourceId = TestDataSeeder.AcmeApprovalRequiredResourceId, startUtc = start, endUtc = start.AddHours(1), quantity = 1,
        });
        var created = await createResponse.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions);

        using var approverClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeApproverEmail);
        var response = await approverClient.GetAsync("/bookings/pending-approval");

        var body = await response.Content.ReadFromJsonAsync<List<PendingApprovalItem>>(JsonOptions);
        Assert.Null(body!.Single(item => item.BookingId == created!.Id).SeriesId);
    }

    // SysAdmin had never been exercised at the API level for any of the three approval endpoints - only
    // TenantAdmin's half of [Authorize(Roles="Approver,TenantAdmin,SysAdmin")] was proven to actually work.
    [Fact]
    public async Task GetPendingApprovals_AsSysAdmin_SeesEveryPendingBookingInTheTenant()
    {
        using var memberClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var start = TestDataSeeder.AvailabilityAnchorUtc.AddHours(2).AddDays(71);
        var createResponse = await memberClient.PostAsJsonAsync("/bookings", new
        {
            resourceId = TestDataSeeder.AcmeApprovalRequiredResourceId, startUtc = start, endUtc = start.AddHours(1), quantity = 1,
        });
        var created = await createResponse.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions);

        using var sysAdminClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeSysAdminEmail);
        var response = await sysAdminClient.GetAsync("/bookings/pending-approval");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<List<PendingApprovalItem>>(JsonOptions);
        Assert.Contains(body!, item => item.BookingId == created!.Id);
    }

    // TenantAdmin is explicitly listed in [Authorize(Roles="Approver,TenantAdmin,SysAdmin")] on all 3 of
    // these routes, but - unlike Approver and SysAdmin - had never actually been exercised against any
    // of them by name; TestDataSeeder's own comment acknowledges Approver/SysAdmin were added specifically
    // to close this kind of gap, but TenantAdmin coverage here was never added alongside it.
    [Fact]
    public async Task GetPendingApprovals_AsTenantAdmin_SeesEveryPendingBookingInTheTenant()
    {
        using var memberClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var start = TestDataSeeder.AvailabilityAnchorUtc.AddHours(2).AddDays(74);
        var createResponse = await memberClient.PostAsJsonAsync("/bookings", new
        {
            resourceId = TestDataSeeder.AcmeApprovalRequiredResourceId, startUtc = start, endUtc = start.AddHours(1), quantity = 1,
        });
        var created = await createResponse.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions);

        using var adminClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var response = await adminClient.GetAsync("/bookings/pending-approval");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<List<PendingApprovalItem>>(JsonOptions);
        Assert.Contains(body!, item => item.BookingId == created!.Id);
    }

    [Fact]
    public async Task ApproveBooking_AsTenantAdmin_ConfirmsTheBooking()
    {
        using var memberClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var start = TestDataSeeder.AvailabilityAnchorUtc.AddHours(2).AddDays(75);
        var createResponse = await memberClient.PostAsJsonAsync("/bookings", new
        {
            resourceId = TestDataSeeder.AcmeApprovalRequiredResourceId, startUtc = start, endUtc = start.AddHours(1), quantity = 1,
        });
        var created = await createResponse.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions);

        using var adminClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var response = await adminClient.PostAsJsonAsync($"/bookings/{created!.Id}/approve", new { decisionNote = (string?)null });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions);
        Assert.Equal(BookingStatus.Confirmed, body!.Status);
    }

    [Fact]
    public async Task RejectBooking_AsTenantAdmin_RejectsTheBooking()
    {
        using var memberClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var start = TestDataSeeder.AvailabilityAnchorUtc.AddHours(2).AddDays(76);
        var createResponse = await memberClient.PostAsJsonAsync("/bookings", new
        {
            resourceId = TestDataSeeder.AcmeApprovalRequiredResourceId, startUtc = start, endUtc = start.AddHours(1), quantity = 1,
        });
        var created = await createResponse.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions);

        using var adminClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var response = await adminClient.PostAsJsonAsync($"/bookings/{created!.Id}/reject", new { decisionNote = (string?)null });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions);
        Assert.Equal(BookingStatus.Rejected, body!.Status);
    }

    [Fact]
    public async Task ApproveBooking_AsSysAdmin_ConfirmsTheBooking()
    {
        using var memberClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var start = TestDataSeeder.AvailabilityAnchorUtc.AddHours(2).AddDays(72);
        var createResponse = await memberClient.PostAsJsonAsync("/bookings", new
        {
            resourceId = TestDataSeeder.AcmeApprovalRequiredResourceId, startUtc = start, endUtc = start.AddHours(1), quantity = 1,
        });
        var created = await createResponse.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions);

        using var sysAdminClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeSysAdminEmail);
        var response = await sysAdminClient.PostAsJsonAsync($"/bookings/{created!.Id}/approve", new { decisionNote = (string?)null });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions);
        Assert.Equal(BookingStatus.Confirmed, body!.Status);
    }

    [Fact]
    public async Task RejectBooking_AsSysAdmin_RejectsTheBooking()
    {
        using var memberClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var start = TestDataSeeder.AvailabilityAnchorUtc.AddHours(2).AddDays(73);
        var createResponse = await memberClient.PostAsJsonAsync("/bookings", new
        {
            resourceId = TestDataSeeder.AcmeApprovalRequiredResourceId, startUtc = start, endUtc = start.AddHours(1), quantity = 1,
        });
        var created = await createResponse.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions);

        using var sysAdminClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeSysAdminEmail);
        var response = await sysAdminClient.PostAsJsonAsync($"/bookings/{created!.Id}/reject", new { decisionNote = (string?)null });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions);
        Assert.Equal(BookingStatus.Rejected, body!.Status);
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

    // A plain Approver facing a series with many pending occurrences must be able to approve them all in
    // one call instead of one at a time - see ApproveBookingCommandRequest.ApproveRemainingSeries.
    [Fact]
    public async Task ApproveBooking_WithApproveRemainingSeriesTrue_ApprovesEveryOtherPendingOccurrenceInTheSeries()
    {
        using var memberClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var createResponse = await memberClient.PostAsJsonAsync("/bookings/series", new
        {
            resourceId = TestDataSeeder.AcmeApprovalRequiredResourceId, startDate = SeriesStartDate(80), startTime = "09:00:00", endTime = "10:00:00",
            frequency = RecurrenceFrequency.Daily, interval = 1, endDate = (DateOnly?)null, occurrenceCount = 3, quantity = 1,
        });
        var created = await createResponse.Content.ReadFromJsonAsync<CreateSeriesResponse>(JsonOptions);
        var occurrences = created!.CreatedOccurrences.OrderBy(occurrence => occurrence.StartUtc).ToList();
        Assert.All(occurrences, occurrence => Assert.Equal(BookingStatus.Pending, occurrence.Status));

        using var approverClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeApproverEmail);
        var response = await approverClient.PostAsJsonAsync(
            $"/bookings/{occurrences[0].Id}/approve", new { decisionNote = "Approved for the whole series", approveRemainingSeries = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApproveResponse>(JsonOptions);
        Assert.Equal(BookingStatus.Confirmed, body!.Status);
        Assert.Empty(body.CascadedConflicts);
        Assert.Equal(2, body.CascadedApprovedOccurrenceIds.Count);
        Assert.Contains(occurrences[1].Id, body.CascadedApprovedOccurrenceIds);
        Assert.Contains(occurrences[2].Id, body.CascadedApprovedOccurrenceIds);

        var seriesResponse = await memberClient.GetAsync($"/bookings/series/{created.SeriesId}");
        var seriesBody = await seriesResponse.Content.ReadFromJsonAsync<GetSeriesResponse>(JsonOptions);
        Assert.All(seriesBody!.Occurrences, occurrence => Assert.Equal(BookingStatus.Confirmed, occurrence.Status));
    }

    // ApproveRemainingSeries=false (the default) must behave exactly like today - only the targeted
    // booking is decided, siblings are left untouched for the approver to decide individually.
    [Fact]
    public async Task ApproveBooking_WithApproveRemainingSeriesOmitted_OnlyApprovesTheTargetedOccurrence()
    {
        using var memberClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var createResponse = await memberClient.PostAsJsonAsync("/bookings/series", new
        {
            resourceId = TestDataSeeder.AcmeApprovalRequiredResourceId, startDate = SeriesStartDate(90), startTime = "09:00:00", endTime = "10:00:00",
            frequency = RecurrenceFrequency.Daily, interval = 1, endDate = (DateOnly?)null, occurrenceCount = 2, quantity = 1,
        });
        var created = await createResponse.Content.ReadFromJsonAsync<CreateSeriesResponse>(JsonOptions);
        var occurrences = created!.CreatedOccurrences.OrderBy(occurrence => occurrence.StartUtc).ToList();

        using var approverClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeApproverEmail);
        var response = await approverClient.PostAsJsonAsync($"/bookings/{occurrences[0].Id}/approve", new { decisionNote = (string?)null });
        var body = await response.Content.ReadFromJsonAsync<ApproveResponse>(JsonOptions);
        Assert.Empty(body!.CascadedApprovedOccurrenceIds);

        var seriesResponse = await memberClient.GetAsync($"/bookings/series/{created.SeriesId}");
        var seriesBody = await seriesResponse.Content.ReadFromJsonAsync<GetSeriesResponse>(JsonOptions);
        Assert.Equal(BookingStatus.Confirmed, seriesBody!.Occurrences.Single(o => o.Id == occurrences[0].Id).Status);
        Assert.Equal(BookingStatus.Pending, seriesBody.Occurrences.Single(o => o.Id == occurrences[1].Id).Status);
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
    public async Task ApproveBooking_AsPlainMember_ReturnsForbidden()
    {
        using var memberClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var start = TestDataSeeder.AvailabilityAnchorUtc.AddHours(2).AddDays(66);
        var createResponse = await memberClient.PostAsJsonAsync("/bookings", new
        {
            resourceId = TestDataSeeder.AcmeApprovalRequiredResourceId, startUtc = start, endUtc = start.AddHours(1), quantity = 1,
        });
        var created = await createResponse.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions);

        var response = await memberClient.PostAsJsonAsync($"/bookings/{created!.Id}/approve", new { decisionNote = (string?)null });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RejectBooking_AsPlainMember_ReturnsForbidden()
    {
        using var memberClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var start = TestDataSeeder.AvailabilityAnchorUtc.AddHours(2).AddDays(67);
        var createResponse = await memberClient.PostAsJsonAsync("/bookings", new
        {
            resourceId = TestDataSeeder.AcmeApprovalRequiredResourceId, startUtc = start, endUtc = start.AddHours(1), quantity = 1,
        });
        var created = await createResponse.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions);

        var response = await memberClient.PostAsJsonAsync($"/bookings/{created!.Id}/reject", new { decisionNote = (string?)null });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Mirrors ApproveBooking_ForAnotherTenantsBooking_ReturnsNotFound above - Reject had no equivalent
    // cross-tenant test at all before this.
    [Fact]
    public async Task RejectBooking_ForAnotherTenantsBooking_ReturnsNotFound()
    {
        using var globexClient = await AuthenticatedClientAsync(TestDataSeeder.GlobexMemberEmail);
        var start = TestDataSeeder.AvailabilityAnchorUtc.AddHours(2).AddDays(68);
        var createResponse = await globexClient.PostAsJsonAsync("/bookings", new
        {
            resourceId = TestDataSeeder.GlobexResourceId, startUtc = start, endUtc = start.AddHours(1), quantity = 1,
        });
        var created = await createResponse.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions);

        using var acmeApproverClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeApproverEmail);
        var response = await acmeApproverClient.PostAsJsonAsync($"/bookings/{created!.Id}/reject", new { decisionNote = (string?)null });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // The deeper, resource-specific ApprovalAuthorization check (409 Booking.ApprovalForbidden) - distinct
    // from the route-level 403 a plain Member gets above. This caller genuinely holds the Approver role
    // (passes [Authorize(Roles="Approver,...")]) but isn't a ResourceApprover for THIS resource. Only ever
    // unit-tested via mocks before this - never proven through the real HTTP pipeline + real DB.
    [Fact]
    public async Task ApproveBooking_AsAnApproverNotAssignedToThisResource_ReturnsConflictWithApprovalForbiddenCode()
    {
        using var memberClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var start = TestDataSeeder.AvailabilityAnchorUtc.AddHours(2).AddDays(69);
        var createResponse = await memberClient.PostAsJsonAsync("/bookings", new
        {
            resourceId = TestDataSeeder.AcmeApprovalRequiredResourceId, startUtc = start, endUtc = start.AddHours(1), quantity = 1,
        });
        var created = await createResponse.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions);

        using var unassignedApproverClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeUnassignedApproverEmail);
        var response = await unassignedApproverClient.PostAsJsonAsync($"/bookings/{created!.Id}/approve", new { decisionNote = (string?)null });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Booking.ApprovalForbidden", await ReadErrorCodeAsync(response));
    }

    // ResourceApprover assignment is never encoded in the JWT - only the global "Approver" role is (see
    // ApprovalAuthorization.EnsureCallerCanDecideAsync). This proves that in practice: the approver's
    // token is issued BEFORE the assignment even exists, yet the very same token can approve immediately
    // after an admin creates the assignment - no re-login, no new token, because the assignment check is
    // a live DB read on every request, not something the token itself carries.
    [Fact]
    public async Task AssignResourceApprover_ThenApproveWithATokenIssuedBeforeTheAssignmentExisted_Succeeds()
    {
        using var approverClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeUnassignedApproverEmail);

        using var adminClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var createResourceResponse = await adminClient.PostAsJsonAsync("/resources", new
        {
            resourceTypeId = TestDataSeeder.ResourceTypeId,
            name = $"Live Grant Room {Guid.NewGuid()}",
            capacity = 2,
            requiresApproval = true,
            timeZoneId = "UTC",
        });
        var resource = await createResourceResponse.Content.ReadFromJsonAsync<ResourceIdResponse>(JsonOptions);
        foreach (var dayOfWeek in Enum.GetValues<DayOfWeek>())
        {
            await adminClient.PostAsJsonAsync($"/resources/{resource!.Id}/availability-rules",
                new { dayOfWeek, startTime = "00:00:00", endTime = "23:59:59" });
        }

        // The assignment must exist before the booking is created - CreateBookingCommandHandler itself
        // rejects a RequiresApproval resource with zero configured approvers (Booking.NoApproverConfigured).
        // What this test proves is unaffected by that ordering: approverClient's token (obtained above,
        // before this call) still predates the assignment.
        var assignResponse = await adminClient.PostAsJsonAsync(
            $"/resources/{resource!.Id}/approvers", new { userId = TestDataSeeder.AcmeUnassignedApproverUserId });
        Assert.Equal(HttpStatusCode.Created, assignResponse.StatusCode);

        using var memberClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var start = TestDataSeeder.AvailabilityAnchorUtc.AddHours(2).AddDays(90);
        var createBookingResponse = await memberClient.PostAsJsonAsync(
            "/bookings", new { resourceId = resource.Id, startUtc = start, endUtc = start.AddHours(1), quantity = 1 });
        var booking = await createBookingResponse.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions);

        var approveResponse = await approverClient.PostAsJsonAsync($"/bookings/{booking!.Id}/approve", new { decisionNote = (string?)null });

        Assert.Equal(HttpStatusCode.OK, approveResponse.StatusCode);
        var approved = await approveResponse.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions);
        Assert.Equal(BookingStatus.Confirmed, approved!.Status);
    }

    // The mirror image of the test above: revocation must also be immediate and live-checked, not
    // something a caller can keep exploiting with an already-issued token just because nothing forces
    // them to log in again.
    [Fact]
    public async Task RemoveResourceApprover_ThenApproveWithTheSameToken_IsImmediatelyRejected()
    {
        using var adminClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var createResourceResponse = await adminClient.PostAsJsonAsync("/resources", new
        {
            resourceTypeId = TestDataSeeder.ResourceTypeId,
            name = $"Live Revoke Room {Guid.NewGuid()}",
            capacity = 2,
            requiresApproval = true,
            timeZoneId = "UTC",
        });
        var resource = await createResourceResponse.Content.ReadFromJsonAsync<ResourceIdResponse>(JsonOptions);
        foreach (var dayOfWeek in Enum.GetValues<DayOfWeek>())
        {
            await adminClient.PostAsJsonAsync($"/resources/{resource!.Id}/availability-rules",
                new { dayOfWeek, startTime = "00:00:00", endTime = "23:59:59" });
        }
        // Two approvers, so removing one does not hit the RemoveResourceApprover.LastRemaining guard.
        await adminClient.PostAsJsonAsync($"/resources/{resource!.Id}/approvers", new { userId = TestDataSeeder.AcmeApproverUserId });
        await adminClient.PostAsJsonAsync($"/resources/{resource.Id}/approvers", new { userId = TestDataSeeder.AcmeUnassignedApproverUserId });

        using var approverClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeUnassignedApproverEmail);

        using var memberClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);
        var start = TestDataSeeder.AvailabilityAnchorUtc.AddHours(2).AddDays(91);
        var createBookingResponse = await memberClient.PostAsJsonAsync(
            "/bookings", new { resourceId = resource.Id, startUtc = start, endUtc = start.AddHours(1), quantity = 1 });
        var booking = await createBookingResponse.Content.ReadFromJsonAsync<BookingResponse>(JsonOptions);

        var removeResponse = await adminClient.DeleteAsync($"/resources/{resource.Id}/approvers/{TestDataSeeder.AcmeUnassignedApproverUserId}");
        Assert.Equal(HttpStatusCode.NoContent, removeResponse.StatusCode);

        var approveResponse = await approverClient.PostAsJsonAsync($"/bookings/{booking!.Id}/approve", new { decisionNote = (string?)null });

        Assert.Equal(HttpStatusCode.Conflict, approveResponse.StatusCode);
        Assert.Equal("Booking.ApprovalForbidden", await ReadErrorCodeAsync(approveResponse));
    }

    // Booking.NoApproverConfigured (409) had no API-level test - only ever unit-tested via mocks.
    [Fact]
    public async Task CreateBooking_ForResourceRequiringApprovalWithNoConfiguredApprovers_ReturnsConflictWithNoApproverConfiguredCode()
    {
        using var adminClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var createResourceResponse = await adminClient.PostAsJsonAsync("/resources", new
        {
            resourceTypeId = TestDataSeeder.ResourceTypeId,
            name = $"No Approver Room {Guid.NewGuid()}",
            capacity = 2,
            requiresApproval = true,
            timeZoneId = "UTC",
        });
        var resource = await createResourceResponse.Content.ReadFromJsonAsync<ResourceIdResponse>(JsonOptions);
        foreach (var dayOfWeek in Enum.GetValues<DayOfWeek>())
        {
            await adminClient.PostAsJsonAsync($"/resources/{resource!.Id}/availability-rules",
                new { dayOfWeek, startTime = "00:00:00", endTime = "23:59:59" });
        }
        var start = TestDataSeeder.AvailabilityAnchorUtc.AddHours(2).AddDays(70);

        var response = await adminClient.PostAsJsonAsync("/bookings", new { resourceId = resource!.Id, startUtc = start, endUtc = start.AddHours(1), quantity = 1 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Booking.NoApproverConfigured", await ReadErrorCodeAsync(response));
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

    private sealed record BookingResponse(Guid Id, Guid ResourceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Quantity, BookingStatus Status);

    private sealed record ResourceIdResponse(Guid Id);

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
        Guid BookingId, Guid ResourceId, Guid UserId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Quantity, DateTimeOffset ExpiresAtUtc,
        Guid? SeriesId);

    private sealed record ApprovalCascadeConflict(Guid BookingId, string Reason);

    private sealed record ApproveResponse(
        Guid Id, Guid ResourceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Quantity, BookingStatus Status,
        IReadOnlyList<Guid> CascadedApprovedOccurrenceIds, IReadOnlyList<ApprovalCascadeConflict> CascadedConflicts);
}
