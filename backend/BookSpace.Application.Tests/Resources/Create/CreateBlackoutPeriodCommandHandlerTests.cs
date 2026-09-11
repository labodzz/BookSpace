using BookSpace.Application.Common;
using BookSpace.Application.Resources;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Resources;

public sealed class CreateBlackoutPeriodCommandHandlerTests
{
    private readonly Mock<IBlackoutPeriodRepository> _blackoutPeriodRepository = new();
    private readonly Mock<IResourceRepository> _resourceRepository = new();
    private readonly Mock<IBookingAvailabilityRepository> _bookingAvailabilityRepository = new();
    private readonly Mock<ICurrentUserContext> _currentUserContext = new();

    private CreateBlackoutPeriodCommandHandler CreateSut() =>
        new(_blackoutPeriodRepository.Object, _resourceRepository.Object, _bookingAvailabilityRepository.Object, _currentUserContext.Object);

    private static Resource CreateResource() => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        ResourceTypeId = Guid.NewGuid(),
        Name = "Conference Room A",
        Capacity = 8,
        Status = ResourceStatus.Active,
        TimeZoneId = "UTC",
    };

    [Fact]
    public async Task Handle_WithValidCommand_CreatesAndReturnsPeriod()
    {
        var resource = CreateResource();
        var tenantId = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var end = start.AddHours(2);
        _currentUserContext.SetupGet(c => c.TenantId).Returns(tenantId);
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _bookingAvailabilityRepository
            .Setup(r => r.GetActiveBookingsAsync(resource.Id, It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Booking>)[]);
        var sut = CreateSut();
        var request = new CreateBlackoutPeriodCommandRequest(resource.Id, start, end, "Projector maintenance");

        var result = await sut.Handle(request, CancellationToken.None);

        Assert.Equal(resource.Id, result.ResourceId);
        Assert.Equal("Projector maintenance", result.Reason);
        Assert.Empty(result.ConflictingBookingIds);
        _blackoutPeriodRepository.Verify(r => r.AddAsync(
            It.Is<BlackoutPeriod>(period => period.TenantId == tenantId && period.ResourceId == resource.Id),
            It.IsAny<CancellationToken>()), Times.Once);
        _blackoutPeriodRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_OverlappingAnExistingActiveBooking_StillCreatesButReportsTheConflict()
    {
        var resource = CreateResource();
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var end = start.AddHours(2);
        var overlappingBooking = new Booking
        {
            Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id, UserId = Guid.NewGuid(),
            StartUtc = start, EndUtc = end, Quantity = 1, Status = BookingStatus.Confirmed, CreatedAtUtc = start,
        };
        _currentUserContext.SetupGet(c => c.TenantId).Returns(resource.TenantId);
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _bookingAvailabilityRepository
            .Setup(r => r.GetActiveBookingsAsync(resource.Id, It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([overlappingBooking]);
        var sut = CreateSut();
        var request = new CreateBlackoutPeriodCommandRequest(resource.Id, start, end, "Emergency maintenance");

        var result = await sut.Handle(request, CancellationToken.None);

        // Never blocked - see docs/recurring-bookings-and-approvals.md for why - but always reported.
        _blackoutPeriodRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains(overlappingBooking.Id, result.ConflictingBookingIds);
    }

    [Fact]
    public async Task Handle_WithUnknownResource_ThrowsNotFoundExceptionWithoutSaving()
    {
        _resourceRepository.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Resource?)null);
        var sut = CreateSut();
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var request = new CreateBlackoutPeriodCommandRequest(Guid.NewGuid(), start, start.AddHours(1), "Maintenance");

        var exception = await Assert.ThrowsAsync<NotFoundException>(() => sut.Handle(request, CancellationToken.None));

        Assert.Equal("Resource.NotFound", exception.ErrorCode);
        _blackoutPeriodRepository.Verify(r => r.AddAsync(It.IsAny<BlackoutPeriod>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // The Archived-is-terminal branch - identical reasoning/code shape to
    // CreateAvailabilityRuleCommandHandler's own check - had no test at all before this, despite a
    // dedicated code comment explaining the design intent.
    [Fact]
    public async Task Handle_WithArchivedResource_ThrowsConflictExceptionWithoutSaving()
    {
        var resource = CreateResource();
        resource.Status = ResourceStatus.Archived;
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        var sut = CreateSut();
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var request = new CreateBlackoutPeriodCommandRequest(resource.Id, start, start.AddHours(1), "Maintenance");

        await Assert.ThrowsAsync<ConflictException>(() => sut.Handle(request, CancellationToken.None));

        _blackoutPeriodRepository.Verify(r => r.AddAsync(It.IsAny<BlackoutPeriod>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
