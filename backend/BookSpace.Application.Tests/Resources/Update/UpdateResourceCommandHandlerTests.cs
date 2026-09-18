using BookSpace.Application.Bookings;
using BookSpace.Application.Common;
using BookSpace.Application.Resources;
using BookSpace.Application.Tests.Bookings;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Resources;

public sealed class UpdateResourceCommandHandlerTests
{
    private readonly Mock<IResourceRepository> _resourceRepository = new();
    private readonly Mock<IBookingAvailabilityRepository> _bookingAvailabilityRepository = new();

    // The lock is a pass-through here (no real DB/transaction in a handler test) - the actual
    // capacity-reduction-vs-booking-creation race this lock closes is proven against real SQL Server in
    // BookSpace.Infrastructure.Tests (UpdateResourceCapacityConcurrencyTests).
    private UpdateResourceCommandHandler CreateSut() =>
        new(new PassThroughResourceBookingLock(), _resourceRepository.Object, _bookingAvailabilityRepository.Object);

    private static Resource CreateResource(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        ResourceTypeId = Guid.NewGuid(),
        Name = "Old Name",
        Description = "Old description",
        Capacity = 4,
        RequiresApproval = false,
        Status = ResourceStatus.Active,
        TimeZoneId = "UTC",
    };

    [Fact]
    public async Task Handle_WithValidCommand_UpdatesAndReturnsResource()
    {
        var resource = CreateResource();
        var newResourceTypeId = Guid.NewGuid();
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _resourceRepository.Setup(r => r.ResourceTypeExistsAsync(newResourceTypeId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _resourceRepository.Setup(r => r.ExistsByNameAsync("New Name", resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var sut = CreateSut();
        var request = new UpdateResourceCommandRequest(resource.Id, newResourceTypeId, "New Name", "New description", 10, true, "UTC", ResourceStatus.Maintenance);

        var result = await sut.Handle(request, CancellationToken.None);

        Assert.Equal("New Name", result.Name);
        Assert.Equal(ResourceStatus.Maintenance, result.Status);
        Assert.Equal(newResourceTypeId, result.ResourceTypeId);
        _resourceRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        // Capacity increased (4 -> 10) - the peak-demand re-check only applies to a reduction, and this
        // was previously only implied by the test above succeeding, never directly asserted as skipped.
        _bookingAvailabilityRepository.Verify(
            r => r.GetActiveBookingsAsync(It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_WithCapacityUnchanged_SkipsThePeakDemandCheck()
    {
        var resource = CreateResource(); // Capacity = 4
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _resourceRepository.Setup(r => r.ResourceTypeExistsAsync(resource.ResourceTypeId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _resourceRepository.Setup(r => r.ExistsByNameAsync(resource.Name, resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var sut = CreateSut();
        var request = new UpdateResourceCommandRequest(resource.Id, resource.ResourceTypeId, resource.Name, resource.Description, 4, false, "UTC", ResourceStatus.Active);

        await sut.Handle(request, CancellationToken.None);

        _bookingAvailabilityRepository.Verify(
            r => r.GetActiveBookingsAsync(It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_WithUnknownResource_ThrowsNotFoundException()
    {
        _resourceRepository.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Resource?)null);
        var sut = CreateSut();
        var request = new UpdateResourceCommandRequest(Guid.NewGuid(), Guid.NewGuid(), "Name", null, 1, false, "UTC", ResourceStatus.Active);

        var exception = await Assert.ThrowsAsync<NotFoundException>(() => sut.Handle(request, CancellationToken.None));

        Assert.Equal(ErrorCodes.ResourceNotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WithUnknownResourceType_ThrowsNotFoundException()
    {
        var resource = CreateResource();
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _resourceRepository.Setup(r => r.ResourceTypeExistsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var sut = CreateSut();
        var request = new UpdateResourceCommandRequest(resource.Id, Guid.NewGuid(), "Name", null, 1, false, "UTC", ResourceStatus.Active);

        var exception = await Assert.ThrowsAsync<NotFoundException>(() => sut.Handle(request, CancellationToken.None));

        Assert.Equal(ErrorCodes.ResourceTypeNotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WithDuplicateNameOnAnotherResource_ThrowsConflictException()
    {
        var resource = CreateResource();
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _resourceRepository.Setup(r => r.ResourceTypeExistsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _resourceRepository.Setup(r => r.ExistsByNameAsync("Taken Name", resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var sut = CreateSut();
        var request = new UpdateResourceCommandRequest(resource.Id, resource.ResourceTypeId, "Taken Name", null, 1, false, "UTC", ResourceStatus.Active);

        var exception = await Assert.ThrowsAsync<ConflictException>(() => sut.Handle(request, CancellationToken.None));

        Assert.Equal(ErrorCodes.ResourceNameConflict, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WithArchivedResource_ThrowsConflictException()
    {
        var resource = CreateResource();
        resource.Status = ResourceStatus.Archived;
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        var sut = CreateSut();
        var request = new UpdateResourceCommandRequest(resource.Id, resource.ResourceTypeId, "New Name", null, 4, false, "UTC", ResourceStatus.Active);

        await Assert.ThrowsAsync<ConflictException>(() => sut.Handle(request, CancellationToken.None));
        _resourceRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_DecreasingCapacityWithNoActiveBookings_Succeeds()
    {
        var resource = CreateResource();
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _resourceRepository.Setup(r => r.ResourceTypeExistsAsync(resource.ResourceTypeId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _resourceRepository.Setup(r => r.ExistsByNameAsync(resource.Name, resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _bookingAvailabilityRepository
            .Setup(r => r.GetActiveBookingsAsync(resource.Id, It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Booking>)[]);
        var sut = CreateSut();
        var request = new UpdateResourceCommandRequest(resource.Id, resource.ResourceTypeId, resource.Name, null, 1, false, "UTC", ResourceStatus.Active);

        var result = await sut.Handle(request, CancellationToken.None);

        Assert.Equal(1, result.Capacity);
    }

    [Fact]
    public async Task Handle_DecreasingCapacityBelowPeakBookingDemand_ThrowsConflictException()
    {
        var resource = CreateResource();
        resource.Capacity = 8;
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _resourceRepository.Setup(r => r.ResourceTypeExistsAsync(resource.ResourceTypeId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _resourceRepository.Setup(r => r.ExistsByNameAsync(resource.Name, resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var now = DateTimeOffset.UtcNow;
        var overlappingBookings = new List<Booking>
        {
            new() { Id = Guid.NewGuid(), StartUtc = now.AddHours(1), EndUtc = now.AddHours(3), Quantity = 3, Status = BookingStatus.Confirmed },
            new() { Id = Guid.NewGuid(), StartUtc = now.AddHours(2), EndUtc = now.AddHours(4), Quantity = 4, Status = BookingStatus.Pending },
        };
        _bookingAvailabilityRepository
            .Setup(r => r.GetActiveBookingsAsync(resource.Id, It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(overlappingBookings);
        var sut = CreateSut();
        // Peak overlap (hours 2-3) demands 3 + 4 = 7; reducing capacity to 6 must be rejected.
        var request = new UpdateResourceCommandRequest(resource.Id, resource.ResourceTypeId, resource.Name, null, 6, false, "UTC", ResourceStatus.Active);

        await Assert.ThrowsAsync<ConflictException>(() => sut.Handle(request, CancellationToken.None));
        _resourceRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_DecreasingCapacityToExactlyPeakBookingDemand_Succeeds()
    {
        var resource = CreateResource();
        resource.Capacity = 8;
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _resourceRepository.Setup(r => r.ResourceTypeExistsAsync(resource.ResourceTypeId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _resourceRepository.Setup(r => r.ExistsByNameAsync(resource.Name, resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var now = DateTimeOffset.UtcNow;
        var overlappingBookings = new List<Booking>
        {
            new() { Id = Guid.NewGuid(), StartUtc = now.AddHours(1), EndUtc = now.AddHours(3), Quantity = 3, Status = BookingStatus.Confirmed },
            new() { Id = Guid.NewGuid(), StartUtc = now.AddHours(2), EndUtc = now.AddHours(4), Quantity = 4, Status = BookingStatus.Pending },
        };
        _bookingAvailabilityRepository
            .Setup(r => r.GetActiveBookingsAsync(resource.Id, It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(overlappingBookings);
        var sut = CreateSut();
        var request = new UpdateResourceCommandRequest(resource.Id, resource.ResourceTypeId, resource.Name, null, 7, false, "UTC", ResourceStatus.Active);

        var result = await sut.Handle(request, CancellationToken.None);

        Assert.Equal(7, result.Capacity);
    }

    [Fact]
    public async Task Handle_DecreasingCapacityWithBackToBackNonOverlappingBookings_TreatsThemAsNonOverlapping()
    {
        // Regression test for a sweep-line tie-breaking bug: without an explicit "end before start at
        // the same instant" rule, these two back-to-back (touching, not overlapping) bookings could be
        // miscounted as briefly needing 5 + 5 = 10 combined, when in reality one ends exactly as the
        // other begins and neither ever needs capacity at the same moment as the other.
        var resource = CreateResource();
        resource.Capacity = 8;
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _resourceRepository.Setup(r => r.ResourceTypeExistsAsync(resource.ResourceTypeId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _resourceRepository.Setup(r => r.ExistsByNameAsync(resource.Name, resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var now = DateTimeOffset.UtcNow;
        var touchingBookings = new List<Booking>
        {
            new() { Id = Guid.NewGuid(), StartUtc = now.AddHours(1), EndUtc = now.AddHours(2), Quantity = 5, Status = BookingStatus.Confirmed },
            new() { Id = Guid.NewGuid(), StartUtc = now.AddHours(2), EndUtc = now.AddHours(3), Quantity = 5, Status = BookingStatus.Confirmed },
        };
        _bookingAvailabilityRepository
            .Setup(r => r.GetActiveBookingsAsync(resource.Id, It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(touchingBookings);
        var sut = CreateSut();
        // True peak demand is 5 (never both at once); reducing to 5 must succeed even though the naive
        // (buggy) sum of both quantities would be 10.
        var request = new UpdateResourceCommandRequest(resource.Id, resource.ResourceTypeId, resource.Name, null, 5, false, "UTC", ResourceStatus.Active);

        var result = await sut.Handle(request, CancellationToken.None);

        Assert.Equal(5, result.Capacity);
    }

    // Every other test in this file uses PassThroughResourceBookingLock, which ignores its resourceId
    // argument entirely - a bug that passed the wrong id would go undetected by any of them. This is the
    // one test that actually asserts the lock is acquired keyed by the request's own Id.
    [Fact]
    public async Task Handle_AcquiresTheResourceBookingLockKeyedByTheRequestsOwnId()
    {
        var resource = CreateResource();
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _resourceRepository.Setup(r => r.ResourceTypeExistsAsync(resource.ResourceTypeId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _resourceRepository.Setup(r => r.ExistsByNameAsync(resource.Name, resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var lockMock = new Mock<IResourceBookingLock>();
        lockMock
            .Setup(l => l.RunExclusiveAsync(
                It.IsAny<Guid>(), It.IsAny<Func<CancellationToken, Task<UpdateResourceResponse>>>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, Func<CancellationToken, Task<UpdateResourceResponse>>, CancellationToken>((_, operation, ct) => operation(ct));
        var sut = new UpdateResourceCommandHandler(lockMock.Object, _resourceRepository.Object, _bookingAvailabilityRepository.Object);
        var request = new UpdateResourceCommandRequest(
            resource.Id, resource.ResourceTypeId, resource.Name, resource.Description, resource.Capacity, resource.RequiresApproval, resource.TimeZoneId, resource.Status);

        await sut.Handle(request, CancellationToken.None);

        lockMock.Verify(l => l.RunExclusiveAsync(
            resource.Id, It.IsAny<Func<CancellationToken, Task<UpdateResourceResponse>>>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
