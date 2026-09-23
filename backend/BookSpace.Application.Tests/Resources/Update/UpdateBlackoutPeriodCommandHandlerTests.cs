using BookSpace.Application.Common;
using BookSpace.Application.Resources;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Resources;

public sealed class UpdateBlackoutPeriodCommandHandlerTests
{
    private readonly Mock<IBlackoutPeriodRepository> _blackoutPeriodRepository = new();
    private readonly Mock<IResourceRepository> _resourceRepository = new();

    // Every successful update re-checks for conflicting bookings against the new window - defaults to
    // "none" here so existing tests don't each need to set this up individually; the dedicated conflict
    // test below overrides it explicitly.
    private readonly Mock<IBookingAvailabilityRepository> _bookingAvailabilityRepository = CreateBookingAvailabilityRepositoryMock();

    private UpdateBlackoutPeriodCommandHandler CreateSut() =>
        new(_blackoutPeriodRepository.Object, _resourceRepository.Object, _bookingAvailabilityRepository.Object);

    private static Mock<IBookingAvailabilityRepository> CreateBookingAvailabilityRepositoryMock()
    {
        var mock = new Mock<IBookingAvailabilityRepository>();
        mock.Setup(r => r.GetActiveBookingsAsync(It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Booking>)[]);
        return mock;
    }

    private static BlackoutPeriod CreatePeriod(Guid resourceId) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        ResourceId = resourceId,
        StartUtc = DateTimeOffset.UtcNow.AddDays(1),
        EndUtc = DateTimeOffset.UtcNow.AddDays(1).AddHours(2),
        Reason = "Old reason",
    };

    private static Resource CreateResource(Guid id, ResourceStatus status = ResourceStatus.Active) => new()
    {
        Id = id,
        TenantId = Guid.NewGuid(),
        ResourceTypeId = Guid.NewGuid(),
        Name = "Desk 1",
        Capacity = 1,
        RequiresApproval = false,
        Status = status,
        TimeZoneId = "UTC",
    };

    // Every test below that reaches the period lookup needs a found, non-Archived resource first - set
    // up once here so each test only overrides what it specifically cares about.
    private void SetUpActiveResource(Guid resourceId) =>
        _resourceRepository.Setup(r => r.FindByIdAsync(resourceId, It.IsAny<CancellationToken>())).ReturnsAsync(CreateResource(resourceId));

    [Fact]
    public async Task Handle_WithMatchingPeriod_UpdatesAndSaves()
    {
        var resourceId = Guid.NewGuid();
        var period = CreatePeriod(resourceId);
        var newStart = DateTimeOffset.UtcNow.AddDays(2);
        var newEnd = newStart.AddHours(3);
        SetUpActiveResource(resourceId);
        _blackoutPeriodRepository.Setup(r => r.FindByIdAsync(period.Id, It.IsAny<CancellationToken>())).ReturnsAsync(period);
        var sut = CreateSut();

        var result = await sut.Handle(new UpdateBlackoutPeriodCommandRequest(resourceId, period.Id, newStart, newEnd, "New reason"), CancellationToken.None);

        Assert.Equal(newStart, result.StartUtc);
        Assert.Equal(newEnd, result.EndUtc);
        Assert.Equal("New reason", result.Reason);
        _blackoutPeriodRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_MovedIntoOverlapWithAnExistingActiveBooking_StillUpdatesButReportsTheConflict()
    {
        var resourceId = Guid.NewGuid();
        var period = CreatePeriod(resourceId);
        var newStart = DateTimeOffset.UtcNow.AddDays(2);
        var newEnd = newStart.AddHours(3);
        var overlappingBooking = new Booking
        {
            Id = Guid.NewGuid(), TenantId = period.TenantId, ResourceId = resourceId, UserId = Guid.NewGuid(),
            StartUtc = newStart, EndUtc = newEnd, Quantity = 1, Status = BookingStatus.Confirmed, CreatedAtUtc = newStart,
        };
        SetUpActiveResource(resourceId);
        _blackoutPeriodRepository.Setup(r => r.FindByIdAsync(period.Id, It.IsAny<CancellationToken>())).ReturnsAsync(period);
        _bookingAvailabilityRepository
            .Setup(r => r.GetActiveBookingsAsync(resourceId, It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([overlappingBooking]);
        var sut = CreateSut();

        var result = await sut.Handle(new UpdateBlackoutPeriodCommandRequest(resourceId, period.Id, newStart, newEnd, "New reason"), CancellationToken.None);

        _blackoutPeriodRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains(overlappingBooking.Id, result.ConflictingBookingIds);
    }

    [Fact]
    public async Task Handle_WithUnknownResource_ThrowsNotFoundException()
    {
        var resourceId = Guid.NewGuid();
        var sut = CreateSut();
        var start = DateTimeOffset.UtcNow.AddDays(1);

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new UpdateBlackoutPeriodCommandRequest(resourceId, Guid.NewGuid(), start, start.AddHours(1), "Reason"), CancellationToken.None));

        Assert.Equal("Resource.NotFound", exception.ErrorCode);
        _blackoutPeriodRepository.Verify(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Genuine gap found while reconstructing this coverage: every sibling handler
    // (CreateBlackoutPeriodCommandHandler, AssignResourceApproverCommandHandler,
    // UpdateResourceCommandHandler) rejects edits against an Archived resource - Update was missing the
    // same ResourceGuard.EnsureNotArchived call, so this used to be the one way to edit something on an
    // effectively-deleted resource.
    [Fact]
    public async Task Handle_ForAnArchivedResource_ThrowsConflictExceptionWithoutLookingUpThePeriod()
    {
        var resourceId = Guid.NewGuid();
        _resourceRepository.Setup(r => r.FindByIdAsync(resourceId, It.IsAny<CancellationToken>())).ReturnsAsync(CreateResource(resourceId, ResourceStatus.Archived));
        var sut = CreateSut();
        var start = DateTimeOffset.UtcNow.AddDays(1);

        await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new UpdateBlackoutPeriodCommandRequest(resourceId, Guid.NewGuid(), start, start.AddHours(1), "Reason"), CancellationToken.None));

        _blackoutPeriodRepository.Verify(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _blackoutPeriodRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WithUnknownPeriod_ThrowsNotFoundException()
    {
        var resourceId = Guid.NewGuid();
        SetUpActiveResource(resourceId);
        _blackoutPeriodRepository.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((BlackoutPeriod?)null);
        var sut = CreateSut();
        var start = DateTimeOffset.UtcNow.AddDays(1);

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new UpdateBlackoutPeriodCommandRequest(resourceId, Guid.NewGuid(), start, start.AddHours(1), "Reason"), CancellationToken.None));

        Assert.Equal("BlackoutPeriod.NotFound", exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WithPeriodBelongingToADifferentResource_ThrowsNotFoundException()
    {
        var resourceId = Guid.NewGuid();
        var period = CreatePeriod(Guid.NewGuid());
        SetUpActiveResource(resourceId);
        _blackoutPeriodRepository.Setup(r => r.FindByIdAsync(period.Id, It.IsAny<CancellationToken>())).ReturnsAsync(period);
        var sut = CreateSut();
        var start = DateTimeOffset.UtcNow.AddDays(1);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new UpdateBlackoutPeriodCommandRequest(resourceId, period.Id, start, start.AddHours(1), "Reason"), CancellationToken.None));

        _blackoutPeriodRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // The six cases below trace the backdating rule exhaustively: reject only if the new StartUtc is
    // BOTH earlier than the currently-stored StartUtc AND itself in the past. Moving a value that's
    // already in the past is fine as long as it isn't pushed EARLIER; moving a future value earlier is
    // fine as long as it doesn't cross into the past.

    [Fact]
    public async Task Handle_EditingReasonOnlyOnAPastBlackout_Succeeds()
    {
        var resourceId = Guid.NewGuid();
        var period = CreatePeriod(resourceId);
        period.StartUtc = DateTimeOffset.UtcNow.AddDays(-2);
        period.EndUtc = DateTimeOffset.UtcNow.AddDays(-1);
        SetUpActiveResource(resourceId);
        _blackoutPeriodRepository.Setup(r => r.FindByIdAsync(period.Id, It.IsAny<CancellationToken>())).ReturnsAsync(period);
        var sut = CreateSut();

        var result = await sut.Handle(
            new UpdateBlackoutPeriodCommandRequest(resourceId, period.Id, period.StartUtc, period.EndUtc, "Corrected reason"), CancellationToken.None);

        Assert.Equal("Corrected reason", result.Reason);
        Assert.Equal(period.StartUtc, result.StartUtc);
    }

    [Fact]
    public async Task Handle_MovingAPastBlackoutFurtherIntoThePast_ThrowsConflictException()
    {
        var resourceId = Guid.NewGuid();
        var period = CreatePeriod(resourceId);
        period.StartUtc = DateTimeOffset.UtcNow.AddDays(-2);
        period.EndUtc = DateTimeOffset.UtcNow.AddDays(-1);
        SetUpActiveResource(resourceId);
        _blackoutPeriodRepository.Setup(r => r.FindByIdAsync(period.Id, It.IsAny<CancellationToken>())).ReturnsAsync(period);
        var sut = CreateSut();
        var furtherBack = period.StartUtc.AddDays(-5);

        await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new UpdateBlackoutPeriodCommandRequest(resourceId, period.Id, furtherBack, period.EndUtc, "Reason"), CancellationToken.None));
        _blackoutPeriodRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_MovingAPastBlackoutForwardTowardsNow_Succeeds()
    {
        var resourceId = Guid.NewGuid();
        var period = CreatePeriod(resourceId);
        period.StartUtc = DateTimeOffset.UtcNow.AddDays(-5);
        period.EndUtc = DateTimeOffset.UtcNow.AddDays(-4);
        SetUpActiveResource(resourceId);
        _blackoutPeriodRepository.Setup(r => r.FindByIdAsync(period.Id, It.IsAny<CancellationToken>())).ReturnsAsync(period);
        var sut = CreateSut();
        var laterButStillPast = period.StartUtc.AddDays(2);

        var result = await sut.Handle(
            new UpdateBlackoutPeriodCommandRequest(resourceId, period.Id, laterButStillPast, period.EndUtc.AddDays(2), "Reason"), CancellationToken.None);

        Assert.Equal(laterButStillPast, result.StartUtc);
    }

    [Fact]
    public async Task Handle_MovingAFutureBlackoutEarlierButStillFuture_Succeeds()
    {
        var resourceId = Guid.NewGuid();
        var period = CreatePeriod(resourceId);
        period.StartUtc = DateTimeOffset.UtcNow.AddDays(30);
        period.EndUtc = DateTimeOffset.UtcNow.AddDays(31);
        SetUpActiveResource(resourceId);
        _blackoutPeriodRepository.Setup(r => r.FindByIdAsync(period.Id, It.IsAny<CancellationToken>())).ReturnsAsync(period);
        var sut = CreateSut();
        var earlierButStillFuture = DateTimeOffset.UtcNow.AddDays(7);

        var result = await sut.Handle(
            new UpdateBlackoutPeriodCommandRequest(resourceId, period.Id, earlierButStillFuture, earlierButStillFuture.AddHours(2), "Reason"),
            CancellationToken.None);

        Assert.Equal(earlierButStillFuture, result.StartUtc);
    }

    [Fact]
    public async Task Handle_MovingAFutureBlackoutIntoThePast_ThrowsConflictException()
    {
        var resourceId = Guid.NewGuid();
        var period = CreatePeriod(resourceId);
        period.StartUtc = DateTimeOffset.UtcNow.AddDays(7);
        period.EndUtc = DateTimeOffset.UtcNow.AddDays(8);
        SetUpActiveResource(resourceId);
        _blackoutPeriodRepository.Setup(r => r.FindByIdAsync(period.Id, It.IsAny<CancellationToken>())).ReturnsAsync(period);
        var sut = CreateSut();
        var intoThePast = DateTimeOffset.UtcNow.AddDays(-1);

        await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new UpdateBlackoutPeriodCommandRequest(resourceId, period.Id, intoThePast, intoThePast.AddHours(2), "Reason"), CancellationToken.None));
        _blackoutPeriodRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
