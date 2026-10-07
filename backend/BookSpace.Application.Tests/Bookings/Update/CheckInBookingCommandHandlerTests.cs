using BookSpace.Application.Bookings;
using BookSpace.Application.Common;
using BookSpace.Application.Resources;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Bookings;

public sealed class CheckInBookingCommandHandlerTests
{
    private readonly Mock<IBookingRepository> _bookingRepository = new();
    private readonly Mock<ITenantRepository> _tenantRepository = new();
    private readonly Mock<IResourceApproverRepository> _resourceApproverRepository = new();
    private readonly Mock<ICurrentUserContext> _currentUserContext = new();

    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid OwnerUserId = Guid.NewGuid();
    private static readonly Guid ResourceId = Guid.NewGuid();
    private static readonly DateTimeOffset StartUtc = DateTimeOffset.UtcNow; // "now" is within the window by construction

    private CheckInBookingCommandHandler CreateSut() => new(
        new PassThroughResourceBookingLock(), _bookingRepository.Object, _tenantRepository.Object,
        _resourceApproverRepository.Object, _currentUserContext.Object);

    private static Booking CreateBooking(BookingStatus status = BookingStatus.Confirmed, DateTimeOffset? checkedInAtUtc = null) => new()
    {
        Id = Guid.NewGuid(), TenantId = TenantId, ResourceId = ResourceId, UserId = OwnerUserId,
        StartUtc = StartUtc, EndUtc = StartUtc.AddHours(1), Quantity = 1, Status = status,
        CheckedInAtUtc = checkedInAtUtc, CreatedAtUtc = StartUtc.AddDays(-1),
    };

    private static Tenant CreateTenant() => new()
    {
        Id = TenantId, Name = "Test Tenant", DefaultTimeZoneId = "UTC", CreatedAtUtc = DateTimeOffset.UtcNow,
        CheckInLeadMinutes = 15, NoShowGraceMinutes = 15,
    };

    private void SetupBooking(Booking booking)
    {
        _bookingRepository.Setup(r => r.FindResourceIdAsync(booking.Id, It.IsAny<CancellationToken>())).ReturnsAsync(booking.ResourceId);
        _bookingRepository.Setup(r => r.FindByIdAsync(booking.Id, It.IsAny<CancellationToken>())).ReturnsAsync(booking);
        _tenantRepository.Setup(r => r.FindByIdAsync(TenantId, It.IsAny<CancellationToken>())).ReturnsAsync(CreateTenant());
    }

    [Fact]
    public async Task Handle_OwnerCheckingInTheirOwnConfirmedBookingWithinTheWindow_Succeeds()
    {
        var booking = CreateBooking();
        SetupBooking(booking);
        _currentUserContext.SetupGet(c => c.UserId).Returns(OwnerUserId);
        _currentUserContext.SetupGet(c => c.Roles).Returns([]);
        var sut = CreateSut();

        var result = await sut.Handle(new CheckInBookingCommandRequest(booking.Id), CancellationToken.None);

        Assert.False(result.AlreadyCheckedIn);
        Assert.Equal(booking.CheckedInAtUtc, result.CheckedInAtUtc);
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithUnknownBooking_ThrowsNotFoundException()
    {
        _bookingRepository.Setup(r => r.FindResourceIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid?)null);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new CheckInBookingCommandRequest(Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(ErrorCodes.BookingNotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WithBookingBelongingToAnotherMember_ThrowsNotFoundException()
    {
        var booking = CreateBooking();
        SetupBooking(booking);
        _currentUserContext.SetupGet(c => c.UserId).Returns(Guid.NewGuid()); // a different member
        _currentUserContext.SetupGet(c => c.Roles).Returns([]);
        _resourceApproverRepository
            .Setup(r => r.FindByResourceAndUserAsync(ResourceId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResourceApprover?)null);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new CheckInBookingCommandRequest(booking.Id), CancellationToken.None));

        Assert.Equal(ErrorCodes.BookingNotFound, exception.ErrorCode);
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_ApproverAssignedToTheResource_CanCheckInAnotherMembersBooking()
    {
        var booking = CreateBooking();
        SetupBooking(booking);
        var approverUserId = Guid.NewGuid();
        _currentUserContext.SetupGet(c => c.UserId).Returns(approverUserId);
        _currentUserContext.SetupGet(c => c.Roles).Returns(["Approver"]);
        _resourceApproverRepository
            .Setup(r => r.FindByResourceAndUserAsync(ResourceId, approverUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceApprover { Id = Guid.NewGuid(), TenantId = TenantId, ResourceId = ResourceId, UserId = approverUserId });
        var sut = CreateSut();

        var result = await sut.Handle(new CheckInBookingCommandRequest(booking.Id), CancellationToken.None);

        Assert.False(result.AlreadyCheckedIn);
    }

    [Fact]
    public async Task Handle_ApproverNotAssignedToTheResource_ThrowsNotFoundException()
    {
        var booking = CreateBooking();
        SetupBooking(booking);
        var unassignedApproverUserId = Guid.NewGuid();
        _currentUserContext.SetupGet(c => c.UserId).Returns(unassignedApproverUserId);
        _currentUserContext.SetupGet(c => c.Roles).Returns(["Approver"]);
        _resourceApproverRepository
            .Setup(r => r.FindByResourceAndUserAsync(ResourceId, unassignedApproverUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResourceApprover?)null);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new CheckInBookingCommandRequest(booking.Id), CancellationToken.None));

        Assert.Equal(ErrorCodes.BookingNotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_TenantAdmin_CanCheckInAnotherMembersBookingInTheirOwnTenant()
    {
        var booking = CreateBooking();
        SetupBooking(booking);
        _currentUserContext.SetupGet(c => c.UserId).Returns(Guid.NewGuid());
        _currentUserContext.SetupGet(c => c.Roles).Returns(["TenantAdmin"]);
        var sut = CreateSut();

        var result = await sut.Handle(new CheckInBookingCommandRequest(booking.Id), CancellationToken.None);

        Assert.False(result.AlreadyCheckedIn);
        // Never needed a resource-approver lookup for an admin - the bypass is unconditional.
        _resourceApproverRepository.Verify(
            r => r.FindByResourceAndUserAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // The global SysAdmin is deliberately NOT treated as privileged here any more (see
    // ApprovalAuthorization.IsTenantAdmin) - it falls through to the same resource-approver check an
    // unassigned Approver hits, and (having no such assignment) gets the identical NotFoundException.
    [Fact]
    public async Task Handle_AsSysAdmin_ThrowsNotFoundExceptionJustLikeAnUnassignedApprover()
    {
        var booking = CreateBooking();
        SetupBooking(booking);
        _currentUserContext.SetupGet(c => c.UserId).Returns(Guid.NewGuid());
        _currentUserContext.SetupGet(c => c.Roles).Returns(["SysAdmin"]);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new CheckInBookingCommandRequest(booking.Id), CancellationToken.None));

        Assert.Equal(ErrorCodes.BookingNotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_RepeatedCheckIn_IsIdempotentAndNeverChangesTheOriginalCheckedInAtUtc()
    {
        var originalCheckedInAtUtc = StartUtc.AddMinutes(-5);
        var booking = CreateBooking(checkedInAtUtc: originalCheckedInAtUtc);
        SetupBooking(booking);
        _currentUserContext.SetupGet(c => c.UserId).Returns(OwnerUserId);
        _currentUserContext.SetupGet(c => c.Roles).Returns([]);
        var sut = CreateSut();

        var result = await sut.Handle(new CheckInBookingCommandRequest(booking.Id), CancellationToken.None);

        Assert.True(result.AlreadyCheckedIn);
        Assert.Equal(originalCheckedInAtUtc, result.CheckedInAtUtc);
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_AlreadyCheckedIn_SucceedsEvenIfTheWindowHasSinceClosed()
    {
        var originalCheckedInAtUtc = StartUtc.AddMinutes(10);
        var booking = CreateBooking(checkedInAtUtc: originalCheckedInAtUtc);
        booking.StartUtc = StartUtc.AddDays(-2); // the window closed long ago
        booking.EndUtc = booking.StartUtc.AddHours(1);
        SetupBooking(booking);
        _currentUserContext.SetupGet(c => c.UserId).Returns(OwnerUserId);
        _currentUserContext.SetupGet(c => c.Roles).Returns([]);
        var sut = CreateSut();

        var result = await sut.Handle(new CheckInBookingCommandRequest(booking.Id), CancellationToken.None);

        Assert.True(result.AlreadyCheckedIn);
        Assert.Equal(originalCheckedInAtUtc, result.CheckedInAtUtc);
    }

    [Theory]
    [InlineData(BookingStatus.Pending)]
    [InlineData(BookingStatus.Rejected)]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.Completed)]
    [InlineData(BookingStatus.NoShow)]
    [InlineData(BookingStatus.Expired)]
    public async Task Handle_BookingNotConfirmed_ThrowsConflictException(BookingStatus status)
    {
        var booking = CreateBooking(status);
        SetupBooking(booking);
        _currentUserContext.SetupGet(c => c.UserId).Returns(OwnerUserId);
        _currentUserContext.SetupGet(c => c.Roles).Returns([]);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new CheckInBookingCommandRequest(booking.Id), CancellationToken.None));

        Assert.Equal(ErrorCodes.BookingCheckInNotAllowed, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_BeforeTheCheckInWindowOpens_ThrowsConflictException()
    {
        var booking = CreateBooking();
        booking.StartUtc = StartUtc.AddHours(2); // window opens 15 minutes before - still 1h45m away
        booking.EndUtc = booking.StartUtc.AddHours(1);
        SetupBooking(booking);
        _currentUserContext.SetupGet(c => c.UserId).Returns(OwnerUserId);
        _currentUserContext.SetupGet(c => c.Roles).Returns([]);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new CheckInBookingCommandRequest(booking.Id), CancellationToken.None));

        Assert.Equal(ErrorCodes.BookingCheckInNotAllowed, exception.ErrorCode);
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_AfterTheGracePeriodCloses_ThrowsConflictException()
    {
        var booking = CreateBooking();
        booking.StartUtc = StartUtc.AddHours(-2); // closes 15 minutes after start - long past that
        booking.EndUtc = booking.StartUtc.AddHours(1);
        SetupBooking(booking);
        _currentUserContext.SetupGet(c => c.UserId).Returns(OwnerUserId);
        _currentUserContext.SetupGet(c => c.Roles).Returns([]);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new CheckInBookingCommandRequest(booking.Id), CancellationToken.None));

        Assert.Equal(ErrorCodes.BookingCheckInNotAllowed, exception.ErrorCode);
        _bookingRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_CrossTenantBooking_NeverFoundSinceFindByIdAsyncIsTenantFiltered()
    {
        // FindResourceIdAsync/FindByIdAsync are tenant-filtered in the real repository (the DbContext's
        // global query filter) - a cross-tenant id is never found at all, proven here the same way
        // CancelBookingCommandHandlerTests proves it: simulating "not found".
        _bookingRepository.Setup(r => r.FindResourceIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid?)null);
        var sut = CreateSut();

        await Assert.ThrowsAsync<NotFoundException>(() => sut.Handle(new CheckInBookingCommandRequest(Guid.NewGuid()), CancellationToken.None));
    }
}
