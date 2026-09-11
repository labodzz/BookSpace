using BookSpace.Application.Bookings;
using BookSpace.Application.Common;
using BookSpace.Application.Resources;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Bookings;

public sealed class ApproveBookingCommandHandlerTests
{
    private readonly Mock<IBookingRepository> _bookingRepository = new();
    private readonly Mock<IResourceRepository> _resourceRepository = new();
    private readonly Mock<IAvailabilityRuleRepository> _availabilityRuleRepository = new();
    private readonly Mock<IBlackoutPeriodRepository> _blackoutPeriodRepository = new();
    private readonly Mock<IBookingAvailabilityRepository> _bookingAvailabilityRepository = new();
    private readonly Mock<IApprovalRequestRepository> _approvalRequestRepository = new();
    private readonly Mock<IResourceApproverRepository> _resourceApproverRepository = new();
    private readonly Mock<ICurrentUserContext> _currentUserContext = new();

    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ApproverUserId = Guid.NewGuid();
    private static readonly DateOnly Date = new(2026, 9, 7);

    private ApproveBookingCommandHandler CreateSut() => new(
        new PassThroughResourceBookingLock(),
        _bookingRepository.Object,
        _resourceRepository.Object,
        _availabilityRuleRepository.Object,
        _blackoutPeriodRepository.Object,
        _bookingAvailabilityRepository.Object,
        _approvalRequestRepository.Object,
        _resourceApproverRepository.Object,
        _currentUserContext.Object);

    private static DateTimeOffset At(int hour) => new(Date.Year, Date.Month, Date.Day, hour, 0, 0, TimeSpan.Zero);

    private static Resource CreateResource(int capacity = 4) => new()
    {
        Id = Guid.NewGuid(), TenantId = TenantId, ResourceTypeId = Guid.NewGuid(), Name = "Room",
        Capacity = capacity, Status = ResourceStatus.Active, TimeZoneId = "UTC", RequiresApproval = true,
    };

    private static Booking CreatePendingBooking(Resource resource, int quantity = 1) => new()
    {
        Id = Guid.NewGuid(), TenantId = TenantId, ResourceId = resource.Id, UserId = Guid.NewGuid(),
        StartUtc = At(10), EndUtc = At(11), Quantity = quantity, Status = BookingStatus.Pending, CreatedAtUtc = At(0),
    };

    private void SetupEligible(Resource resource, Booking booking)
    {
        _currentUserContext.SetupGet(c => c.UserId).Returns(ApproverUserId);
        _currentUserContext.SetupGet(c => c.Roles).Returns([]);
        _bookingRepository.Setup(r => r.FindResourceIdAsync(booking.Id, It.IsAny<CancellationToken>())).ReturnsAsync(booking.ResourceId);
        _bookingRepository.Setup(r => r.FindByIdAsync(booking.Id, It.IsAny<CancellationToken>())).ReturnsAsync(booking);
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _resourceApproverRepository
            .Setup(r => r.FindByResourceAndUserAsync(resource.Id, ApproverUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceApprover { Id = Guid.NewGuid(), TenantId = TenantId, ResourceId = resource.Id, UserId = ApproverUserId });
        _availabilityRuleRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AvailabilityRule
            {
                Id = Guid.NewGuid(), TenantId = TenantId, ResourceId = resource.Id,
                DayOfWeek = Date.DayOfWeek, StartTime = TimeOnly.MinValue, EndTime = TimeOnly.MaxValue,
            }]);
        _blackoutPeriodRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _bookingAvailabilityRepository
            .Setup(r => r.GetActiveBookingsAsync(resource.Id, It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([booking]); // the booking itself is "active" (Pending) and must be excluded from its own demand sum
        _approvalRequestRepository
            .Setup(r => r.FindByBookingIdAsync(booking.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApprovalRequest
            {
                Id = Guid.NewGuid(), TenantId = TenantId, BookingId = booking.Id, ApproverId = null,
                Status = ApprovalStatus.Pending, RequestedAtUtc = At(0), ExpiresAtUtc = At(0).AddHours(48),
            });
    }

    [Fact]
    public async Task Handle_WithOwnResourceApproverAndStillEligible_ApprovesConfirmsBookingAndSaves()
    {
        var resource = CreateResource();
        var booking = CreatePendingBooking(resource);
        SetupEligible(resource, booking);
        var sut = CreateSut();

        var result = await sut.Handle(new ApproveBookingCommandRequest(booking.Id, "Looks good"), CancellationToken.None);

        Assert.Equal(BookingStatus.Confirmed, result.Status);
        Assert.Equal(BookingStatus.Confirmed, booking.Status);
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_AsTenantAdmin_BypassesThePerResourceApproverCheck()
    {
        var resource = CreateResource();
        var booking = CreatePendingBooking(resource);
        SetupEligible(resource, booking);
        _currentUserContext.SetupGet(c => c.Roles).Returns(["TenantAdmin"]);
        // Deliberately NOT a ResourceApprover for this resource - TenantAdmin must not need to be.
        _resourceApproverRepository
            .Setup(r => r.FindByResourceAndUserAsync(resource.Id, ApproverUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResourceApprover?)null);
        var sut = CreateSut();

        var result = await sut.Handle(new ApproveBookingCommandRequest(booking.Id, null), CancellationToken.None);

        Assert.Equal(BookingStatus.Confirmed, result.Status);
    }

    [Fact]
    public async Task Handle_AsSysAdmin_BypassesThePerResourceApproverCheck()
    {
        var resource = CreateResource();
        var booking = CreatePendingBooking(resource);
        SetupEligible(resource, booking);
        _currentUserContext.SetupGet(c => c.Roles).Returns(["SysAdmin"]);
        // Deliberately NOT a ResourceApprover for this resource - SysAdmin must not need to be, exactly
        // like TenantAdmin above.
        _resourceApproverRepository
            .Setup(r => r.FindByResourceAndUserAsync(resource.Id, ApproverUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResourceApprover?)null);
        var sut = CreateSut();

        var result = await sut.Handle(new ApproveBookingCommandRequest(booking.Id, null), CancellationToken.None);

        Assert.Equal(BookingStatus.Confirmed, result.Status);
    }

    [Fact]
    public async Task Handle_WhenCallerIsNotAResourceApproverForThisResource_ThrowsConflictExceptionWithApprovalForbiddenCode()
    {
        var resource = CreateResource();
        var booking = CreatePendingBooking(resource);
        SetupEligible(resource, booking);
        _resourceApproverRepository
            .Setup(r => r.FindByResourceAndUserAsync(resource.Id, ApproverUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResourceApprover?)null);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new ApproveBookingCommandRequest(booking.Id, null), CancellationToken.None));

        Assert.Equal(ErrorCodes.BookingApprovalForbidden, exception.ErrorCode);
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WithUnknownBooking_ThrowsNotFoundException()
    {
        _currentUserContext.SetupGet(c => c.UserId).Returns(ApproverUserId);
        _bookingRepository.Setup(r => r.FindResourceIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid?)null);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new ApproveBookingCommandRequest(Guid.NewGuid(), null), CancellationToken.None));

        Assert.Equal(ErrorCodes.BookingNotFound, exception.ErrorCode);
    }

    [Theory]
    [InlineData(BookingStatus.Confirmed)]
    [InlineData(BookingStatus.Rejected)]
    [InlineData(BookingStatus.Cancelled)]
    public async Task Handle_WithBookingNotPending_ThrowsConflictExceptionWithApprovalNotAllowedCode(BookingStatus status)
    {
        var resource = CreateResource();
        var booking = CreatePendingBooking(resource);
        booking.Status = status;
        SetupEligible(resource, booking);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new ApproveBookingCommandRequest(booking.Id, null), CancellationToken.None));

        Assert.Equal(ErrorCodes.BookingApprovalNotAllowed, exception.ErrorCode);
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenABlackoutWasAddedAfterCreationMakingTheSlotNoLongerAvailable_ThrowsConflictExceptionAndLeavesBookingPending()
    {
        // The scenario the re-check exists for: eligible at creation time, but something changed before
        // approval (here, a blackout was added covering the booking's window).
        var resource = CreateResource();
        var booking = CreatePendingBooking(resource);
        SetupEligible(resource, booking);
        _blackoutPeriodRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new BlackoutPeriod
            {
                Id = Guid.NewGuid(), TenantId = TenantId, ResourceId = resource.Id,
                StartUtc = booking.StartUtc, EndUtc = booking.EndUtc, Reason = "Emergency closure",
            }]);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new ApproveBookingCommandRequest(booking.Id, null), CancellationToken.None));

        Assert.Equal(ErrorCodes.BookingBlackoutConflict, exception.ErrorCode);
        Assert.Equal(BookingStatus.Pending, booking.Status); // never partially approved
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenAnotherBookingHasTakenTheRemainingCapacity_ThrowsConflictExceptionAndLeavesBookingPending()
    {
        var resource = CreateResource(capacity: 1);
        var booking = CreatePendingBooking(resource, quantity: 1);
        SetupEligible(resource, booking);
        var anotherConfirmedBooking = new Booking
        {
            Id = Guid.NewGuid(), TenantId = TenantId, ResourceId = resource.Id, UserId = Guid.NewGuid(),
            StartUtc = booking.StartUtc, EndUtc = booking.EndUtc, Quantity = 1, Status = BookingStatus.Confirmed, CreatedAtUtc = At(0),
        };
        _bookingAvailabilityRepository
            .Setup(r => r.GetActiveBookingsAsync(resource.Id, It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([booking, anotherConfirmedBooking]);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new ApproveBookingCommandRequest(booking.Id, null), CancellationToken.None));

        Assert.Equal(ErrorCodes.BookingCapacityExceeded, exception.ErrorCode);
        Assert.Equal(BookingStatus.Pending, booking.Status);
    }

    // The other genuinely-external re-check trigger §5 of docs/recurring-bookings-and-approvals.md names
    // alongside "a blackout was added" - the resource's status itself changing to non-Active between
    // creation and approval. Only blackout and capacity were previously exercised as re-check failures.
    [Fact]
    public async Task Handle_WhenResourceStatusChangedToInactiveAfterCreation_ThrowsConflictExceptionAndLeavesBookingPending()
    {
        var resource = CreateResource();
        var booking = CreatePendingBooking(resource);
        SetupEligible(resource, booking);
        resource.Status = ResourceStatus.Inactive; // changed after the booking was created, before approval

        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new ApproveBookingCommandRequest(booking.Id, null), CancellationToken.None));

        Assert.Equal(ErrorCodes.BookingResourceUnavailable, exception.ErrorCode);
        Assert.Equal(BookingStatus.Pending, booking.Status);
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // The defensive resourceRepository.FindByIdAsync null-check inside ApproveUnderLockAsync - unreachable
    // in ordinary operation (the resource existed moments ago, when the booking was resolved), but still
    // an explicit throw with its own ErrorCode that had no test.
    [Fact]
    public async Task Handle_WhenResourceNoLongerExistsAtApprovalTime_ThrowsNotFoundExceptionWithResourceNotFoundCode()
    {
        var resource = CreateResource();
        var booking = CreatePendingBooking(resource);
        SetupEligible(resource, booking);
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync((Resource?)null);

        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new ApproveBookingCommandRequest(booking.Id, null), CancellationToken.None));

        Assert.Equal(ErrorCodes.ResourceNotFound, exception.ErrorCode);
    }
}
