using BookSpace.Application.Common;
using BookSpace.Application.Resources;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Resources;

public sealed class UpdateResourceCommandHandlerTests
{
    private readonly Mock<IResourceRepository> _resourceRepository = new();
    private readonly Mock<IBookingAvailabilityRepository> _bookingAvailabilityRepository = new();

    private UpdateResourceCommandHandler CreateSut() => new(_resourceRepository.Object, _bookingAvailabilityRepository.Object);

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
    }

    [Fact]
    public async Task Handle_WithUnknownResource_ThrowsNotFoundException()
    {
        _resourceRepository.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Resource?)null);
        var sut = CreateSut();
        var request = new UpdateResourceCommandRequest(Guid.NewGuid(), Guid.NewGuid(), "Name", null, 1, false, "UTC", ResourceStatus.Active);

        await Assert.ThrowsAsync<NotFoundException>(() => sut.Handle(request, CancellationToken.None));
    }

    [Fact]
    public async Task Handle_WithUnknownResourceType_ThrowsNotFoundException()
    {
        var resource = CreateResource();
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _resourceRepository.Setup(r => r.ResourceTypeExistsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var sut = CreateSut();
        var request = new UpdateResourceCommandRequest(resource.Id, Guid.NewGuid(), "Name", null, 1, false, "UTC", ResourceStatus.Active);

        await Assert.ThrowsAsync<NotFoundException>(() => sut.Handle(request, CancellationToken.None));
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

        await Assert.ThrowsAsync<ConflictException>(() => sut.Handle(request, CancellationToken.None));
    }
}
