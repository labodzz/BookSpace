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
    private readonly Mock<INotificationOutboxWriter> _notificationOutboxWriter = new();

    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ApproverUserId = Guid.NewGuid();
    private static readonly DateOnly Date = new(2026, 9, 7);

    private ApproveBookingCommandHandler CreateSut()
    {
        _notificationOutboxWriter
            .Setup(w => w.EnqueueAsync(It.IsAny<NotificationOutboxRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnqueueNotificationResult(EnqueueOutcome.Created, Guid.NewGuid()));
        return new ApproveBookingCommandHandler(
            new PassThroughResourceBookingLock(),
            _bookingRepository.Object,
            _resourceRepository.Object,
            _availabilityRuleRepository.Object,
            _blackoutPeriodRepository.Object,
            _bookingAvailabilityRepository.Object,
            _approvalRequestRepository.Object,
            _resourceApproverRepository.Object,
            _currentUserContext.Object,
            _notificationOutboxWriter.Object);
    }

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
        // Confirmation goes to the booking OWNER, not the approver (ApproverUserId != booking.UserId here).
        // EnqueueAsync's own SaveChangesAsync is the sole commit point.
        _notificationOutboxWriter.Verify(w => w.EnqueueAsync(
            It.Is<NotificationOutboxRequest>(req =>
                req.TenantId == TenantId && req.NotificationType == BookingNotificationTypes.Confirmation &&
                req.RecipientUserId == booking.UserId && req.IdempotencyKey == $"booking:{booking.Id}:confirmation"),
            It.IsAny<CancellationToken>()), Times.Once);
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenTheNotificationEnqueueFails_DoesNotPersistTheApprovalEitherSinceNoSeparateSaveExists()
    {
        var resource = CreateResource();
        var booking = CreatePendingBooking(resource);
        SetupEligible(resource, booking);
        var sut = CreateSut();
        _notificationOutboxWriter
            .Setup(w => w.EnqueueAsync(It.IsAny<NotificationOutboxRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Simulated unexpected database failure."));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.Handle(new ApproveBookingCommandRequest(booking.Id, "Looks good"), CancellationToken.None));

        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenTheNotificationWasAlreadyEnqueuedByAnEarlierAttempt_StillApprovesSuccessfully()
    {
        var resource = CreateResource();
        var booking = CreatePendingBooking(resource);
        SetupEligible(resource, booking);
        _notificationOutboxWriter
            .Setup(w => w.EnqueueAsync(It.IsAny<NotificationOutboxRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnqueueNotificationResult(EnqueueOutcome.AlreadyExists, null));
        var sut = CreateSut();

        var result = await sut.Handle(new ApproveBookingCommandRequest(booking.Id, "Looks good"), CancellationToken.None);

        Assert.Equal(BookingStatus.Confirmed, result.Status);
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
        _notificationOutboxWriter.Verify(w => w.EnqueueAsync(It.IsAny<NotificationOutboxRequest>(), It.IsAny<CancellationToken>()), Times.Never);
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
        _notificationOutboxWriter.Verify(w => w.EnqueueAsync(It.IsAny<NotificationOutboxRequest>(), It.IsAny<CancellationToken>()), Times.Never);
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
        _notificationOutboxWriter.Verify(w => w.EnqueueAsync(It.IsAny<NotificationOutboxRequest>(), It.IsAny<CancellationToken>()), Times.Never);
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
        _notificationOutboxWriter.Verify(w => w.EnqueueAsync(It.IsAny<NotificationOutboxRequest>(), It.IsAny<CancellationToken>()), Times.Never);
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
        _notificationOutboxWriter.Verify(w => w.EnqueueAsync(It.IsAny<NotificationOutboxRequest>(), It.IsAny<CancellationToken>()), Times.Never);
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

    private static Booking CreateSeriesOccurrence(Resource resource, Guid seriesId, Guid userId, int startHourOffsetDays, BookingStatus status) => new()
    {
        Id = Guid.NewGuid(), TenantId = TenantId, ResourceId = resource.Id, UserId = userId, SeriesId = seriesId,
        StartUtc = At(10).AddDays(startHourOffsetDays), EndUtc = At(11).AddDays(startHourOffsetDays), Quantity = 1,
        Status = status, CreatedAtUtc = At(0),
    };

    private ApprovalRequest SetupApprovalRequestFor(Booking occurrence) => new()
    {
        Id = Guid.NewGuid(), TenantId = TenantId, BookingId = occurrence.Id, ApproverId = null,
        Status = ApprovalStatus.Pending, RequestedAtUtc = At(0), ExpiresAtUtc = At(0).AddHours(48),
    };

    [Fact]
    public async Task Handle_WithApproveRemainingSeries_ApprovesEveryOtherPendingOccurrenceInTheSameSeries()
    {
        var resource = CreateResource();
        var seriesId = Guid.NewGuid();
        var booking = CreatePendingBooking(resource);
        booking.SeriesId = seriesId;
        SetupEligible(resource, booking);

        var sibling1 = CreateSeriesOccurrence(resource, seriesId, booking.UserId, startHourOffsetDays: 7, BookingStatus.Pending);
        var sibling2 = CreateSeriesOccurrence(resource, seriesId, booking.UserId, startHourOffsetDays: 14, BookingStatus.Pending);
        var sibling1Approval = SetupApprovalRequestFor(sibling1);
        var sibling2Approval = SetupApprovalRequestFor(sibling2);

        _bookingRepository.Setup(r => r.GetBySeriesIdAsync(seriesId, It.IsAny<CancellationToken>())).ReturnsAsync([booking, sibling1, sibling2]);
        _approvalRequestRepository
            .Setup(r => r.GetByBookingIdsAsync(It.Is<IReadOnlyList<Guid>>(ids => ids.Contains(sibling1.Id) && ids.Contains(sibling2.Id)), It.IsAny<CancellationToken>()))
            .ReturnsAsync([sibling1Approval, sibling2Approval]);
        // Each sibling's own eligibility re-check excludes only itself from active demand, same as the primary booking.
        _bookingAvailabilityRepository
            .Setup(r => r.GetActiveBookingsAsync(resource.Id, sibling1.StartUtc, sibling1.EndUtc, It.IsAny<CancellationToken>()))
            .ReturnsAsync([sibling1]);
        _bookingAvailabilityRepository
            .Setup(r => r.GetActiveBookingsAsync(resource.Id, sibling2.StartUtc, sibling2.EndUtc, It.IsAny<CancellationToken>()))
            .ReturnsAsync([sibling2]);

        var sut = CreateSut();

        var result = await sut.Handle(new ApproveBookingCommandRequest(booking.Id, "Approved for the whole series", ApproveRemainingSeries: true), CancellationToken.None);

        Assert.Equal(BookingStatus.Confirmed, booking.Status);
        Assert.Equal(BookingStatus.Confirmed, sibling1.Status);
        Assert.Equal(BookingStatus.Confirmed, sibling2.Status);
        Assert.Equal(ApprovalStatus.Approved, sibling1Approval.Status);
        Assert.Equal(ApprovalStatus.Approved, sibling2Approval.Status);
        Assert.Equal([sibling1.Id, sibling2.Id], result.CascadedApprovedOccurrenceIds);
        Assert.Empty(result.CascadedConflicts);
        // One confirmation per actually-approved occurrence (primary + both siblings) - all three go to
        // their own owner (booking.UserId for every occurrence in this series, per CreateSeriesOccurrence).
        _notificationOutboxWriter.Verify(w => w.EnqueueAsync(
            It.Is<NotificationOutboxRequest>(req => req.IdempotencyKey == $"booking:{booking.Id}:confirmation"), It.IsAny<CancellationToken>()), Times.Once);
        _notificationOutboxWriter.Verify(w => w.EnqueueAsync(
            It.Is<NotificationOutboxRequest>(req => req.IdempotencyKey == $"booking:{sibling1.Id}:confirmation"), It.IsAny<CancellationToken>()), Times.Once);
        _notificationOutboxWriter.Verify(w => w.EnqueueAsync(
            It.Is<NotificationOutboxRequest>(req => req.IdempotencyKey == $"booking:{sibling2.Id}:confirmation"), It.IsAny<CancellationToken>()), Times.Once);
        _notificationOutboxWriter.Verify(w => w.EnqueueAsync(It.IsAny<NotificationOutboxRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WithApproveRemainingSeriesAndAnIneligibleSibling_LeavesThatSiblingPendingAndReportsTheConflict()
    {
        var resource = CreateResource();
        var seriesId = Guid.NewGuid();
        var booking = CreatePendingBooking(resource);
        booking.SeriesId = seriesId;
        SetupEligible(resource, booking);

        var okSibling = CreateSeriesOccurrence(resource, seriesId, booking.UserId, startHourOffsetDays: 7, BookingStatus.Pending);
        var blackedOutSibling = CreateSeriesOccurrence(resource, seriesId, booking.UserId, startHourOffsetDays: 14, BookingStatus.Pending);
        var okSiblingApproval = SetupApprovalRequestFor(okSibling);
        var blackedOutSiblingApproval = SetupApprovalRequestFor(blackedOutSibling);

        _bookingRepository.Setup(r => r.GetBySeriesIdAsync(seriesId, It.IsAny<CancellationToken>())).ReturnsAsync([booking, okSibling, blackedOutSibling]);
        _approvalRequestRepository
            .Setup(r => r.GetByBookingIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([okSiblingApproval, blackedOutSiblingApproval]);
        _bookingAvailabilityRepository
            .Setup(r => r.GetActiveBookingsAsync(resource.Id, okSibling.StartUtc, okSibling.EndUtc, It.IsAny<CancellationToken>()))
            .ReturnsAsync([okSibling]);
        _bookingAvailabilityRepository
            .Setup(r => r.GetActiveBookingsAsync(resource.Id, blackedOutSibling.StartUtc, blackedOutSibling.EndUtc, It.IsAny<CancellationToken>()))
            .ReturnsAsync([blackedOutSibling]);
        // A blackout was added covering only the second sibling's window - the exact "something changed
        // since creation" scenario the re-check exists for, now exercised per-occurrence in a cascade.
        _blackoutPeriodRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new BlackoutPeriod
            {
                Id = Guid.NewGuid(), TenantId = TenantId, ResourceId = resource.Id,
                StartUtc = blackedOutSibling.StartUtc, EndUtc = blackedOutSibling.EndUtc, Reason = "Emergency closure",
            }]);

        var sut = CreateSut();

        var result = await sut.Handle(new ApproveBookingCommandRequest(booking.Id, null, ApproveRemainingSeries: true), CancellationToken.None);

        // The primary booking and the eligible sibling are approved; the ineligible one is left exactly
        // Pending, never force-approved and never rejected on the approver's behalf.
        Assert.Equal(BookingStatus.Confirmed, booking.Status);
        Assert.Equal(BookingStatus.Confirmed, okSibling.Status);
        Assert.Equal(BookingStatus.Pending, blackedOutSibling.Status);
        Assert.Equal(ApprovalStatus.Pending, blackedOutSiblingApproval.Status);
        Assert.Equal([okSibling.Id], result.CascadedApprovedOccurrenceIds);
        var conflict = Assert.Single(result.CascadedConflicts);
        Assert.Equal(blackedOutSibling.Id, conflict.BookingId);
        Assert.Equal(ErrorCodes.BookingBlackoutConflict, conflict.Reason);
    }

    [Fact]
    public async Task Handle_WithApproveRemainingSeriesTrue_NeverTouchesSiblingOccurrencesThatAreNotPending()
    {
        var resource = CreateResource();
        var seriesId = Guid.NewGuid();
        var booking = CreatePendingBooking(resource);
        booking.SeriesId = seriesId;
        SetupEligible(resource, booking);

        var alreadyConfirmed = CreateSeriesOccurrence(resource, seriesId, booking.UserId, startHourOffsetDays: 7, BookingStatus.Confirmed);
        var alreadyRejected = CreateSeriesOccurrence(resource, seriesId, booking.UserId, startHourOffsetDays: 14, BookingStatus.Rejected);
        var alreadyCancelled = CreateSeriesOccurrence(resource, seriesId, booking.UserId, startHourOffsetDays: 21, BookingStatus.Cancelled);
        _bookingRepository.Setup(r => r.GetBySeriesIdAsync(seriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([booking, alreadyConfirmed, alreadyRejected, alreadyCancelled]);

        var sut = CreateSut();

        var result = await sut.Handle(new ApproveBookingCommandRequest(booking.Id, null, ApproveRemainingSeries: true), CancellationToken.None);

        Assert.Empty(result.CascadedApprovedOccurrenceIds);
        Assert.Empty(result.CascadedConflicts);
        _approvalRequestRepository.Verify(r => r.GetByBookingIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WithApproveRemainingSeriesButBookingHasNoSeriesId_IgnoresTheFlag()
    {
        var resource = CreateResource();
        var booking = CreatePendingBooking(resource); // SeriesId is null - a plain one-off booking
        SetupEligible(resource, booking);
        var sut = CreateSut();

        var result = await sut.Handle(new ApproveBookingCommandRequest(booking.Id, null, ApproveRemainingSeries: true), CancellationToken.None);

        Assert.Equal(BookingStatus.Confirmed, result.Status);
        Assert.Empty(result.CascadedApprovedOccurrenceIds);
        Assert.Empty(result.CascadedConflicts);
        _bookingRepository.Verify(r => r.GetBySeriesIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WithApproveRemainingSeriesFalse_DoesNotCascadeEvenWhenTheBookingBelongsToASeries()
    {
        var resource = CreateResource();
        var seriesId = Guid.NewGuid();
        var booking = CreatePendingBooking(resource);
        booking.SeriesId = seriesId;
        SetupEligible(resource, booking);
        var sut = CreateSut();

        var result = await sut.Handle(new ApproveBookingCommandRequest(booking.Id, null), CancellationToken.None);

        Assert.Equal(BookingStatus.Confirmed, result.Status);
        Assert.Empty(result.CascadedApprovedOccurrenceIds);
        _bookingRepository.Verify(r => r.GetBySeriesIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
