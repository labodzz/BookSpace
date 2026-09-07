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
}
