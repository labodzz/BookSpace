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
}
