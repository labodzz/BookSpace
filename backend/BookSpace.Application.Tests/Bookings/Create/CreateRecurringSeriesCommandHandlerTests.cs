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
        var sut = CreateSut();

        var result = await sut.Handle(CreateRequest(resource.Id, occurrenceCount: 3), CancellationToken.None);

        Assert.Equal(3, result.CreatedOccurrences.Count);
        Assert.All(result.CreatedOccurrences, occurrence => Assert.Equal(BookingStatus.Pending, occurrence.Status));
        _approvalRequestRepository.Verify(r => r.AddAsync(It.IsAny<ApprovalRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
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
