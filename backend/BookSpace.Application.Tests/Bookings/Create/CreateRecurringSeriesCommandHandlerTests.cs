using BookSpace.Application.Bookings;
using BookSpace.Application.Common;
using BookSpace.Application.Resources;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Bookings;

public sealed class CreateRecurringSeriesCommandHandlerTests
{
    private readonly Mock<IResourceRepository> _resourceRepository = new();
    private readonly Mock<IAvailabilityRuleRepository> _availabilityRuleRepository = new();
    private readonly Mock<IBlackoutPeriodRepository> _blackoutPeriodRepository = new();
    private readonly Mock<IBookingAvailabilityRepository> _bookingAvailabilityRepository = new();
    private readonly Mock<IBookingRepository> _bookingRepository = new();
    private readonly Mock<IRecurringSeriesRepository> _recurringSeriesRepository = new();
    private readonly Mock<IResourceApproverRepository> _resourceApproverRepository = new();
    private readonly Mock<IApprovalRequestRepository> _approvalRequestRepository = new();
    private readonly Mock<ITenantRepository> _tenantRepository = new();
    private readonly Mock<ICurrentUserContext> _currentUserContext = new();

    private static readonly DateOnly StartDate = new(2026, 1, 5); // a Monday
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    private CreateRecurringSeriesCommandHandler CreateSut() => new(
        new PassThroughResourceBookingLock(),
        _resourceRepository.Object,
        _availabilityRuleRepository.Object,
        _blackoutPeriodRepository.Object,
        _bookingAvailabilityRepository.Object,
        _bookingRepository.Object,
        _recurringSeriesRepository.Object,
        _resourceApproverRepository.Object,
        _approvalRequestRepository.Object,
        _tenantRepository.Object,
        _currentUserContext.Object);

    private static Resource CreateResource(int capacity = 8, bool requiresApproval = false) => new()
    {
        Id = Guid.NewGuid(), TenantId = TenantId, ResourceTypeId = Guid.NewGuid(), Name = "Meeting Room",
        Capacity = capacity, Status = ResourceStatus.Active, TimeZoneId = "UTC", RequiresApproval = requiresApproval,
    };

    private static AvailabilityRule OpenEveryDay(Resource resource) => new()
    {
        Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
        DayOfWeek = StartDate.DayOfWeek, StartTime = TimeOnly.MinValue, EndTime = TimeOnly.MaxValue,
    };

    private void SetupResource(Resource resource)
    {
        _currentUserContext.SetupGet(c => c.TenantId).Returns(TenantId);
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        // Open every day of the week so daily/weekly candidate dates all fall inside an open period,
        // regardless of which day of the week they land on.
        _availabilityRuleRepository
            .Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enum.GetValues<DayOfWeek>().Select(day => new AvailabilityRule
            {
                Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
                DayOfWeek = day, StartTime = TimeOnly.MinValue, EndTime = TimeOnly.MaxValue,
            }).ToList());
        _blackoutPeriodRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _bookingAvailabilityRepository
            .Setup(r => r.GetActiveBookingsAsync(resource.Id, It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    private static CreateRecurringSeriesCommandRequest CreateRequest(
        Guid resourceId, RecurrenceFrequency frequency = RecurrenceFrequency.Daily, int interval = 1,
        int? occurrenceCount = 3, DateOnly? endDate = null, int quantity = 1) =>
        new(resourceId, StartDate, new TimeOnly(9, 0), new TimeOnly(10, 0), frequency, interval, endDate, occurrenceCount, quantity);

    [Fact]
    public async Task Handle_WithAllOccurrencesEligible_CreatesSeriesAndEveryOccurrenceAsConfirmed()
    {
        var resource = CreateResource();
        SetupResource(resource);
        var sut = CreateSut();

        var result = await sut.Handle(CreateRequest(resource.Id, occurrenceCount: 3), CancellationToken.None);

        Assert.Equal(3, result.RequestedOccurrenceCount);
        Assert.Equal(3, result.CreatedOccurrences.Count);
        Assert.Empty(result.Conflicts);
        Assert.All(result.CreatedOccurrences, occurrence => Assert.Equal(BookingStatus.Confirmed, occurrence.Status));
        _recurringSeriesRepository.Verify(r => r.AddAsync(It.IsAny<RecurringSeries>(), It.IsAny<CancellationToken>()), Times.Once);
        _bookingRepository.Verify(r => r.AddAsync(It.IsAny<Booking>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
        _recurringSeriesRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithOneOccurrenceOverlappingABlackout_ReportsExactlyThatConflictAndStillCreatesTheRest()
    {
        var resource = CreateResource();
        SetupResource(resource);
        var secondOccurrenceDate = StartDate.AddDays(1);
        var blackout = new BlackoutPeriod
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
            StartUtc = new DateTimeOffset(secondOccurrenceDate.Year, secondOccurrenceDate.Month, secondOccurrenceDate.Day, 9, 30, 0, TimeSpan.Zero),
            EndUtc = new DateTimeOffset(secondOccurrenceDate.Year, secondOccurrenceDate.Month, secondOccurrenceDate.Day, 10, 30, 0, TimeSpan.Zero),
            Reason = "Maintenance",
        };
        _blackoutPeriodRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([blackout]);
        var sut = CreateSut();

        var result = await sut.Handle(CreateRequest(resource.Id, occurrenceCount: 3), CancellationToken.None);

        Assert.Equal(3, result.RequestedOccurrenceCount);
        Assert.Equal(2, result.CreatedOccurrences.Count);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(secondOccurrenceDate, conflict.Date);
        Assert.Equal(ErrorCodes.BookingBlackoutConflict, conflict.Reason);
    }

    // The middle-position conflict above proves conflicts are surfaced correctly, but not that the loop's
    // FIRST or LAST iteration is handled the same way - an off-by-one in the per-occurrence loop could
    // easily mishandle either edge while a middle index still works.
    [Fact]
    public async Task Handle_WithTheFirstOccurrenceConflicting_ReportsThatConflictAndStillCreatesTheRest()
    {
        var resource = CreateResource();
        SetupResource(resource);
        var blackout = new BlackoutPeriod
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
            StartUtc = new DateTimeOffset(StartDate.Year, StartDate.Month, StartDate.Day, 9, 30, 0, TimeSpan.Zero),
            EndUtc = new DateTimeOffset(StartDate.Year, StartDate.Month, StartDate.Day, 10, 30, 0, TimeSpan.Zero),
            Reason = "Maintenance",
        };
        _blackoutPeriodRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([blackout]);
        var sut = CreateSut();

        var result = await sut.Handle(CreateRequest(resource.Id, occurrenceCount: 3), CancellationToken.None);

        Assert.Equal(3, result.RequestedOccurrenceCount);
        Assert.Equal(2, result.CreatedOccurrences.Count);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(StartDate, conflict.Date);
        Assert.Equal(ErrorCodes.BookingBlackoutConflict, conflict.Reason);
    }

    [Fact]
    public async Task Handle_WithTheLastOccurrenceConflicting_ReportsThatConflictAndStillCreatesTheRest()
    {
        var resource = CreateResource();
        SetupResource(resource);
        var thirdOccurrenceDate = StartDate.AddDays(2);
        var blackout = new BlackoutPeriod
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
            StartUtc = new DateTimeOffset(thirdOccurrenceDate.Year, thirdOccurrenceDate.Month, thirdOccurrenceDate.Day, 9, 30, 0, TimeSpan.Zero),
            EndUtc = new DateTimeOffset(thirdOccurrenceDate.Year, thirdOccurrenceDate.Month, thirdOccurrenceDate.Day, 10, 30, 0, TimeSpan.Zero),
            Reason = "Maintenance",
        };
        _blackoutPeriodRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([blackout]);
        var sut = CreateSut();

        var result = await sut.Handle(CreateRequest(resource.Id, occurrenceCount: 3), CancellationToken.None);

        Assert.Equal(3, result.RequestedOccurrenceCount);
        Assert.Equal(2, result.CreatedOccurrences.Count);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(thirdOccurrenceDate, conflict.Date);
        Assert.Equal(ErrorCodes.BookingBlackoutConflict, conflict.Reason);
    }

    [Fact]
    public async Task Handle_WithTwoOfFiveOccurrencesConflicting_ReportsBothConflictsAndCreatesTheOtherThree()
    {
        var resource = CreateResource();
        SetupResource(resource);
        var secondOccurrenceDate = StartDate.AddDays(1);
        var fourthOccurrenceDate = StartDate.AddDays(3);
        BlackoutPeriod BlackoutFor(DateOnly date) => new()
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
            StartUtc = new DateTimeOffset(date.Year, date.Month, date.Day, 9, 30, 0, TimeSpan.Zero),
            EndUtc = new DateTimeOffset(date.Year, date.Month, date.Day, 10, 30, 0, TimeSpan.Zero),
            Reason = "Maintenance",
        };
        _blackoutPeriodRepository
            .Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([BlackoutFor(secondOccurrenceDate), BlackoutFor(fourthOccurrenceDate)]);
        var sut = CreateSut();

        var result = await sut.Handle(CreateRequest(resource.Id, occurrenceCount: 5), CancellationToken.None);

        Assert.Equal(5, result.RequestedOccurrenceCount);
        Assert.Equal(3, result.CreatedOccurrences.Count);
        Assert.Equal(2, result.Conflicts.Count);
        Assert.Contains(result.Conflicts, c => c.Date == secondOccurrenceDate);
        Assert.Contains(result.Conflicts, c => c.Date == fourthOccurrenceDate);
        Assert.All(result.Conflicts, c => Assert.Equal(ErrorCodes.BookingBlackoutConflict, c.Reason));
    }

    // The !resolution.Succeeded branch (spring-forward NonexistentLocalTime) had zero coverage at the
    // handler level before this - RecurringOccurrenceGeneratorTests proves TryResolveOccurrence in
    // isolation, but never chained through CreateRecurringSeriesCommandHandler's own loop, which is a
    // structurally different code path (BookingEligibilityChecker's blackout conflicts, exercised by
    // every other conflict test in this file, never touch this branch at all).
    [Fact]
    public async Task Handle_WithAnOccurrenceLandingInASpringForwardGap_ReportsNonexistentLocalTimeAndStillCreatesTheRest()
    {
        var resource = CreateResource();
        resource.TimeZoneId = "Europe/Sarajevo";
        SetupResource(resource);
        var transitionSunday = LastSundayOfMonth(DateTime.UtcNow.Year + 1, 3);
        // 02:30 never occurred that day in Europe/Sarajevo (clocks jump 02:00 -> 03:00).
        var request = new CreateRecurringSeriesCommandRequest(
            resource.Id, transitionSunday, new TimeOnly(2, 30), new TimeOnly(4, 0),
            RecurrenceFrequency.Weekly, Interval: 1, EndDate: null, OccurrenceCount: 3, Quantity: 1);
        var sut = CreateSut();

        var result = await sut.Handle(request, CancellationToken.None);

        Assert.Equal(3, result.RequestedOccurrenceCount);
        Assert.Equal(2, result.CreatedOccurrences.Count);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(transitionSunday, conflict.Date);
        Assert.Equal("NonexistentLocalTime", conflict.Reason);
    }

    private static DateOnly LastSundayOfMonth(int year, int month)
    {
        var lastDay = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        while (lastDay.DayOfWeek != DayOfWeek.Sunday)
        {
            lastDay = lastDay.AddDays(-1);
        }

        return lastDay;
    }

    [Fact]
    public async Task Handle_WithNoEligibleOccurrenceAtAll_ThrowsConflictExceptionAndCreatesNothing()
    {
        var resource = CreateResource();
        SetupResource(resource);
        _blackoutPeriodRepository
            .Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new BlackoutPeriod
            {
                Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
                StartUtc = DateTimeOffset.MinValue.AddYears(1), EndUtc = DateTimeOffset.MaxValue.AddYears(-1), Reason = "Permanent closure",
            }]);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<ConflictException>(() => sut.Handle(CreateRequest(resource.Id), CancellationToken.None));

        Assert.Equal(ErrorCodes.RecurringSeriesNoValidOccurrences, exception.ErrorCode);
        _recurringSeriesRepository.Verify(r => r.AddAsync(It.IsAny<RecurringSeries>(), It.IsAny<CancellationToken>()), Times.Never);
        _recurringSeriesRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_ForResourceRequiringApproval_CreatesEveryOccurrenceAsPendingWithItsOwnApprovalRequest()
    {
        var resource = CreateResource(requiresApproval: true);
        SetupResource(resource);
        _resourceApproverRepository
            .Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ResourceApprover { Id = Guid.NewGuid(), TenantId = TenantId, ResourceId = resource.Id, UserId = Guid.NewGuid() }]);
        _tenantRepository
            .Setup(r => r.FindByIdAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Tenant { Id = TenantId, Name = "Tenant", DefaultTimeZoneId = "UTC", ApprovalExpiryHours = 48, CreatedAtUtc = DateTimeOffset.UtcNow });
        var capturedApprovalRequests = new List<ApprovalRequest>();
        _approvalRequestRepository
            .Setup(r => r.AddAsync(It.IsAny<ApprovalRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ApprovalRequest, CancellationToken>((request, _) => capturedApprovalRequests.Add(request))
            .Returns(Task.CompletedTask);
        var sut = CreateSut();

        var result = await sut.Handle(CreateRequest(resource.Id, occurrenceCount: 3), CancellationToken.None);

        Assert.Equal(3, result.CreatedOccurrences.Count);
        Assert.All(result.CreatedOccurrences, occurrence => Assert.Equal(BookingStatus.Pending, occurrence.Status));
        Assert.Equal(3, capturedApprovalRequests.Count);
        // Each ApprovalRequest must correlate 1:1 with its OWN occurrence's Booking, never a shared or
        // duplicated BookingId - a bug that pointed every ApprovalRequest at the same booking would
        // previously have passed this test (it only checked AddAsync's call count).
        var createdBookingIds = result.CreatedOccurrences.Select(occurrence => occurrence.Id).ToHashSet();
        var approvalRequestBookingIds = capturedApprovalRequests.Select(request => request.BookingId).ToHashSet();
        Assert.Equal(createdBookingIds, approvalRequestBookingIds);
        Assert.Equal(3, approvalRequestBookingIds.Count); // no duplicates
    }

    [Fact]
    public async Task Handle_ForResourceRequiringApprovalWithNoConfiguredApprover_ThrowsConflictExceptionWithNoApproverConfiguredCode()
    {
        var resource = CreateResource(requiresApproval: true);
        SetupResource(resource);
        _resourceApproverRepository
            .Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ResourceApprover>)[]);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<ConflictException>(() => sut.Handle(CreateRequest(resource.Id), CancellationToken.None));

        Assert.Equal(ErrorCodes.BookingNoApproverConfigured, exception.ErrorCode);
        _recurringSeriesRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // Every other test in this file uses the default Daily frequency - Weekly/Monthly's integration with
    // BookingEligibilityChecker/Booking creation through the full handler had never been exercised
    // (Weekly/Monthly's date-generation logic itself is unit-tested separately in
    // RecurringOccurrenceGeneratorTests, but never chained through this handler before).
    [Fact]
    public async Task Handle_WithWeeklyFrequency_CreatesAllOccurrencesOnTheSameWeekday()
    {
        var resource = CreateResource();
        SetupResource(resource);
        var sut = CreateSut();

        var result = await sut.Handle(CreateRequest(resource.Id, frequency: RecurrenceFrequency.Weekly, occurrenceCount: 3), CancellationToken.None);

        Assert.Equal(3, result.CreatedOccurrences.Count);
        Assert.Empty(result.Conflicts);
        var startDates = result.CreatedOccurrences.Select(o => DateOnly.FromDateTime(o.StartUtc.UtcDateTime)).OrderBy(d => d).ToList();
        Assert.Equal([StartDate, StartDate.AddDays(7), StartDate.AddDays(14)], startDates);
    }

    [Fact]
    public async Task Handle_WithMonthlyFrequency_CreatesAllOccurrencesOnTheSameDayOfMonth()
    {
        var resource = CreateResource();
        SetupResource(resource);
        var sut = CreateSut();

        var result = await sut.Handle(CreateRequest(resource.Id, frequency: RecurrenceFrequency.Monthly, occurrenceCount: 3), CancellationToken.None);

        Assert.Equal(3, result.CreatedOccurrences.Count);
        Assert.Empty(result.Conflicts);
        var startDates = result.CreatedOccurrences.Select(o => DateOnly.FromDateTime(o.StartUtc.UtcDateTime)).OrderBy(d => d).ToList();
        Assert.Equal([StartDate, StartDate.AddMonths(1), StartDate.AddMonths(2)], startDates);
    }

    // Every other test uses OccurrenceCount - the EndDate end-condition was never driven through the
    // full handler.
    [Fact]
    public async Task Handle_WithAnEndDateInsteadOfOccurrenceCount_CreatesOccurrencesUntilTheEndDateInclusive()
    {
        var resource = CreateResource();
        SetupResource(resource);
        var sut = CreateSut();

        var result = await sut.Handle(CreateRequest(resource.Id, occurrenceCount: null, endDate: StartDate.AddDays(2)), CancellationToken.None);

        Assert.Equal(3, result.RequestedOccurrenceCount); // StartDate, +1, +2 - inclusive of the end date
        Assert.Equal(3, result.CreatedOccurrences.Count);
    }

    [Fact]
    public async Task Handle_WithQuantityGreaterThanOne_UsesThatQuantityForEveryOccurrence()
    {
        var resource = CreateResource(capacity: 8);
        SetupResource(resource);
        var sut = CreateSut();

        await sut.Handle(CreateRequest(resource.Id, occurrenceCount: 2, quantity: 3), CancellationToken.None);

        _bookingRepository.Verify(r => r.AddAsync(It.Is<Booking>(booking => booking.Quantity == 3), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // Every other test only verifies AddAsync/SaveChangesAsync call counts - the persisted RecurringSeries
    // object's own field values (TimeZoneId snapshot, StartDate/Interval/OccurrenceCount/Quantity) were
    // never actually asserted.
    [Fact]
    public async Task Handle_PersistsTheSeriesWithTheRequestsFieldsAndTheResourcesSnapshottedTimeZone()
    {
        var resource = CreateResource();
        resource.TimeZoneId = "Europe/Sarajevo";
        SetupResource(resource);
        RecurringSeries? capturedSeries = null;
        _recurringSeriesRepository
            .Setup(r => r.AddAsync(It.IsAny<RecurringSeries>(), It.IsAny<CancellationToken>()))
            .Callback<RecurringSeries, CancellationToken>((series, _) => capturedSeries = series)
            .Returns(Task.CompletedTask);
        var sut = CreateSut();

        await sut.Handle(CreateRequest(resource.Id, frequency: RecurrenceFrequency.Weekly, interval: 2, occurrenceCount: 4, quantity: 2), CancellationToken.None);

        Assert.NotNull(capturedSeries);
        // TenantId/UserId must always be server-derived from ICurrentUserContext, never client-suppliable
        // - a real tenant-isolation invariant that no test in this file previously asserted.
        Assert.Equal(TenantId, capturedSeries!.TenantId);
        Assert.Equal(UserId, capturedSeries.UserId);
        Assert.Equal(resource.Id, capturedSeries.ResourceId);
        Assert.Equal(StartDate, capturedSeries.StartDate);
        Assert.Equal(RecurrenceFrequency.Weekly, capturedSeries.Frequency);
        Assert.Equal(2, capturedSeries.Interval);
        Assert.Equal(4, capturedSeries.OccurrenceCount);
        Assert.Equal(2, capturedSeries.Quantity);
        Assert.Equal("Europe/Sarajevo", capturedSeries.TimeZoneId); // snapshotted from the resource, never client-suppliable
    }

    [Fact]
    public async Task Handle_WithUnknownResource_ThrowsNotFoundException()
    {
        _currentUserContext.SetupGet(c => c.TenantId).Returns(TenantId);
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _resourceRepository.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Resource?)null);
        var sut = CreateSut();

        await Assert.ThrowsAsync<NotFoundException>(() => sut.Handle(CreateRequest(Guid.NewGuid()), CancellationToken.None));
    }
}
