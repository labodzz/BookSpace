using BookSpace.Application.Common;
using BookSpace.Application.Resources;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Resources;

public sealed class GetResourceAvailabilityQueryHandlerTests
{
    private readonly Mock<IResourceRepository> _resourceRepository = new();
    private readonly Mock<IAvailabilityRuleRepository> _availabilityRuleRepository = new();
    private readonly Mock<IBlackoutPeriodRepository> _blackoutPeriodRepository = new();
    private readonly Mock<IBookingAvailabilityRepository> _bookingAvailabilityRepository = new();

    private static readonly DateOnly Date = new(2026, 9, 7);

    private GetResourceAvailabilityQueryHandler CreateSut() => new(
        _resourceRepository.Object, _availabilityRuleRepository.Object, _blackoutPeriodRepository.Object, _bookingAvailabilityRepository.Object);

    private static DateTimeOffset At(int hour, int minute = 0) => new(Date.Year, Date.Month, Date.Day, hour, minute, 0, TimeSpan.Zero);

    private static Resource CreateResource(int capacity = 8) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        ResourceTypeId = Guid.NewGuid(),
        Name = "Laptop Cart",
        Capacity = capacity,
        Status = ResourceStatus.Active,
        TimeZoneId = "UTC",
    };

    private void SetupNoBlackoutsOrBookings(Resource resource)
    {
        _blackoutPeriodRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _bookingAvailabilityRepository
            .Setup(r => r.GetActiveBookingsAsync(resource.Id, It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    // The blackout range-filter itself (period.StartUtc < rangeEndUtcExclusive && period.EndUtc >
    // rangeStartUtc) had no exact-boundary test - only a mid-range overlapping blackout was ever used.
    [Fact]
    public async Task Handle_WithBlackoutEndingExactlyAtTheRangeStart_ExcludesItFromTheResult()
    {
        var resource = CreateResource(capacity: 8);
        var rule = new AvailabilityRule
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
            DayOfWeek = Date.DayOfWeek, StartTime = TimeOnly.MinValue, EndTime = TimeOnly.MaxValue,
        };
        var rangeStart = new DateTimeOffset(Date.Year, Date.Month, Date.Day, 0, 0, 0, TimeSpan.Zero);
        var blackout = new BlackoutPeriod
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
            StartUtc = rangeStart.AddHours(-1), EndUtc = rangeStart, // ends exactly at rangeStartUtc
            Reason = "Just before",
        };
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _availabilityRuleRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([rule]);
        _blackoutPeriodRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([blackout]);
        _bookingAvailabilityRepository
            .Setup(r => r.GetActiveBookingsAsync(resource.Id, It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var sut = CreateSut();

        var result = await sut.Handle(new GetResourceAvailabilityQueryRequest(resource.Id, Date, Date), CancellationToken.None);

        Assert.Empty(result.Blackouts);
        Assert.Contains(result.BookableSlots, slot => slot.AvailableCapacity == 8);
    }

    [Fact]
    public async Task Handle_WithBlackoutStartingExactlyAtTheRangeEnd_ExcludesItFromTheResult()
    {
        var resource = CreateResource(capacity: 8);
        var rule = new AvailabilityRule
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
            DayOfWeek = Date.DayOfWeek, StartTime = TimeOnly.MinValue, EndTime = TimeOnly.MaxValue,
        };
        var rangeEndExclusive = new DateTimeOffset(Date.AddDays(1).Year, Date.AddDays(1).Month, Date.AddDays(1).Day, 0, 0, 0, TimeSpan.Zero);
        var blackout = new BlackoutPeriod
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
            StartUtc = rangeEndExclusive, EndUtc = rangeEndExclusive.AddHours(1), // starts exactly at rangeEndUtcExclusive
            Reason = "Just after",
        };
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _availabilityRuleRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([rule]);
        _blackoutPeriodRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([blackout]);
        _bookingAvailabilityRepository
            .Setup(r => r.GetActiveBookingsAsync(resource.Id, It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var sut = CreateSut();

        var result = await sut.Handle(new GetResourceAvailabilityQueryRequest(resource.Id, Date, Date), CancellationToken.None);

        Assert.Empty(result.Blackouts);
        Assert.Contains(result.BookableSlots, slot => slot.AvailableCapacity == 8);
    }

    // Same !=Active branch as Inactive/Maintenance (both already tested elsewhere in this file) but
    // Archived specifically had never been exercised by name at this handler level.
    [Fact]
    public async Task Handle_WithArchivedResource_ReturnsNoBookableSlotsButStillComputesTheSchedule()
    {
        var resource = CreateResource();
        resource.Status = ResourceStatus.Archived;
        var rule = new AvailabilityRule
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
            DayOfWeek = Date.DayOfWeek, StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(18, 0),
        };
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _availabilityRuleRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([rule]);
        SetupNoBlackoutsOrBookings(resource);
        var sut = CreateSut();

        var result = await sut.Handle(new GetResourceAvailabilityQueryRequest(resource.Id, Date, Date), CancellationToken.None);

        Assert.NotEmpty(result.OpenPeriods); // schedule is still visible - not 404, not hidden
        Assert.Empty(result.BookableSlots); // but nothing is actually bookable
    }

    // Response echo fields (TimeZoneId, Capacity, Blackouts[].Reason) were never directly asserted -
    // every other test here checks OpenPeriods/BookableSlots/BusyPeriods but not these.
    [Fact]
    public async Task Handle_EchoesResourceTimeZoneCapacityAndBlackoutReasonOnTheResponse()
    {
        var resource = CreateResource(capacity: 6);
        resource.TimeZoneId = "Europe/Sarajevo";
        var blackout = new BlackoutPeriod
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
            StartUtc = At(10), EndUtc = At(11), Reason = "Deep cleaning",
        };
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _availabilityRuleRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _blackoutPeriodRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([blackout]);
        _bookingAvailabilityRepository
            .Setup(r => r.GetActiveBookingsAsync(resource.Id, It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var sut = CreateSut();

        var result = await sut.Handle(new GetResourceAvailabilityQueryRequest(resource.Id, Date, Date), CancellationToken.None);

        Assert.Equal("Europe/Sarajevo", result.TimeZoneId);
        Assert.Equal(6, result.Capacity);
        var echoedBlackout = Assert.Single(result.Blackouts);
        Assert.Equal("Deep cleaning", echoedBlackout.Reason);
    }

    [Fact]
    public async Task Handle_WithUnknownResource_ThrowsNotFoundException()
    {
        _resourceRepository.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Resource?)null);
        var sut = CreateSut();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new GetResourceAvailabilityQueryRequest(Guid.NewGuid(), Date, Date), CancellationToken.None));
    }

    [Fact]
    public async Task Handle_WithNoAvailabilityRules_ReturnsNoOpenPeriodsOrBookableSlots()
    {
        var resource = CreateResource();
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _availabilityRuleRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        SetupNoBlackoutsOrBookings(resource);
        var sut = CreateSut();

        var result = await sut.Handle(new GetResourceAvailabilityQueryRequest(resource.Id, Date, Date), CancellationToken.None);

        Assert.Empty(result.OpenPeriods);
        Assert.Empty(result.BookableSlots);
    }

    [Fact]
    public async Task Handle_WithRuleOnlyOnADifferentWeekday_HasNoOpenPeriodForQueriedDay()
    {
        var resource = CreateResource();
        var rule = new AvailabilityRule
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
            DayOfWeek = Date.AddDays(1).DayOfWeek, StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(18, 0),
        };
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _availabilityRuleRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([rule]);
        SetupNoBlackoutsOrBookings(resource);
        var sut = CreateSut();

        var result = await sut.Handle(new GetResourceAvailabilityQueryRequest(resource.Id, Date, Date), CancellationToken.None);

        Assert.Empty(result.OpenPeriods);
    }

    [Fact]
    public async Task Handle_WithOpenWindowAndNoOccupancies_ReturnsFullCapacityBookableSlot()
    {
        var resource = CreateResource(capacity: 8);
        var rule = new AvailabilityRule
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
            DayOfWeek = Date.DayOfWeek, StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(18, 0),
        };
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _availabilityRuleRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([rule]);
        SetupNoBlackoutsOrBookings(resource);
        var sut = CreateSut();

        var result = await sut.Handle(new GetResourceAvailabilityQueryRequest(resource.Id, Date, Date), CancellationToken.None);

        var slot = Assert.Single(result.BookableSlots);
        Assert.Equal(At(8), slot.StartUtc);
        Assert.Equal(At(18), slot.EndUtc);
        Assert.Equal(8, slot.AvailableCapacity);
    }

    [Fact]
    public async Task Handle_WithTwoOverlappingRulesSameDay_MergesIntoOneOpenPeriod()
    {
        var resource = CreateResource();
        var morning = new AvailabilityRule
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
            DayOfWeek = Date.DayOfWeek, StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(14, 0),
        };
        var afternoon = new AvailabilityRule
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
            DayOfWeek = Date.DayOfWeek, StartTime = new TimeOnly(12, 0), EndTime = new TimeOnly(18, 0),
        };
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _availabilityRuleRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([morning, afternoon]);
        SetupNoBlackoutsOrBookings(resource);
        var sut = CreateSut();

        var result = await sut.Handle(new GetResourceAvailabilityQueryRequest(resource.Id, Date, Date), CancellationToken.None);

        var period = Assert.Single(result.OpenPeriods);
        Assert.Equal(At(8), period.StartUtc);
        Assert.Equal(At(18), period.EndUtc);
    }

    [Fact]
    public async Task Handle_WithBlackoutOverlappingWindow_ExcludesThatPortionFromBookableSlots()
    {
        var resource = CreateResource(capacity: 8);
        var rule = new AvailabilityRule
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
            DayOfWeek = Date.DayOfWeek, StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(18, 0),
        };
        var blackout = new BlackoutPeriod
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
            StartUtc = At(12), EndUtc = At(13), Reason = "Maintenance",
        };
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _availabilityRuleRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([rule]);
        _blackoutPeriodRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([blackout]);
        _bookingAvailabilityRepository
            .Setup(r => r.GetActiveBookingsAsync(resource.Id, It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var sut = CreateSut();

        var result = await sut.Handle(new GetResourceAvailabilityQueryRequest(resource.Id, Date, Date), CancellationToken.None);

        Assert.DoesNotContain(result.BookableSlots, slot => slot.StartUtc < At(13) && slot.EndUtc > At(12));
        Assert.Contains(result.BookableSlots, slot => slot.StartUtc == At(8) && slot.EndUtc == At(12) && slot.AvailableCapacity == 8);
        Assert.Contains(result.BookableSlots, slot => slot.StartUtc == At(13) && slot.EndUtc == At(18) && slot.AvailableCapacity == 8);
    }

    [Fact]
    public async Task Handle_WithBookingPartiallyConsumingCapacity_ReducesAvailableCapacityForOverlapOnly()
    {
        // 8 laptops; a Confirmed booking takes 5 for a two-hour window - the rest of the day should
        // still show full capacity, and the overlap should show exactly 3 remaining.
        var resource = CreateResource(capacity: 8);
        var rule = new AvailabilityRule
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
            DayOfWeek = Date.DayOfWeek, StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(18, 0),
        };
        var booking = new Booking
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id, UserId = Guid.NewGuid(),
            StartUtc = At(10), EndUtc = At(12), Quantity = 5, Status = BookingStatus.Confirmed, CreatedAtUtc = At(0),
        };
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _availabilityRuleRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([rule]);
        _blackoutPeriodRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _bookingAvailabilityRepository
            .Setup(r => r.GetActiveBookingsAsync(resource.Id, It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([booking]);
        var sut = CreateSut();

        var result = await sut.Handle(new GetResourceAvailabilityQueryRequest(resource.Id, Date, Date), CancellationToken.None);

        Assert.Contains(result.BookableSlots, slot => slot.StartUtc == At(8) && slot.EndUtc == At(10) && slot.AvailableCapacity == 8);
        Assert.Contains(result.BookableSlots, slot => slot.StartUtc == At(10) && slot.EndUtc == At(12) && slot.AvailableCapacity == 3);
        Assert.Contains(result.BookableSlots, slot => slot.StartUtc == At(12) && slot.EndUtc == At(18) && slot.AvailableCapacity == 8);
        var busyPeriod = Assert.Single(result.BusyPeriods);
        Assert.Equal(5, busyPeriod.Quantity);
    }

    [Fact]
    public async Task Handle_ForResourceInMaintenance_ReturnsNoBookableSlotsButStillReportsSchedule()
    {
        var resource = CreateResource(capacity: 8);
        resource.Status = ResourceStatus.Maintenance;
        var rule = new AvailabilityRule
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
            DayOfWeek = Date.DayOfWeek, StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(18, 0),
        };
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _availabilityRuleRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([rule]);
        SetupNoBlackoutsOrBookings(resource);
        var sut = CreateSut();

        var result = await sut.Handle(new GetResourceAvailabilityQueryRequest(resource.Id, Date, Date), CancellationToken.None);

        Assert.Empty(result.BookableSlots);
        Assert.NotEmpty(result.OpenPeriods);
    }

    [Fact]
    public async Task Handle_ForInactiveResource_ReturnsNoBookableSlots()
    {
        var resource = CreateResource(capacity: 8);
        resource.Status = ResourceStatus.Inactive;
        var rule = new AvailabilityRule
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
            DayOfWeek = Date.DayOfWeek, StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(18, 0),
        };
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _availabilityRuleRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([rule]);
        SetupNoBlackoutsOrBookings(resource);
        var sut = CreateSut();

        var result = await sut.Handle(new GetResourceAvailabilityQueryRequest(resource.Id, Date, Date), CancellationToken.None);

        Assert.Empty(result.BookableSlots);
    }

    // DST policy tests below use Europe/Sarajevo (real-world CET/CEST rules: clocks spring forward on
    // the last Sunday of March, fall back on the last Sunday of October) rather than a hardcoded date,
    // so the test stays valid regardless of which year it runs in.
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
    public async Task Handle_WithRuleStartingInsideSpringForwardGap_NormalizesForwardInsteadOfThrowing()
    {
        var resource = CreateResource();
        resource.TimeZoneId = "Europe/Sarajevo";
        var springForwardDate = LastSundayOfMonth(DateTime.UtcNow.Year + 1, 3);
        var rule = new AvailabilityRule
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
            DayOfWeek = springForwardDate.DayOfWeek, StartTime = new TimeOnly(2, 30), EndTime = new TimeOnly(4, 0),
        };
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _availabilityRuleRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([rule]);
        SetupNoBlackoutsOrBookings(resource);
        var sut = CreateSut();

        var result = await sut.Handle(
            new GetResourceAvailabilityQueryRequest(resource.Id, springForwardDate, springForwardDate), CancellationToken.None);

        // 02:30 never existed that day (clocks jumped 02:00 -> 03:00 CET->CEST); normalized forward to
        // 03:30 CEST (UTC+2) = 01:30Z. 04:00 was already valid CEST = 02:00Z - the resulting 30-minute
        // open period (instead of the "requested" 90 minutes) is the documented DST-crossing tradeoff.
        var period = Assert.Single(result.OpenPeriods);
        Assert.Equal(new DateTimeOffset(springForwardDate.Year, springForwardDate.Month, springForwardDate.Day, 1, 30, 0, TimeSpan.Zero), period.StartUtc);
        Assert.Equal(new DateTimeOffset(springForwardDate.Year, springForwardDate.Month, springForwardDate.Day, 2, 0, 0, TimeSpan.Zero), period.EndUtc);
    }

    [Fact]
    public async Task Handle_WithRuleStartingInsideFallBackAmbiguousHour_ResolvesToStandardOffsetWithoutThrowing()
    {
        var resource = CreateResource();
        resource.TimeZoneId = "Europe/Sarajevo";
        var fallBackDate = LastSundayOfMonth(DateTime.UtcNow.Year + 1, 10);
        var rule = new AvailabilityRule
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
            DayOfWeek = fallBackDate.DayOfWeek, StartTime = new TimeOnly(2, 30), EndTime = new TimeOnly(5, 0),
        };
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _availabilityRuleRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([rule]);
        SetupNoBlackoutsOrBookings(resource);
        var sut = CreateSut();

        var result = await sut.Handle(new GetResourceAvailabilityQueryRequest(resource.Id, fallBackDate, fallBackDate), CancellationToken.None);

        // 02:30 occurs twice that day (clocks fall 03:00 -> 02:00 CEST->CET). .NET's documented default
        // for an ambiguous local time is the standard (post-transition, non-daylight) offset - CET,
        // UTC+1 - i.e. the LATER of the two occurrences, not the earlier CEST one.
        var period = Assert.Single(result.OpenPeriods);
        Assert.Equal(new DateTimeOffset(fallBackDate.Year, fallBackDate.Month, fallBackDate.Day, 1, 30, 0, TimeSpan.Zero), period.StartUtc);
    }

    [Fact]
    public async Task Handle_WithRuleSpanningTheSpringForwardTransition_ProducesThePhysicallyCorrectDuration()
    {
        // 01:00-05:00 local nominally spans 4 hours, but the 02:00-03:00 hour never happened that day
        // (clocks jump CET->CEST), so the REAL elapsed time is 3 hours. Each endpoint independently
        // resolves to its own correct offset (01:00 is still CET/+1, 05:00 is already CEST/+2), which
        // inherently produces the physically correct 3-hour UTC span - proving the earlier "duration
        // off by the DST delta" assumption in ConvertLocalToUtc's comment was never actually true.
        var resource = CreateResource();
        resource.TimeZoneId = "Europe/Sarajevo";
        var springForwardDate = LastSundayOfMonth(DateTime.UtcNow.Year + 1, 3);
        var rule = new AvailabilityRule
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
            DayOfWeek = springForwardDate.DayOfWeek, StartTime = new TimeOnly(1, 0), EndTime = new TimeOnly(5, 0),
        };
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _availabilityRuleRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([rule]);
        SetupNoBlackoutsOrBookings(resource);
        var sut = CreateSut();

        var result = await sut.Handle(
            new GetResourceAvailabilityQueryRequest(resource.Id, springForwardDate, springForwardDate), CancellationToken.None);

        var period = Assert.Single(result.OpenPeriods);
        Assert.Equal(new DateTimeOffset(springForwardDate.Year, springForwardDate.Month, springForwardDate.Day, 0, 0, 0, TimeSpan.Zero), period.StartUtc);
        Assert.Equal(new DateTimeOffset(springForwardDate.Year, springForwardDate.Month, springForwardDate.Day, 3, 0, 0, TimeSpan.Zero), period.EndUtc);
        Assert.Equal(TimeSpan.FromHours(3), period.EndUtc - period.StartUtc);
    }

    [Fact]
    public async Task Handle_WithRuleSpanningTheFallBackTransition_ProducesThePhysicallyCorrectDuration()
    {
        // 01:00-04:00 local nominally spans 3 hours, but the 02:00-03:00 hour happens TWICE that day
        // (clocks fall CEST->CET), so the REAL elapsed time is 4 hours. Symmetric proof to the
        // spring-forward test above.
        var resource = CreateResource();
        resource.TimeZoneId = "Europe/Sarajevo";
        var fallBackDate = LastSundayOfMonth(DateTime.UtcNow.Year + 1, 10);
        var rule = new AvailabilityRule
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
            DayOfWeek = fallBackDate.DayOfWeek, StartTime = new TimeOnly(1, 0), EndTime = new TimeOnly(4, 0),
        };
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _availabilityRuleRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([rule]);
        SetupNoBlackoutsOrBookings(resource);
        var sut = CreateSut();

        var result = await sut.Handle(new GetResourceAvailabilityQueryRequest(resource.Id, fallBackDate, fallBackDate), CancellationToken.None);

        var period = Assert.Single(result.OpenPeriods);
        Assert.Equal(new DateTimeOffset(fallBackDate.Year, fallBackDate.Month, fallBackDate.Day - 1, 23, 0, 0, TimeSpan.Zero), period.StartUtc);
        Assert.Equal(new DateTimeOffset(fallBackDate.Year, fallBackDate.Month, fallBackDate.Day, 3, 0, 0, TimeSpan.Zero), period.EndUtc);
        Assert.Equal(TimeSpan.FromHours(4), period.EndUtc - period.StartUtc);
    }

    [Fact]
    public async Task Handle_WithNonDstIntervalInNonUtcTimeZone_ConvertsUsingThatZonesFixedOffset()
    {
        var resource = CreateResource(capacity: 8);
        resource.TimeZoneId = "Europe/Sarajevo";
        // Mid-January: unambiguously CET (UTC+1) all day, nowhere near a transition.
        var winterDate = new DateOnly(DateTime.UtcNow.Year + 1, 1, 15);
        var rule = new AvailabilityRule
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
            DayOfWeek = winterDate.DayOfWeek, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 0),
        };
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _availabilityRuleRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([rule]);
        SetupNoBlackoutsOrBookings(resource);
        var sut = CreateSut();

        var result = await sut.Handle(new GetResourceAvailabilityQueryRequest(resource.Id, winterDate, winterDate), CancellationToken.None);

        var period = Assert.Single(result.OpenPeriods);
        Assert.Equal(new DateTimeOffset(winterDate.Year, winterDate.Month, winterDate.Day, 8, 0, 0, TimeSpan.Zero), period.StartUtc);
        Assert.Equal(new DateTimeOffset(winterDate.Year, winterDate.Month, winterDate.Day, 16, 0, 0, TimeSpan.Zero), period.EndUtc);
    }

    [Fact]
    public async Task Handle_WithRuleActiveEveryDayAcrossAMultiDayRange_ProducesOnePeriodPerDayAtTheCorrectMidnightBoundary()
    {
        var resource = CreateResource();
        var toDate = Date.AddDays(1);
        var ruleForFirstDay = new AvailabilityRule
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
            DayOfWeek = Date.DayOfWeek, StartTime = new TimeOnly(22, 0), EndTime = TimeOnly.MaxValue,
        };
        var ruleForSecondDay = new AvailabilityRule
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
            DayOfWeek = toDate.DayOfWeek, StartTime = TimeOnly.MinValue, EndTime = new TimeOnly(2, 0),
        };
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _availabilityRuleRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([ruleForFirstDay, ruleForSecondDay]);
        SetupNoBlackoutsOrBookings(resource);
        var sut = CreateSut();

        var result = await sut.Handle(new GetResourceAvailabilityQueryRequest(resource.Id, Date, toDate), CancellationToken.None);

        Assert.Equal(2, result.OpenPeriods.Count);
        Assert.Contains(result.OpenPeriods, period => period.StartUtc == At(22));
        Assert.Contains(result.OpenPeriods, period => period.StartUtc == At(0).AddDays(1) && period.EndUtc == At(2).AddDays(1));
    }
}
