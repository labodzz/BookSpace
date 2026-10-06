using BookSpace.Application.Bookings;
using BookSpace.Application.Common;
using BookSpace.Application.Notifications;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Bookings;

public sealed class CancelBookingCommandHandlerTests
{
    private readonly Mock<IBookingRepository> _bookingRepository = new();
    private readonly Mock<ICurrentUserContext> _currentUserContext = new();
    private readonly Mock<INotificationOutboxWriter> _notificationOutboxWriter = new();

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid TenantId = Guid.NewGuid();

    private CancelBookingCommandHandler CreateSut()
    {
        // Mirrors the real writer's "a new row was created" outcome by default - tests that specifically
        // care about the AlreadyExists path override this setup themselves.
        _notificationOutboxWriter
            .Setup(w => w.EnqueueManyAsync(It.IsAny<IReadOnlyList<NotificationOutboxRequest>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<NotificationOutboxRequest> requests, CancellationToken _) =>
                (IReadOnlyList<EnqueueNotificationResult>)requests.Select(_ => new EnqueueNotificationResult(EnqueueOutcome.Created, Guid.NewGuid())).ToList());
        return new CancelBookingCommandHandler(
            new PassThroughResourceBookingLock(), _bookingRepository.Object, _currentUserContext.Object, _notificationOutboxWriter.Object);
    }

    private static Booking CreateBooking(BookingStatus status = BookingStatus.Confirmed, DateTimeOffset? checkedInAtUtc = null) => new()
    {
        Id = Guid.NewGuid(), TenantId = TenantId, ResourceId = Guid.NewGuid(), UserId = UserId,
        StartUtc = DateTimeOffset.UtcNow.AddHours(1), EndUtc = DateTimeOffset.UtcNow.AddHours(2),
        Quantity = 1, Status = status, CheckedInAtUtc = checkedInAtUtc, CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    // The handler now resolves ResourceId (a pre-lock projection) before ever loading the tracked booking -
    // see CancelBookingCommandHandler.Handle. Every test exercising a real, found booking must set up both.
    private void SetupFoundBooking(Booking booking)
    {
        _bookingRepository.Setup(r => r.FindResourceIdAsync(booking.Id, It.IsAny<CancellationToken>())).ReturnsAsync(booking.ResourceId);
        _bookingRepository.Setup(r => r.FindByIdAsync(booking.Id, It.IsAny<CancellationToken>())).ReturnsAsync(booking);
    }

    [Fact]
    public async Task Handle_WithOwnConfirmedBooking_CancelsAndSaves()
    {
        var booking = CreateBooking(BookingStatus.Confirmed);
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        SetupFoundBooking(booking);
        var sut = CreateSut();

        var result = await sut.Handle(new CancelBookingCommandRequest(booking.Id), CancellationToken.None);

        Assert.Equal(BookingStatus.Cancelled, result.Status);
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(UserId, booking.CancelledByUserId);
        Assert.NotNull(booking.CancelledAtUtc);
        _notificationOutboxWriter.Verify(w => w.EnqueueManyAsync(
            It.Is<IReadOnlyList<NotificationOutboxRequest>>(requests => requests.Count == 1 &&
                requests[0].TenantId == TenantId && requests[0].NotificationType == BookingNotificationTypes.Cancellation &&
                requests[0].RecipientUserId == UserId && requests[0].IdempotencyKey == $"booking:{booking.Id}:cancellation"),
            It.IsAny<CancellationToken>()), Times.Once);
        // No separate SaveChangesAsync call: EnqueueManyAsync's own commit is the only persistence point, so
        // a failure there (see the dedicated atomicity test below) can never leave the booking mutation
        // saved without its notification, or vice versa.
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenTheNotificationEnqueueFails_DoesNotPersistTheCancellationEitherSinceNoSeparateSaveExists()
    {
        // Proves atomicity the only way a mock-based handler test can: the handler has no fallback
        // SaveChangesAsync call that could persist the booking mutation independently of the enqueue - if
        // EnqueueAsync throws, NOTHING in this handler commits anything.
        var booking = CreateBooking(BookingStatus.Confirmed);
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        SetupFoundBooking(booking);
        var sut = CreateSut();
        _notificationOutboxWriter
            .Setup(w => w.EnqueueManyAsync(It.IsAny<IReadOnlyList<NotificationOutboxRequest>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Simulated unexpected database failure."));

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.Handle(new CancelBookingCommandRequest(booking.Id), CancellationToken.None));

        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenTheNotificationWasAlreadyEnqueuedByAnEarlierAttempt_StillCancelsSuccessfully()
    {
        // EnqueueOutcome.AlreadyExists is explicitly not an error (see INotificationOutboxWriter) - a
        // retried cancel request (the client resending after a timeout, say) must not surface it as one.
        var booking = CreateBooking(BookingStatus.Confirmed);
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        SetupFoundBooking(booking);
        _notificationOutboxWriter
            .Setup(w => w.EnqueueManyAsync(It.IsAny<IReadOnlyList<NotificationOutboxRequest>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<NotificationOutboxRequest> requests, CancellationToken _) =>
                (IReadOnlyList<EnqueueNotificationResult>)requests.Select(_ => new EnqueueNotificationResult(EnqueueOutcome.AlreadyExists, null)).ToList());
        var sut = CreateSut();

        var result = await sut.Handle(new CancelBookingCommandRequest(booking.Id), CancellationToken.None);

        Assert.Equal(BookingStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task Handle_ForABookingInADifferentTenant_NeverEnqueuesACancellationNotification()
    {
        // FindByIdAsync is tenant-filtered in the real repository (BookSpaceDbContext's global query
        // filter) - a booking from another tenant is never found at all, so this never reaches the enqueue
        // call. Proven here at the handler boundary: simulating "not found" (what the filtered repository
        // actually returns for a cross-tenant id) must never enqueue anything.
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _bookingRepository.Setup(r => r.FindResourceIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid?)null);
        var sut = CreateSut();

        await Assert.ThrowsAsync<NotFoundException>(() => sut.Handle(new CancelBookingCommandRequest(Guid.NewGuid()), CancellationToken.None));

        _notificationOutboxWriter.Verify(w => w.EnqueueManyAsync(It.IsAny<IReadOnlyList<NotificationOutboxRequest>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WithUnknownBooking_ThrowsNotFoundException()
    {
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _bookingRepository.Setup(r => r.FindResourceIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid?)null);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new CancelBookingCommandRequest(Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(ErrorCodes.BookingNotFound, exception.ErrorCode);
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _notificationOutboxWriter.Verify(w => w.EnqueueManyAsync(It.IsAny<IReadOnlyList<NotificationOutboxRequest>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WithBookingBelongingToAnotherUser_ThrowsNotFoundException()
    {
        var booking = CreateBooking();
        booking.UserId = Guid.NewGuid(); // a different user's booking
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _currentUserContext.SetupGet(c => c.Roles).Returns([]); // a plain member, no admin override
        SetupFoundBooking(booking);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new CancelBookingCommandRequest(booking.Id), CancellationToken.None));

        Assert.Equal(ErrorCodes.BookingNotFound, exception.ErrorCode);
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _notificationOutboxWriter.Verify(w => w.EnqueueManyAsync(It.IsAny<IReadOnlyList<NotificationOutboxRequest>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WithNonAdminRoleCancellingAnotherUsersBooking_ThrowsNotFoundException()
    {
        // Proves a role that's merely present (not empty, as in the test above) but not privileged still
        // doesn't bypass the ownership check.
        var booking = CreateBooking();
        booking.UserId = Guid.NewGuid();
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _currentUserContext.SetupGet(c => c.Roles).Returns(["Member"]);
        SetupFoundBooking(booking);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new CancelBookingCommandRequest(booking.Id), CancellationToken.None));

        Assert.Equal(ErrorCodes.BookingNotFound, exception.ErrorCode);
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _notificationOutboxWriter.Verify(w => w.EnqueueManyAsync(It.IsAny<IReadOnlyList<NotificationOutboxRequest>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // SysAdmin is deliberately not parameterized here any more - see ApprovalAuthorization.IsTenantAdmin,
    // which no longer treats it as privileged; a global SysAdmin cancelling someone else's booking now
    // gets the identical NotFoundException a plain Member would (see
    // Handle_AsSysAdminCancellingAnotherUsersBooking_ThrowsNotFoundException below).
    [Fact]
    public async Task Handle_WithPrivilegedRoleCancellingAnotherUsersBooking_CancelsAndRecordsTheAdminAsCanceller()
    {
        var adminUserId = Guid.NewGuid();
        var booking = CreateBooking(BookingStatus.Confirmed); // owned by UserId, not adminUserId
        _currentUserContext.SetupGet(c => c.UserId).Returns(adminUserId);
        _currentUserContext.SetupGet(c => c.Roles).Returns(["TenantAdmin"]);
        SetupFoundBooking(booking);
        var sut = CreateSut();

        var result = await sut.Handle(
            new CancelBookingCommandRequest(booking.Id, Reason: "Freeing the resource for maintenance"), CancellationToken.None);

        Assert.Equal(BookingStatus.Cancelled, result.Status);
        Assert.Equal(adminUserId, booking.CancelledByUserId);
        Assert.NotEqual(booking.UserId, booking.CancelledByUserId);
        Assert.Equal("Freeing the resource for maintenance", booking.CancellationReason);
        // The notification goes to the booking OWNER (booking.UserId), never the admin who performed the
        // cancellation - adminUserId never appears as a RecipientUserId anywhere in this assertion.
        _notificationOutboxWriter.Verify(w => w.EnqueueManyAsync(
            It.Is<IReadOnlyList<NotificationOutboxRequest>>(requests => requests.Count == 1 &&
                requests[0].RecipientUserId == booking.UserId && requests[0].NotificationType == BookingNotificationTypes.Cancellation),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_TenantAdminCancelRemainingSeriesTrue_AppliesTheSameReasonToCascadedOccurrences()
    {
        var adminUserId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var anchor = DateTimeOffset.UtcNow.AddDays(5);
        var booking = CreateBooking();
        booking.SeriesId = seriesId;
        booking.StartUtc = anchor;
        booking.EndUtc = anchor.AddHours(1);
        var laterOccurrence = new Booking
        {
            Id = Guid.NewGuid(), TenantId = TenantId, ResourceId = booking.ResourceId, UserId = UserId, SeriesId = seriesId,
            StartUtc = anchor.AddDays(1), EndUtc = anchor.AddDays(1).AddHours(1), Quantity = 1, Status = BookingStatus.Confirmed, CreatedAtUtc = anchor,
        };

        _currentUserContext.SetupGet(c => c.UserId).Returns(adminUserId);
        _currentUserContext.SetupGet(c => c.Roles).Returns(["TenantAdmin"]);
        SetupFoundBooking(booking);
        _bookingRepository
            .Setup(r => r.GetBySeriesIdAsync(seriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([booking, laterOccurrence]);
        var sut = CreateSut();

        await sut.Handle(
            new CancelBookingCommandRequest(booking.Id, CancelRemainingSeries: true, Reason: "Resource decommissioned"), CancellationToken.None);

        Assert.Equal("Resource decommissioned", booking.CancellationReason);
        Assert.Equal("Resource decommissioned", laterOccurrence.CancellationReason);
        Assert.Equal(adminUserId, laterOccurrence.CancelledByUserId);
        // One idempotent outbox item per actually-cancelled occurrence, not one for the whole series - see
        // docs/background-jobs.md ("Booking lifecycle notifications - cancellation"). Both go to their
        // respective owners (both UserId here), never to adminUserId. Requested in a SINGLE EnqueueManyAsync
        // call so one SaveChangesAsync covers both cancellations and both notifications together.
        _notificationOutboxWriter.Verify(w => w.EnqueueManyAsync(
            It.Is<IReadOnlyList<NotificationOutboxRequest>>(requests =>
                requests.Count == 2 &&
                requests.Any(req => req.IdempotencyKey == $"booking:{booking.Id}:cancellation" && req.RecipientUserId == UserId) &&
                requests.Any(req => req.IdempotencyKey == $"booking:{laterOccurrence.Id}:cancellation" && req.RecipientUserId == UserId)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithAlreadyCancelledBooking_IsIdempotentAndDoesNotSaveAgain()
    {
        var booking = CreateBooking(BookingStatus.Cancelled);
        booking.CancelledAtUtc = DateTimeOffset.UtcNow.AddDays(-1);
        booking.CancelledByUserId = UserId;
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        SetupFoundBooking(booking);
        var sut = CreateSut();

        var result = await sut.Handle(new CancelBookingCommandRequest(booking.Id), CancellationToken.None);

        Assert.Equal(BookingStatus.Cancelled, result.Status);
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        // Already Cancelled before this call = no real state change, so no new notification either.
        _notificationOutboxWriter.Verify(w => w.EnqueueManyAsync(It.IsAny<IReadOnlyList<NotificationOutboxRequest>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(BookingStatus.Completed)]
    [InlineData(BookingStatus.Rejected)]
    [InlineData(BookingStatus.NoShow)]
    public async Task Handle_WithBookingInATerminalNonCancellableStatus_ThrowsConflictException(BookingStatus status)
    {
        var booking = CreateBooking(status);
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        SetupFoundBooking(booking);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new CancelBookingCommandRequest(booking.Id), CancellationToken.None));

        Assert.Equal(ErrorCodes.BookingCancellationNotAllowed, exception.ErrorCode);
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _notificationOutboxWriter.Verify(w => w.EnqueueManyAsync(It.IsAny<IReadOnlyList<NotificationOutboxRequest>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A checked-in booking is never retroactively cancelled, by anyone - the TenantAdmin bypass only ever
    // widens WHO may act (the ownership check), never WHICH states are eligible (the status
    // check below, unconditional for every caller including admins - confirmed by reading
    // CancelBookingCommandHandler.CancelUnderLockAsync: CancellableStatuses has no role branch, so this
    // rule follows that exact, already-existing precedent rather than inventing a new one).
    [Fact]
    public async Task Handle_WithAlreadyCheckedInBooking_ThrowsConflictExceptionAndNeverTouchesCheckedInAtUtc()
    {
        var checkedInAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
        var booking = CreateBooking(BookingStatus.Confirmed, checkedInAtUtc);
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        SetupFoundBooking(booking);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new CancelBookingCommandRequest(booking.Id), CancellationToken.None));

        Assert.Equal(ErrorCodes.BookingAlreadyCheckedIn, exception.ErrorCode);
        Assert.Equal(BookingStatus.Confirmed, booking.Status); // never mutated
        Assert.Equal(checkedInAtUtc, booking.CheckedInAtUtc); // never cleared or changed
        _notificationOutboxWriter.Verify(w => w.EnqueueManyAsync(It.IsAny<IReadOnlyList<NotificationOutboxRequest>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WithPrivilegedRoleCancellingAnAlreadyCheckedInBooking_StillThrowsConflictException()
    {
        var adminUserId = Guid.NewGuid();
        var booking = CreateBooking(BookingStatus.Confirmed, DateTimeOffset.UtcNow.AddMinutes(-5)); // owned by UserId, not adminUserId
        _currentUserContext.SetupGet(c => c.UserId).Returns(adminUserId);
        _currentUserContext.SetupGet(c => c.Roles).Returns(["TenantAdmin"]);
        SetupFoundBooking(booking);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new CancelBookingCommandRequest(booking.Id), CancellationToken.None));

        Assert.Equal(ErrorCodes.BookingAlreadyCheckedIn, exception.ErrorCode);
    }

    // The global SysAdmin is never a tenant member - cancelling someone else's booking must fail the
    // identical way a plain Member's attempt does (NotFoundException, never revealing the booking exists -
    // see Handle_WithNonAdminRoleCancellingAnotherUsersBooking_ThrowsNotFoundException above), not succeed
    // the way TenantAdmin's does. See ApprovalAuthorization.IsTenantAdmin.
    [Fact]
    public async Task Handle_AsSysAdminCancellingAnotherUsersBooking_ThrowsNotFoundException()
    {
        var adminUserId = Guid.NewGuid();
        var booking = CreateBooking(BookingStatus.Confirmed);
        booking.UserId = Guid.NewGuid();
        _currentUserContext.SetupGet(c => c.UserId).Returns(adminUserId);
        _currentUserContext.SetupGet(c => c.Roles).Returns(["SysAdmin"]);
        SetupFoundBooking(booking);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new CancelBookingCommandRequest(booking.Id), CancellationToken.None));

        Assert.Equal(ErrorCodes.BookingNotFound, exception.ErrorCode);
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_PropagatesCancellation_WithoutTranslatingItIntoABusinessConflict()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _bookingRepository
            .Setup(r => r.FindResourceIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var sut = CreateSut();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.Handle(new CancelBookingCommandRequest(Guid.NewGuid()), cts.Token));
    }

    [Fact]
    public async Task Handle_CancelRemainingSeriesFalse_OnlyCancelsThisOneOccurrenceLeavingOthersUntouched()
    {
        var seriesId = Guid.NewGuid();
        var booking = CreateBooking();
        booking.SeriesId = seriesId;
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        SetupFoundBooking(booking);
        var sut = CreateSut();

        var result = await sut.Handle(new CancelBookingCommandRequest(booking.Id, CancelRemainingSeries: false), CancellationToken.None);

        Assert.Equal(BookingStatus.Cancelled, result.Status);
        Assert.Empty(result.CascadedOccurrenceIds);
        _bookingRepository.Verify(r => r.GetBySeriesIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_CancelRemainingSeriesTrue_CancelsThisAndLaterOccurrencesButNotEarlierOnes()
    {
        var seriesId = Guid.NewGuid();
        var anchor = DateTimeOffset.UtcNow.AddDays(5);
        var booking = CreateBooking();
        booking.SeriesId = seriesId;
        booking.StartUtc = anchor;
        booking.EndUtc = anchor.AddHours(1);

        var earlierOccurrence = new Booking
        {
            Id = Guid.NewGuid(), TenantId = TenantId, ResourceId = booking.ResourceId, UserId = UserId, SeriesId = seriesId,
            StartUtc = anchor.AddDays(-1), EndUtc = anchor.AddDays(-1).AddHours(1), Quantity = 1, Status = BookingStatus.Confirmed, CreatedAtUtc = anchor,
        };
        var laterOccurrence = new Booking
        {
            Id = Guid.NewGuid(), TenantId = TenantId, ResourceId = booking.ResourceId, UserId = UserId, SeriesId = seriesId,
            StartUtc = anchor.AddDays(1), EndUtc = anchor.AddDays(1).AddHours(1), Quantity = 1, Status = BookingStatus.Confirmed, CreatedAtUtc = anchor,
        };
        var alreadyCompletedLaterOccurrence = new Booking
        {
            Id = Guid.NewGuid(), TenantId = TenantId, ResourceId = booking.ResourceId, UserId = UserId, SeriesId = seriesId,
            StartUtc = anchor.AddDays(2), EndUtc = anchor.AddDays(2).AddHours(1), Quantity = 1, Status = BookingStatus.Completed, CreatedAtUtc = anchor,
        };

        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        SetupFoundBooking(booking);
        _bookingRepository
            .Setup(r => r.GetBySeriesIdAsync(seriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([earlierOccurrence, booking, laterOccurrence, alreadyCompletedLaterOccurrence]);
        var sut = CreateSut();

        var result = await sut.Handle(new CancelBookingCommandRequest(booking.Id, CancelRemainingSeries: true), CancellationToken.None);

        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(BookingStatus.Cancelled, laterOccurrence.Status);
        Assert.Contains(laterOccurrence.Id, result.CascadedOccurrenceIds);
        // Never touched: an earlier occurrence, and a later one that already ran its course (Completed).
        Assert.Equal(BookingStatus.Confirmed, earlierOccurrence.Status);
        Assert.Equal(BookingStatus.Completed, alreadyCompletedLaterOccurrence.Status);
        Assert.DoesNotContain(earlierOccurrence.Id, result.CascadedOccurrenceIds);
        Assert.DoesNotContain(alreadyCompletedLaterOccurrence.Id, result.CascadedOccurrenceIds);
        Assert.DoesNotContain(booking.Id, result.CascadedOccurrenceIds); // the anchor itself is Id, not a "cascaded" extra
        // Exactly one notification per actually-cancelled occurrence (booking + laterOccurrence) - none for
        // the untouched earlier or already-completed ones, requested in a single EnqueueManyAsync call.
        _notificationOutboxWriter.Verify(w => w.EnqueueManyAsync(
            It.Is<IReadOnlyList<NotificationOutboxRequest>>(requests =>
                requests.Count == 2 &&
                requests.Any(req => req.IdempotencyKey == $"booking:{booking.Id}:cancellation") &&
                requests.Any(req => req.IdempotencyKey == $"booking:{laterOccurrence.Id}:cancellation")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_CancelRemainingSeriesTrue_SkipsLaterOccurrencesAlreadyRejectedOrCancelled()
    {
        // The Completed exclusion is already proven above; CancellableStatuses excludes four statuses in
        // total (Rejected, Cancelled, Completed, NoShow) via one boolean check - this proves the other two.
        var seriesId = Guid.NewGuid();
        var anchor = DateTimeOffset.UtcNow.AddDays(5);
        var booking = CreateBooking();
        booking.SeriesId = seriesId;
        booking.StartUtc = anchor;
        booking.EndUtc = anchor.AddHours(1);

        var alreadyRejectedLaterOccurrence = new Booking
        {
            Id = Guid.NewGuid(), TenantId = TenantId, ResourceId = booking.ResourceId, UserId = UserId, SeriesId = seriesId,
            StartUtc = anchor.AddDays(1), EndUtc = anchor.AddDays(1).AddHours(1), Quantity = 1, Status = BookingStatus.Rejected, CreatedAtUtc = anchor,
        };
        var alreadyCancelledLaterOccurrence = new Booking
        {
            Id = Guid.NewGuid(), TenantId = TenantId, ResourceId = booking.ResourceId, UserId = UserId, SeriesId = seriesId,
            StartUtc = anchor.AddDays(2), EndUtc = anchor.AddDays(2).AddHours(1), Quantity = 1, Status = BookingStatus.Cancelled, CreatedAtUtc = anchor,
        };

        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        SetupFoundBooking(booking);
        _bookingRepository
            .Setup(r => r.GetBySeriesIdAsync(seriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([booking, alreadyRejectedLaterOccurrence, alreadyCancelledLaterOccurrence]);
        var sut = CreateSut();

        var result = await sut.Handle(new CancelBookingCommandRequest(booking.Id, CancelRemainingSeries: true), CancellationToken.None);

        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(BookingStatus.Rejected, alreadyRejectedLaterOccurrence.Status);
        Assert.Equal(BookingStatus.Cancelled, alreadyCancelledLaterOccurrence.Status);
        Assert.DoesNotContain(alreadyRejectedLaterOccurrence.Id, result.CascadedOccurrenceIds);
        Assert.DoesNotContain(alreadyCancelledLaterOccurrence.Id, result.CascadedOccurrenceIds);
    }

    // Proves no partial/arbitrary cascade state: a checked-in sibling is silently EXCLUDED from the
    // cascade (same treatment as an already-Cancelled/Rejected/Completed sibling), never causes the loop to
    // throw or abort - the rest of the series is still cancelled in the same call, in the same commit.
    [Fact]
    public async Task Handle_CancelRemainingSeriesTrue_SkipsACheckedInLaterOccurrenceButStillCancelsTheRestOfTheSeries()
    {
        var seriesId = Guid.NewGuid();
        var anchor = DateTimeOffset.UtcNow.AddDays(5);
        var booking = CreateBooking();
        booking.SeriesId = seriesId;
        booking.StartUtc = anchor;
        booking.EndUtc = anchor.AddHours(1);

        var checkedInLaterOccurrence = new Booking
        {
            Id = Guid.NewGuid(), TenantId = TenantId, ResourceId = booking.ResourceId, UserId = UserId, SeriesId = seriesId,
            StartUtc = anchor.AddDays(1), EndUtc = anchor.AddDays(1).AddHours(1), Quantity = 1, Status = BookingStatus.Confirmed,
            CheckedInAtUtc = anchor.AddDays(1).AddMinutes(-5), CreatedAtUtc = anchor,
        };
        var stillEligibleLaterOccurrence = new Booking
        {
            Id = Guid.NewGuid(), TenantId = TenantId, ResourceId = booking.ResourceId, UserId = UserId, SeriesId = seriesId,
            StartUtc = anchor.AddDays(2), EndUtc = anchor.AddDays(2).AddHours(1), Quantity = 1, Status = BookingStatus.Confirmed, CreatedAtUtc = anchor,
        };

        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        SetupFoundBooking(booking);
        _bookingRepository
            .Setup(r => r.GetBySeriesIdAsync(seriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([booking, checkedInLaterOccurrence, stillEligibleLaterOccurrence]);
        var sut = CreateSut();

        var result = await sut.Handle(new CancelBookingCommandRequest(booking.Id, CancelRemainingSeries: true), CancellationToken.None);

        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(BookingStatus.Cancelled, stillEligibleLaterOccurrence.Status);
        Assert.Contains(stillEligibleLaterOccurrence.Id, result.CascadedOccurrenceIds);
        // Never retroactively cancelled, and never even attempted - status/CheckedInAtUtc both untouched.
        Assert.Equal(BookingStatus.Confirmed, checkedInLaterOccurrence.Status);
        Assert.NotNull(checkedInLaterOccurrence.CheckedInAtUtc);
        Assert.DoesNotContain(checkedInLaterOccurrence.Id, result.CascadedOccurrenceIds);
        // Exactly one notification per ACTUALLY cancelled occurrence - none for the checked-in one.
        _notificationOutboxWriter.Verify(w => w.EnqueueManyAsync(
            It.Is<IReadOnlyList<NotificationOutboxRequest>>(requests =>
                requests.Count == 2 &&
                requests.Any(req => req.IdempotencyKey == $"booking:{booking.Id}:cancellation") &&
                requests.Any(req => req.IdempotencyKey == $"booking:{stillEligibleLaterOccurrence.Id}:cancellation") &&
                requests.All(req => req.IdempotencyKey != $"booking:{checkedInLaterOccurrence.Id}:cancellation")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_CancelRemainingSeriesTrue_CascadesAPendingLaterOccurrence()
    {
        // CancellableStatuses includes Pending - an occurrence still awaiting approval must be cascaded
        // exactly like a Confirmed one, not left dangling because it hasn't been decided yet.
        var seriesId = Guid.NewGuid();
        var anchor = DateTimeOffset.UtcNow.AddDays(5);
        var booking = CreateBooking();
        booking.SeriesId = seriesId;
        booking.StartUtc = anchor;
        booking.EndUtc = anchor.AddHours(1);

        var pendingLaterOccurrence = new Booking
        {
            Id = Guid.NewGuid(), TenantId = TenantId, ResourceId = booking.ResourceId, UserId = UserId, SeriesId = seriesId,
            StartUtc = anchor.AddDays(1), EndUtc = anchor.AddDays(1).AddHours(1), Quantity = 1, Status = BookingStatus.Pending, CreatedAtUtc = anchor,
        };

        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        SetupFoundBooking(booking);
        _bookingRepository
            .Setup(r => r.GetBySeriesIdAsync(seriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([booking, pendingLaterOccurrence]);
        var sut = CreateSut();

        var result = await sut.Handle(new CancelBookingCommandRequest(booking.Id, CancelRemainingSeries: true), CancellationToken.None);

        Assert.Equal(BookingStatus.Cancelled, pendingLaterOccurrence.Status);
        Assert.Contains(pendingLaterOccurrence.Id, result.CascadedOccurrenceIds);
    }

    [Fact]
    public async Task Handle_WithOwnPendingBooking_CancelsAndSaves()
    {
        // Cancelling a single (non-cascaded) Pending booking - the one CancellableStatuses status never
        // exercised by the ordinary single-cancellation tests above (which all default to Confirmed).
        var booking = CreateBooking(BookingStatus.Pending);
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        SetupFoundBooking(booking);
        var sut = CreateSut();

        var result = await sut.Handle(new CancelBookingCommandRequest(booking.Id), CancellationToken.None);

        Assert.Equal(BookingStatus.Cancelled, result.Status);
        _notificationOutboxWriter.Verify(w => w.EnqueueManyAsync(
            It.Is<IReadOnlyList<NotificationOutboxRequest>>(requests =>
                requests.Count == 1 && requests[0].NotificationType == BookingNotificationTypes.Cancellation),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_CancelRemainingSeriesTrueOnAOneOffBooking_IsIgnoredAndOnlyCancelsTheOneBooking()
    {
        var booking = CreateBooking(); // SeriesId is null - a one-off booking
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        SetupFoundBooking(booking);
        var sut = CreateSut();

        var result = await sut.Handle(new CancelBookingCommandRequest(booking.Id, CancelRemainingSeries: true), CancellationToken.None);

        Assert.Equal(BookingStatus.Cancelled, result.Status);
        Assert.Empty(result.CascadedOccurrenceIds);
        _bookingRepository.Verify(r => r.GetBySeriesIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
