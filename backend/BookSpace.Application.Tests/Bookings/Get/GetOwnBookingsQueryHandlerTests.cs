using BookSpace.Application.Bookings;
using BookSpace.Application.Common;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Bookings;

public sealed class GetOwnBookingsQueryHandlerTests
{
    private readonly Mock<IBookingRepository> _bookingRepository = new();
    private readonly Mock<ICurrentUserContext> _currentUserContext = new();

    private static readonly Guid UserId = Guid.NewGuid();

    private GetOwnBookingsQueryHandler CreateSut() => new(_bookingRepository.Object, _currentUserContext.Object);

    [Fact]
    public async Task Handle_QueriesByTheCallersOwnUserId_NeverAClientSuppliedOne()
    {
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _bookingRepository
            .Setup(r => r.GetOwnBookingsAsync(UserId, null, null, null, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<Booking>([], 1, 20, 0));
        var sut = CreateSut();

        await sut.Handle(new GetOwnBookingsQueryRequest(), CancellationToken.None);

        _bookingRepository.Verify(r => r.GetOwnBookingsAsync(UserId, null, null, null, 1, 20, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithSeriesIdFilter_PassesItThrough()
    {
        var seriesId = Guid.NewGuid();
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _bookingRepository
            .Setup(r => r.GetOwnBookingsAsync(UserId, seriesId, null, null, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<Booking>([], 1, 20, 0));
        var sut = CreateSut();

        await sut.Handle(new GetOwnBookingsQueryRequest(SeriesId: seriesId), CancellationToken.None);

        _bookingRepository.Verify(r => r.GetOwnBookingsAsync(UserId, seriesId, null, null, 1, 20, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithDateRangeFilter_PassesItThrough()
    {
        var fromUtc = DateTimeOffset.UtcNow;
        var toUtc = fromUtc.AddDays(30);
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _bookingRepository
            .Setup(r => r.GetOwnBookingsAsync(UserId, null, fromUtc, toUtc, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<Booking>([], 1, 20, 0));
        var sut = CreateSut();

        await sut.Handle(new GetOwnBookingsQueryRequest(FromUtc: fromUtc, ToUtc: toUtc), CancellationToken.None);

        _bookingRepository.Verify(r => r.GetOwnBookingsAsync(UserId, null, fromUtc, toUtc, 1, 20, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_MapsBookingsToResponseItemsPreservingPaging()
    {
        var booking = new Booking
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceId = Guid.NewGuid(), UserId = UserId,
            StartUtc = DateTimeOffset.UtcNow, EndUtc = DateTimeOffset.UtcNow.AddHours(1),
            Quantity = 2, Status = BookingStatus.Confirmed, CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _bookingRepository
            .Setup(r => r.GetOwnBookingsAsync(UserId, null, null, null, 2, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<Booking>([booking], 2, 10, 1));
        var sut = CreateSut();

        var result = await sut.Handle(new GetOwnBookingsQueryRequest(Page: 2, PageSize: 10), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(booking.Id, item.Id);
        Assert.Equal(booking.Status, item.Status);
        Assert.Equal(2, result.Page);
        Assert.Equal(10, result.PageSize);
        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public async Task Handle_WithBookingCancelledByItsOwner_MapsCancelledByAdminAsFalse()
    {
        var booking = new Booking
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceId = Guid.NewGuid(), UserId = UserId,
            StartUtc = DateTimeOffset.UtcNow, EndUtc = DateTimeOffset.UtcNow.AddHours(1), Quantity = 1,
            Status = BookingStatus.Cancelled, CreatedAtUtc = DateTimeOffset.UtcNow,
            CancelledAtUtc = DateTimeOffset.UtcNow, CancelledByUserId = UserId, CancellationReason = null,
        };
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _bookingRepository
            .Setup(r => r.GetOwnBookingsAsync(UserId, null, null, null, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<Booking>([booking], 1, 20, 1));
        var sut = CreateSut();

        var result = await sut.Handle(new GetOwnBookingsQueryRequest(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.False(item.CancelledByAdmin);
        Assert.Null(item.CancellationReason);
        Assert.NotNull(item.CancelledAtUtc);
    }

    [Fact]
    public async Task Handle_WithBookingCancelledByATenantAdmin_MapsCancelledByAdminAsTrueWithReason()
    {
        var booking = new Booking
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceId = Guid.NewGuid(), UserId = UserId,
            StartUtc = DateTimeOffset.UtcNow, EndUtc = DateTimeOffset.UtcNow.AddHours(1), Quantity = 1,
            Status = BookingStatus.Cancelled, CreatedAtUtc = DateTimeOffset.UtcNow,
            CancelledAtUtc = DateTimeOffset.UtcNow, CancelledByUserId = Guid.NewGuid(), // a different user - the admin
            CancellationReason = "Resource decommissioned",
        };
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _bookingRepository
            .Setup(r => r.GetOwnBookingsAsync(UserId, null, null, null, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<Booking>([booking], 1, 20, 1));
        var sut = CreateSut();

        var result = await sut.Handle(new GetOwnBookingsQueryRequest(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.True(item.CancelledByAdmin);
        Assert.Equal("Resource decommissioned", item.CancellationReason);
    }

    [Fact]
    public async Task Handle_MapsSeriesId_SoTheClientCanTellRecurringOccurrencesApart()
    {
        var seriesId = Guid.NewGuid();
        var booking = new Booking
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceId = Guid.NewGuid(), UserId = UserId,
            StartUtc = DateTimeOffset.UtcNow, EndUtc = DateTimeOffset.UtcNow.AddHours(1),
            Quantity = 1, Status = BookingStatus.Confirmed, CreatedAtUtc = DateTimeOffset.UtcNow, SeriesId = seriesId,
        };
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _bookingRepository
            .Setup(r => r.GetOwnBookingsAsync(UserId, null, null, null, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<Booking>([booking], 1, 20, 1));
        var sut = CreateSut();

        var result = await sut.Handle(new GetOwnBookingsQueryRequest(), CancellationToken.None);

        Assert.Equal(seriesId, Assert.Single(result.Items).SeriesId);
    }
}
