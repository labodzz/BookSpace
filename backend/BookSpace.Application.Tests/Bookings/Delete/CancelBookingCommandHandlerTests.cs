using BookSpace.Application.Bookings;
using BookSpace.Application.Common;
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

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid TenantId = Guid.NewGuid();

    private CancelBookingCommandHandler CreateSut() => new(_bookingRepository.Object, _currentUserContext.Object);

    private static Booking CreateBooking(BookingStatus status = BookingStatus.Confirmed) => new()
    {
        Id = Guid.NewGuid(), TenantId = TenantId, ResourceId = Guid.NewGuid(), UserId = UserId,
        StartUtc = DateTimeOffset.UtcNow.AddHours(1), EndUtc = DateTimeOffset.UtcNow.AddHours(2),
        Quantity = 1, Status = status, CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Handle_WithOwnConfirmedBooking_CancelsAndSaves()
    {
        var booking = CreateBooking(BookingStatus.Confirmed);
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _bookingRepository.Setup(r => r.FindByIdAsync(booking.Id, It.IsAny<CancellationToken>())).ReturnsAsync(booking);
        var sut = CreateSut();

        var result = await sut.Handle(new CancelBookingCommandRequest(booking.Id), CancellationToken.None);

        Assert.Equal(BookingStatus.Cancelled, result.Status);
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(UserId, booking.CancelledByUserId);
        Assert.NotNull(booking.CancelledAtUtc);
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithUnknownBooking_ThrowsNotFoundException()
    {
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _bookingRepository.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Booking?)null);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new CancelBookingCommandRequest(Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(ErrorCodes.BookingNotFound, exception.ErrorCode);
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WithBookingBelongingToAnotherUser_ThrowsNotFoundException()
    {
        var booking = CreateBooking();
        booking.UserId = Guid.NewGuid(); // a different user's booking
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _bookingRepository.Setup(r => r.FindByIdAsync(booking.Id, It.IsAny<CancellationToken>())).ReturnsAsync(booking);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new CancelBookingCommandRequest(booking.Id), CancellationToken.None));

        Assert.Equal(ErrorCodes.BookingNotFound, exception.ErrorCode);
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WithAlreadyCancelledBooking_IsIdempotentAndDoesNotSaveAgain()
    {
        var booking = CreateBooking(BookingStatus.Cancelled);
        booking.CancelledAtUtc = DateTimeOffset.UtcNow.AddDays(-1);
        booking.CancelledByUserId = UserId;
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _bookingRepository.Setup(r => r.FindByIdAsync(booking.Id, It.IsAny<CancellationToken>())).ReturnsAsync(booking);
        var sut = CreateSut();

        var result = await sut.Handle(new CancelBookingCommandRequest(booking.Id), CancellationToken.None);

        Assert.Equal(BookingStatus.Cancelled, result.Status);
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(BookingStatus.Completed)]
    [InlineData(BookingStatus.Rejected)]
    [InlineData(BookingStatus.NoShow)]
    public async Task Handle_WithBookingInATerminalNonCancellableStatus_ThrowsConflictException(BookingStatus status)
    {
        var booking = CreateBooking(status);
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _bookingRepository.Setup(r => r.FindByIdAsync(booking.Id, It.IsAny<CancellationToken>())).ReturnsAsync(booking);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new CancelBookingCommandRequest(booking.Id), CancellationToken.None));

        Assert.Equal(ErrorCodes.BookingCancellationNotAllowed, exception.ErrorCode);
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_CancelRemainingSeriesFalse_OnlyCancelsThisOneOccurrenceLeavingOthersUntouched()
    {
        var seriesId = Guid.NewGuid();
        var booking = CreateBooking();
        booking.SeriesId = seriesId;
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _bookingRepository.Setup(r => r.FindByIdAsync(booking.Id, It.IsAny<CancellationToken>())).ReturnsAsync(booking);
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
        _bookingRepository.Setup(r => r.FindByIdAsync(booking.Id, It.IsAny<CancellationToken>())).ReturnsAsync(booking);
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
        _bookingRepository.Setup(r => r.FindByIdAsync(booking.Id, It.IsAny<CancellationToken>())).ReturnsAsync(booking);
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
        _bookingRepository.Setup(r => r.FindByIdAsync(booking.Id, It.IsAny<CancellationToken>())).ReturnsAsync(booking);
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
        _bookingRepository.Setup(r => r.FindByIdAsync(booking.Id, It.IsAny<CancellationToken>())).ReturnsAsync(booking);
        var sut = CreateSut();

        var result = await sut.Handle(new CancelBookingCommandRequest(booking.Id), CancellationToken.None);

        Assert.Equal(BookingStatus.Cancelled, result.Status);
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_CancelRemainingSeriesTrueOnAOneOffBooking_IsIgnoredAndOnlyCancelsTheOneBooking()
    {
        var booking = CreateBooking(); // SeriesId is null - a one-off booking
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _bookingRepository.Setup(r => r.FindByIdAsync(booking.Id, It.IsAny<CancellationToken>())).ReturnsAsync(booking);
        var sut = CreateSut();

        var result = await sut.Handle(new CancelBookingCommandRequest(booking.Id, CancelRemainingSeries: true), CancellationToken.None);

        Assert.Equal(BookingStatus.Cancelled, result.Status);
        Assert.Empty(result.CascadedOccurrenceIds);
        _bookingRepository.Verify(r => r.GetBySeriesIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
