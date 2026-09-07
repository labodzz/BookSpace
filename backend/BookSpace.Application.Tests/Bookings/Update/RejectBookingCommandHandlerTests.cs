using BookSpace.Application.Bookings;
using BookSpace.Application.Common;
using BookSpace.Application.Resources;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Bookings;

public sealed class RejectBookingCommandHandlerTests
{
    private readonly Mock<IBookingRepository> _bookingRepository = new();
    private readonly Mock<IApprovalRequestRepository> _approvalRequestRepository = new();
    private readonly Mock<IResourceApproverRepository> _resourceApproverRepository = new();
    private readonly Mock<ICurrentUserContext> _currentUserContext = new();

    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ApproverUserId = Guid.NewGuid();
    private static readonly Guid ResourceId = Guid.NewGuid();

    private RejectBookingCommandHandler CreateSut() =>
        new(_bookingRepository.Object, _approvalRequestRepository.Object, _resourceApproverRepository.Object, _currentUserContext.Object);

    private static Booking CreatePendingBooking() => new()
    {
        Id = Guid.NewGuid(), TenantId = TenantId, ResourceId = ResourceId, UserId = Guid.NewGuid(),
        StartUtc = DateTimeOffset.UtcNow.AddHours(1), EndUtc = DateTimeOffset.UtcNow.AddHours(2),
        Quantity = 1, Status = BookingStatus.Pending, CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private ApprovalRequest SetupApprover(Booking booking)
    {
        _currentUserContext.SetupGet(c => c.UserId).Returns(ApproverUserId);
        _currentUserContext.SetupGet(c => c.Roles).Returns([]);
        _bookingRepository.Setup(r => r.FindByIdAsync(booking.Id, It.IsAny<CancellationToken>())).ReturnsAsync(booking);
        _resourceApproverRepository
            .Setup(r => r.FindByResourceAndUserAsync(ResourceId, ApproverUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceApprover { Id = Guid.NewGuid(), TenantId = TenantId, ResourceId = ResourceId, UserId = ApproverUserId });
        var approvalRequest = new ApprovalRequest
        {
            Id = Guid.NewGuid(), TenantId = TenantId, BookingId = booking.Id, ApproverId = null,
            Status = ApprovalStatus.Pending, RequestedAtUtc = DateTimeOffset.UtcNow, ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(48),
        };
        _approvalRequestRepository.Setup(r => r.FindByBookingIdAsync(booking.Id, It.IsAny<CancellationToken>())).ReturnsAsync(approvalRequest);
        return approvalRequest;
    }

    [Fact]
    public async Task Handle_WithOwnResourceApproverAndPendingBooking_RejectsAndSaves()
    {
        var booking = CreatePendingBooking();
        var approvalRequest = SetupApprover(booking);
        var sut = CreateSut();

        var result = await sut.Handle(new RejectBookingCommandRequest(booking.Id, "No longer needed"), CancellationToken.None);

        Assert.Equal(BookingStatus.Rejected, result.Status);
        Assert.Equal(BookingStatus.Rejected, booking.Status);
        Assert.Equal(ApprovalStatus.Rejected, approvalRequest.Status);
        Assert.Equal(ApproverUserId, approvalRequest.ApproverId);
        Assert.Equal("No longer needed", approvalRequest.DecisionNote);
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithUnknownBooking_ThrowsNotFoundException()
    {
        _currentUserContext.SetupGet(c => c.UserId).Returns(ApproverUserId);
        _bookingRepository.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Booking?)null);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new RejectBookingCommandRequest(Guid.NewGuid(), null), CancellationToken.None));

        Assert.Equal(ErrorCodes.BookingNotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WhenCallerIsNotAResourceApproverForThisResource_ThrowsConflictExceptionWithApprovalForbiddenCode()
    {
        var booking = CreatePendingBooking();
        SetupApprover(booking);
        _resourceApproverRepository
            .Setup(r => r.FindByResourceAndUserAsync(ResourceId, ApproverUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResourceApprover?)null);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new RejectBookingCommandRequest(booking.Id, null), CancellationToken.None));

        Assert.Equal(ErrorCodes.BookingApprovalForbidden, exception.ErrorCode);
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WithAlreadyRejectedBooking_ThrowsConflictExceptionWithApprovalNotAllowedCode()
    {
        var booking = CreatePendingBooking();
        booking.Status = BookingStatus.Rejected;
        SetupApprover(booking);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new RejectBookingCommandRequest(booking.Id, null), CancellationToken.None));

        Assert.Equal(ErrorCodes.BookingApprovalNotAllowed, exception.ErrorCode);
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
