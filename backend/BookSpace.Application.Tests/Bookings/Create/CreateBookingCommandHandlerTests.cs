using BookSpace.Application.Bookings;
using BookSpace.Application.Common;
using BookSpace.Application.Resources;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Bookings;

public sealed class CreateBookingCommandHandlerTests
{
    private readonly Mock<IResourceRepository> _resourceRepository = new();
    private readonly Mock<IAvailabilityRuleRepository> _availabilityRuleRepository = new();
    private readonly Mock<IBlackoutPeriodRepository> _blackoutPeriodRepository = new();
    private readonly Mock<IBookingAvailabilityRepository> _bookingAvailabilityRepository = new();
    private readonly Mock<IBookingRepository> _bookingRepository = new();
    private readonly Mock<ICurrentUserContext> _currentUserContext = new();

    private static readonly DateOnly Date = new(2026, 9, 7);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    private CreateBookingCommandHandler CreateSut() => new(
        // The lock is a pass-through here (no real DB/transaction in a handler test) - concurrency
        // itself is proven separately against real SQL Server in BookSpace.Infrastructure.Tests.
        new PassThroughResourceBookingLock(),
        _resourceRepository.Object,
        _availabilityRuleRepository.Object,
        _blackoutPeriodRepository.Object,
        _bookingAvailabilityRepository.Object,
        _bookingRepository.Object,
        _currentUserContext.Object);

    private static DateTimeOffset At(int hour, int minute = 0) => new(Date.Year, Date.Month, Date.Day, hour, minute, 0, TimeSpan.Zero);

    private static Resource CreateResource(int capacity = 8) => new()
    {
        Id = Guid.NewGuid(), TenantId = TenantId, ResourceTypeId = Guid.NewGuid(), Name = "Laptop Cart",
        Capacity = capacity, Status = ResourceStatus.Active, TimeZoneId = "UTC",
    };

    private static AvailabilityRule OpenAllDay(Resource resource) => new()
    {
        Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
        DayOfWeek = Date.DayOfWeek, StartTime = TimeOnly.MinValue, EndTime = TimeOnly.MaxValue,
    };

    private void SetupResource(Resource resource, params AvailabilityRule[] rules)
    {
        _currentUserContext.SetupGet(c => c.TenantId).Returns(TenantId);
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _availabilityRuleRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(rules);
        _blackoutPeriodRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _bookingAvailabilityRepository
            .Setup(r => r.GetActiveBookingsAsync(resource.Id, It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    [Fact]
    public async Task Handle_WithAvailableSlot_CreatesConfirmedBookingAndSaves()
    {
        var resource = CreateResource();
        SetupResource(resource, OpenAllDay(resource));
        var sut = CreateSut();

        var result = await sut.Handle(new CreateBookingCommandRequest(resource.Id, At(10), At(11), Quantity: 1), CancellationToken.None);

        Assert.Equal(resource.Id, result.ResourceId);
        Assert.Equal(At(10), result.StartUtc);
        Assert.Equal(At(11), result.EndUtc);
        Assert.Equal(BookingStatus.Confirmed, result.Status);
        _bookingRepository.Verify(r => r.AddAsync(
            It.Is<Booking>(b => b.TenantId == TenantId && b.UserId == UserId && b.ResourceId == resource.Id && b.Status == BookingStatus.Confirmed),
            It.IsAny<CancellationToken>()), Times.Once);
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithUnknownResource_ThrowsNotFoundException()
    {
        _currentUserContext.SetupGet(c => c.TenantId).Returns(TenantId);
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _resourceRepository.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Resource?)null);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new CreateBookingCommandRequest(Guid.NewGuid(), At(10), At(11)), CancellationToken.None));

        Assert.Equal(ErrorCodes.ResourceNotFound, exception.ErrorCode);
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(ResourceStatus.Archived)]
    [InlineData(ResourceStatus.Inactive)]
    [InlineData(ResourceStatus.Maintenance)]
    public async Task Handle_WithNonActiveResource_ThrowsConflictExceptionWithResourceUnavailableCode(ResourceStatus status)
    {
        var resource = CreateResource();
        resource.Status = status;
        SetupResource(resource, OpenAllDay(resource));
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new CreateBookingCommandRequest(resource.Id, At(10), At(11)), CancellationToken.None));

        Assert.Equal(ErrorCodes.BookingResourceUnavailable, exception.ErrorCode);
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WithNoAvailabilityRuleCoveringTheWindow_ThrowsConflictExceptionWithOutsideAvailabilityCode()
    {
        var resource = CreateResource();
        SetupResource(resource); // no rules at all
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new CreateBookingCommandRequest(resource.Id, At(10), At(11)), CancellationToken.None));

        Assert.Equal(ErrorCodes.BookingOutsideAvailability, exception.ErrorCode);
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WithRequestPartiallyOutsideTheOpenPeriod_ThrowsConflictExceptionWithOutsideAvailabilityCode()
    {
        var resource = CreateResource();
        var rule = new AvailabilityRule
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
            DayOfWeek = Date.DayOfWeek, StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(10, 0),
        };
        SetupResource(resource, rule);
        var sut = CreateSut();

        // Requested 09:00-11:00 but the resource is only open until 10:00.
        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new CreateBookingCommandRequest(resource.Id, At(9), At(11)), CancellationToken.None));

        Assert.Equal(ErrorCodes.BookingOutsideAvailability, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WithBlackoutOverlappingTheWindow_ThrowsConflictExceptionWithBlackoutConflictCode()
    {
        var resource = CreateResource();
        SetupResource(resource, OpenAllDay(resource));
        var blackout = new BlackoutPeriod
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
            StartUtc = At(10, 30), EndUtc = At(11, 30), Reason = "Maintenance",
        };
        _blackoutPeriodRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([blackout]);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new CreateBookingCommandRequest(resource.Id, At(10), At(11)), CancellationToken.None));

        Assert.Equal(ErrorCodes.BookingBlackoutConflict, exception.ErrorCode);
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WithBlackoutEndingExactlyAtBookingStart_DoesNotOverlapAndSucceeds()
    {
        var resource = CreateResource();
        SetupResource(resource, OpenAllDay(resource));
        var blackout = new BlackoutPeriod
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
            StartUtc = At(9), EndUtc = At(10), Reason = "Maintenance",
        };
        _blackoutPeriodRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([blackout]);
        var sut = CreateSut();

        var result = await sut.Handle(new CreateBookingCommandRequest(resource.Id, At(10), At(11)), CancellationToken.None);

        Assert.Equal(BookingStatus.Confirmed, result.Status);
    }

    [Fact]
    public async Task Handle_WithBookingEndingExactlyWhenBlackoutStarts_DoesNotOverlapAndSucceeds()
    {
        var resource = CreateResource();
        SetupResource(resource, OpenAllDay(resource));
        var blackout = new BlackoutPeriod
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
            StartUtc = At(11), EndUtc = At(12), Reason = "Maintenance",
        };
        _blackoutPeriodRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([blackout]);
        var sut = CreateSut();

        var result = await sut.Handle(new CreateBookingCommandRequest(resource.Id, At(10), At(11)), CancellationToken.None);

        Assert.Equal(BookingStatus.Confirmed, result.Status);
    }

    [Fact]
    public async Task Handle_WithExistingBookingEndingExactlyWhenNewOneStarts_DoesNotOverlapAndSucceeds()
    {
        var resource = CreateResource(capacity: 1);
        SetupResource(resource, OpenAllDay(resource));
        var existing = new Booking
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id, UserId = Guid.NewGuid(),
            StartUtc = At(9), EndUtc = At(10), Quantity = 1, Status = BookingStatus.Confirmed, CreatedAtUtc = At(0),
        };
        _bookingAvailabilityRepository
            .Setup(r => r.GetActiveBookingsAsync(resource.Id, At(10), At(11), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]); // the repository itself excludes non-overlapping bookings; existing ends exactly at the new start
        var sut = CreateSut();

        var result = await sut.Handle(new CreateBookingCommandRequest(resource.Id, At(10), At(11)), CancellationToken.None);

        Assert.Equal(BookingStatus.Confirmed, result.Status);
    }

    [Fact]
    public async Task Handle_WhenRequestedQuantityWouldExceedRemainingCapacity_ThrowsConflictExceptionWithCapacityExceededCode()
    {
        var resource = CreateResource(capacity: 2);
        SetupResource(resource, OpenAllDay(resource));
        var existing = new Booking
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id, UserId = Guid.NewGuid(),
            StartUtc = At(10), EndUtc = At(11), Quantity = 2, Status = BookingStatus.Confirmed, CreatedAtUtc = At(0),
        };
        _bookingAvailabilityRepository
            .Setup(r => r.GetActiveBookingsAsync(resource.Id, At(10), At(11), It.IsAny<CancellationToken>()))
            .ReturnsAsync([existing]);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new CreateBookingCommandRequest(resource.Id, At(10), At(11), Quantity: 1), CancellationToken.None));

        Assert.Equal(ErrorCodes.BookingCapacityExceeded, exception.ErrorCode);
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenCapacityLeavesExactlyEnoughRoom_Succeeds()
    {
        // Capacity=2, existing booking uses 1 unit, requesting the remaining 1 - must be accepted, not
        // rejected for merely being "at" capacity.
        var resource = CreateResource(capacity: 2);
        SetupResource(resource, OpenAllDay(resource));
        var existing = new Booking
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id, UserId = Guid.NewGuid(),
            StartUtc = At(10), EndUtc = At(11), Quantity = 1, Status = BookingStatus.Confirmed, CreatedAtUtc = At(0),
        };
        _bookingAvailabilityRepository
            .Setup(r => r.GetActiveBookingsAsync(resource.Id, At(10), At(11), It.IsAny<CancellationToken>()))
            .ReturnsAsync([existing]);
        var sut = CreateSut();

        var result = await sut.Handle(new CreateBookingCommandRequest(resource.Id, At(10), At(11), Quantity: 1), CancellationToken.None);

        Assert.Equal(BookingStatus.Confirmed, result.Status);
    }
}
