using BookSpace.Application.Bookings;
using BookSpace.Application.Common;
using BookSpace.Application.Notifications;
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
    private readonly Mock<IResourceApproverRepository> _resourceApproverRepository = new();
    private readonly Mock<IApprovalRequestRepository> _approvalRequestRepository = new();
    private readonly Mock<ITenantRepository> _tenantRepository = new();
    private readonly Mock<ICurrentUserContext> _currentUserContext = new();
    private readonly Mock<INotificationOutboxWriter> _notificationOutboxWriter = new();

    private static readonly DateOnly Date = new(2026, 9, 7);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    private CreateBookingCommandHandler CreateSut()
    {
        _notificationOutboxWriter
            .Setup(w => w.EnqueueAsync(It.IsAny<NotificationOutboxRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnqueueNotificationResult(EnqueueOutcome.Created, Guid.NewGuid()));
        return new CreateBookingCommandHandler(
            // The lock is a pass-through here (no real DB/transaction in a handler test) - concurrency
            // itself is proven separately against real SQL Server in BookSpace.Infrastructure.Tests.
            new PassThroughResourceBookingLock(),
            _resourceRepository.Object,
            _availabilityRuleRepository.Object,
            _blackoutPeriodRepository.Object,
            _bookingAvailabilityRepository.Object,
            _bookingRepository.Object,
            _resourceApproverRepository.Object,
            _approvalRequestRepository.Object,
            _tenantRepository.Object,
            _currentUserContext.Object,
            _notificationOutboxWriter.Object);
    }

    private static DateTimeOffset At(int hour, int minute = 0) => new(Date.Year, Date.Month, Date.Day, hour, minute, 0, TimeSpan.Zero);

    private static Resource CreateResource(int capacity = 8, bool requiresApproval = false) => new()
    {
        Id = Guid.NewGuid(), TenantId = TenantId, ResourceTypeId = Guid.NewGuid(), Name = "Laptop Cart",
        Capacity = capacity, Status = ResourceStatus.Active, TimeZoneId = "UTC", RequiresApproval = requiresApproval,
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
        // A booking created directly as Confirmed (no approval required) enqueues exactly one confirmation
        // notification, to the booking's own owner. EnqueueAsync's own SaveChangesAsync is the only commit
        // point - no separate bookingRepository.SaveChangesAsync call on this branch.
        _notificationOutboxWriter.Verify(w => w.EnqueueAsync(
            It.Is<NotificationOutboxRequest>(req =>
                req.TenantId == TenantId && req.NotificationType == BookingNotificationTypes.Confirmation &&
                req.RecipientUserId == UserId && req.IdempotencyKey == $"booking:{result.Id}:confirmation"),
            It.IsAny<CancellationToken>()), Times.Once);
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenTheNotificationEnqueueFails_DoesNotPersistTheBookingEitherSinceNoSeparateSaveExists()
    {
        // Proves atomicity the only way a mock-based handler test can: for a Confirmed booking, the
        // handler has no fallback SaveChangesAsync call that could persist it independently of the
        // enqueue - if EnqueueAsync throws, nothing in this handler commits anything.
        var resource = CreateResource();
        SetupResource(resource, OpenAllDay(resource));
        var sut = CreateSut();
        _notificationOutboxWriter
            .Setup(w => w.EnqueueAsync(It.IsAny<NotificationOutboxRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Simulated unexpected database failure."));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.Handle(new CreateBookingCommandRequest(resource.Id, At(10), At(11), Quantity: 1), CancellationToken.None));

        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenTheNotificationWasAlreadyEnqueuedByAnEarlierAttempt_StillCreatesTheBookingSuccessfully()
    {
        // EnqueueOutcome.AlreadyExists is explicitly not an error (see INotificationOutboxWriter) - a
        // retried create request must not surface it as one.
        var resource = CreateResource();
        SetupResource(resource, OpenAllDay(resource));
        _notificationOutboxWriter
            .Setup(w => w.EnqueueAsync(It.IsAny<NotificationOutboxRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnqueueNotificationResult(EnqueueOutcome.AlreadyExists, null));
        var sut = CreateSut();

        var result = await sut.Handle(new CreateBookingCommandRequest(resource.Id, At(10), At(11), Quantity: 1), CancellationToken.None);

        Assert.Equal(BookingStatus.Confirmed, result.Status);
    }

    [Fact]
    public async Task Handle_WithAvailableSlot_IncludesTheResourcesOwnTimeZoneIdInTheResponse()
    {
        var resource = CreateResource();
        resource.TimeZoneId = "Europe/Sarajevo";
        SetupResource(resource, OpenAllDay(resource));
        var sut = CreateSut();

        var result = await sut.Handle(new CreateBookingCommandRequest(resource.Id, At(10), At(11), Quantity: 1), CancellationToken.None);

        Assert.Equal("Europe/Sarajevo", result.TimeZoneId);
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
        // Deliberately returns [existing], not [] - a real repository call for this exact window could
        // legitimately still include a booking that merely touches the boundary (the repository's own
        // range filter is proven separately in BookingAvailabilityRepositoryTests). Returning it here
        // means this test actually exercises the Application-layer clip logic in IntervalMath -
        // existing.End(10) clips to window.Start(10), producing a zero-length occupancy that gets
        // dropped - rather than just asserting the same outcome the mock was told to produce anyway.
        _bookingAvailabilityRepository
            .Setup(r => r.GetActiveBookingsAsync(resource.Id, At(10), At(11), It.IsAny<CancellationToken>()))
            .ReturnsAsync([existing]);
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

    [Fact]
    public async Task Handle_ForResourceRequiringApproval_CreatesPendingBookingWithApprovalRequest()
    {
        var resource = CreateResource(requiresApproval: true);
        SetupResource(resource, OpenAllDay(resource));
        _resourceApproverRepository
            .Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ResourceApprover { Id = Guid.NewGuid(), TenantId = TenantId, ResourceId = resource.Id, UserId = Guid.NewGuid() }]);
        _tenantRepository
            .Setup(r => r.FindByIdAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Tenant { Id = TenantId, Name = "Tenant", DefaultTimeZoneId = "UTC", ApprovalExpiryHours = 48, CreatedAtUtc = At(0) });
        var sut = CreateSut();

        var result = await sut.Handle(new CreateBookingCommandRequest(resource.Id, At(10), At(11)), CancellationToken.None);

        Assert.Equal(BookingStatus.Pending, result.Status);
        _approvalRequestRepository.Verify(r => r.AddAsync(
            It.Is<ApprovalRequest>(a => a.BookingId == result.Id && a.Status == ApprovalStatus.Pending && a.ApproverId == null),
            It.IsAny<CancellationToken>()), Times.Once);
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        // A booking that still needs approval is NOT Confirmed by this call - no confirmation notification
        // until a later ApproveBookingCommandHandler call actually confirms it.
        _notificationOutboxWriter.Verify(w => w.EnqueueAsync(It.IsAny<NotificationOutboxRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // The anomalous-state guard for RequiresApproval - if the current tenant can't be resolved (it always
    // should be, in ordinary operation) this throws rather than silently proceeding without an expiry.
    [Fact]
    public async Task Handle_ForResourceRequiringApprovalWhenTenantLookupFails_ThrowsInvalidOperationException()
    {
        var resource = CreateResource(requiresApproval: true);
        SetupResource(resource, OpenAllDay(resource));
        _resourceApproverRepository
            .Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ResourceApprover { Id = Guid.NewGuid(), TenantId = TenantId, ResourceId = resource.Id, UserId = Guid.NewGuid() }]);
        _tenantRepository.Setup(r => r.FindByIdAsync(TenantId, It.IsAny<CancellationToken>())).ReturnsAsync((Tenant?)null);
        var sut = CreateSut();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.Handle(new CreateBookingCommandRequest(resource.Id, At(10), At(11)), CancellationToken.None));
    }

    // The existing exact-boundary test only proves "remaining=0, request=1"; this proves the sibling case
    // where the remainder is nonzero but the request still exceeds it by exactly 1.
    [Fact]
    public async Task Handle_WhenRequestedQuantityExceedsANonzeroRemainingCapacityByOne_ThrowsConflictExceptionWithCapacityExceededCode()
    {
        var resource = CreateResource(capacity: 3);
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
            sut.Handle(new CreateBookingCommandRequest(resource.Id, At(10), At(11), Quantity: 2), CancellationToken.None));

        Assert.Equal(ErrorCodes.BookingCapacityExceeded, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_ForResourceRequiringApprovalWithNoConfiguredApprover_ThrowsConflictExceptionWithNoApproverConfiguredCode()
    {
        var resource = CreateResource(requiresApproval: true);
        SetupResource(resource, OpenAllDay(resource));
        _resourceApproverRepository
            .Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ResourceApprover>)[]);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new CreateBookingCommandRequest(resource.Id, At(10), At(11)), CancellationToken.None));

        Assert.Equal(ErrorCodes.BookingNoApproverConfigured, exception.ErrorCode);
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // Every other test in this file uses PassThroughResourceBookingLock, which ignores its resourceId
    // argument entirely - a bug that passed the wrong id (e.g. Guid.Empty, or a hardcoded value) would go
    // undetected by any of them. This is the one test in the suite that actually asserts the lock is
    // acquired keyed by the request's own ResourceId.
    [Fact]
    public async Task Handle_AcquiresTheResourceBookingLockKeyedByTheRequestsOwnResourceId()
    {
        var resource = CreateResource();
        SetupResource(resource, OpenAllDay(resource));
        var lockMock = new Mock<IResourceBookingLock>();
        lockMock
            .Setup(l => l.RunExclusiveAsync(
                It.IsAny<Guid>(), It.IsAny<Func<CancellationToken, Task<CreateBookingResponse>>>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, Func<CancellationToken, Task<CreateBookingResponse>>, CancellationToken>((_, operation, ct) => operation(ct));
        _notificationOutboxWriter
            .Setup(w => w.EnqueueAsync(It.IsAny<NotificationOutboxRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnqueueNotificationResult(EnqueueOutcome.Created, Guid.NewGuid()));
        var sut = new CreateBookingCommandHandler(
            lockMock.Object, _resourceRepository.Object, _availabilityRuleRepository.Object, _blackoutPeriodRepository.Object,
            _bookingAvailabilityRepository.Object, _bookingRepository.Object, _resourceApproverRepository.Object,
            _approvalRequestRepository.Object, _tenantRepository.Object, _currentUserContext.Object, _notificationOutboxWriter.Object);

        await sut.Handle(new CreateBookingCommandRequest(resource.Id, At(9), At(10)), CancellationToken.None);

        lockMock.Verify(l => l.RunExclusiveAsync(
            resource.Id, It.IsAny<Func<CancellationToken, Task<CreateBookingResponse>>>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
