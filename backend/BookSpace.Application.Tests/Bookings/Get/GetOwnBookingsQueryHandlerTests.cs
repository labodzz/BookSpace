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
            .Setup(r => r.GetOwnBookingsAsync(UserId, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<Booking>([], 1, 20, 0));
        var sut = CreateSut();

        await sut.Handle(new GetOwnBookingsQueryRequest(), CancellationToken.None);

        _bookingRepository.Verify(r => r.GetOwnBookingsAsync(UserId, 1, 20, It.IsAny<CancellationToken>()), Times.Once);
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
            .Setup(r => r.GetOwnBookingsAsync(UserId, 2, 10, It.IsAny<CancellationToken>()))
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
}
