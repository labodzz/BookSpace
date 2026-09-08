using BookSpace.Application.Bookings;
using BookSpace.Application.Resources;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Bookings;

public sealed class GetPendingApprovalsQueryHandlerTests
{
    private readonly Mock<IBookingRepository> _bookingRepository = new();
    private readonly Mock<IApprovalRequestRepository> _approvalRequestRepository = new();
    private readonly Mock<IResourceApproverRepository> _resourceApproverRepository = new();
    private readonly Mock<ICurrentUserContext> _currentUserContext = new();

    private static readonly Guid UserId = Guid.NewGuid();

    private GetPendingApprovalsQueryHandler CreateSut() =>
        new(_bookingRepository.Object, _approvalRequestRepository.Object, _resourceApproverRepository.Object, _currentUserContext.Object);

    private static Booking CreatePendingBooking(Guid resourceId) => new()
    {
        Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceId = resourceId, UserId = Guid.NewGuid(),
        StartUtc = DateTimeOffset.UtcNow.AddHours(1), EndUtc = DateTimeOffset.UtcNow.AddHours(2),
        Quantity = 1, Status = BookingStatus.Pending, CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Handle_AsPlainApprover_ScopesToOnlyTheResourcesTheyApproveFor()
    {
        var assignedResourceId = Guid.NewGuid();
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _currentUserContext.SetupGet(c => c.Roles).Returns(["Approver"]);
        _resourceApproverRepository
            .Setup(r => r.GetResourceIdsByUserAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([assignedResourceId]);
        var booking = CreatePendingBooking(assignedResourceId);
        _bookingRepository
            .Setup(r => r.GetPendingApprovalAsync(
                It.Is<IReadOnlyList<Guid>>(ids => ids.Count == 1 && ids[0] == assignedResourceId), It.IsAny<CancellationToken>()))
            .ReturnsAsync([booking]);
        _approvalRequestRepository
            .Setup(r => r.GetByBookingIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ApprovalRequest
            {
                Id = Guid.NewGuid(), TenantId = booking.TenantId, BookingId = booking.Id, ApproverId = null,
                Status = ApprovalStatus.Pending, RequestedAtUtc = DateTimeOffset.UtcNow, ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(48),
            }]);
        var sut = CreateSut();

        var result = await sut.Handle(new GetPendingApprovalsQueryRequest(), CancellationToken.None);

        var item = Assert.Single(result);
        Assert.Equal(booking.Id, item.BookingId);
    }

    [Theory]
    [InlineData("TenantAdmin")]
    [InlineData("SysAdmin")]
    public async Task Handle_AsTenantAdminOrSysAdmin_SeesEveryPendingBookingWithoutResourceRestriction(string role)
    {
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _currentUserContext.SetupGet(c => c.Roles).Returns([role]);
        var booking = CreatePendingBooking(Guid.NewGuid());
        _bookingRepository
            .Setup(r => r.GetPendingApprovalAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([booking]);
        _approvalRequestRepository
            .Setup(r => r.GetByBookingIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var sut = CreateSut();

        var result = await sut.Handle(new GetPendingApprovalsQueryRequest(), CancellationToken.None);

        Assert.Single(result);
        _resourceApproverRepository.Verify(r => r.GetResourceIdsByUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WithNoPendingBookings_ReturnsEmptyWithoutQueryingApprovalRequests()
    {
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _currentUserContext.SetupGet(c => c.Roles).Returns(["TenantAdmin"]);
        _bookingRepository.Setup(r => r.GetPendingApprovalAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<Booking>)[]);
        var sut = CreateSut();

        var result = await sut.Handle(new GetPendingApprovalsQueryRequest(), CancellationToken.None);

        Assert.Empty(result);
        _approvalRequestRepository.Verify(
            r => r.GetByBookingIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
