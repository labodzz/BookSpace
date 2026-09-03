using BookSpace.Application.Common;
using BookSpace.Application.ResourceTypes;
using BookSpace.Domain.Entities;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.ResourceTypes;

public sealed class DeleteResourceTypeCommandHandlerTests
{
    private readonly Mock<IResourceTypeRepository> _resourceTypeRepository = new();

    private DeleteResourceTypeCommandHandler CreateSut() => new(_resourceTypeRepository.Object);

    private static ResourceType CreateResourceType(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        Name = "Meeting Room",
    };

    [Fact]
    public async Task Handle_WithUnusedResourceType_RemovesAndSaves()
    {
        var resourceType = CreateResourceType();
        _resourceTypeRepository.Setup(r => r.FindByIdAsync(resourceType.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resourceType);
        _resourceTypeRepository.Setup(r => r.IsReferencedByAnyResourceAsync(resourceType.Id, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var sut = CreateSut();

        await sut.Handle(new DeleteResourceTypeCommandRequest(resourceType.Id), CancellationToken.None);

        _resourceTypeRepository.Verify(r => r.RemoveAsync(resourceType, It.IsAny<CancellationToken>()), Times.Once);
        _resourceTypeRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithUnknownId_ThrowsNotFoundException()
    {
        _resourceTypeRepository.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((ResourceType?)null);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new DeleteResourceTypeCommandRequest(Guid.NewGuid()), CancellationToken.None));

        Assert.Equal("Resource.ResourceTypeNotFound", exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WithResourceTypeStillInUse_ThrowsConflictExceptionWithoutRemoving()
    {
        var resourceType = CreateResourceType();
        _resourceTypeRepository.Setup(r => r.FindByIdAsync(resourceType.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resourceType);
        _resourceTypeRepository.Setup(r => r.IsReferencedByAnyResourceAsync(resourceType.Id, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new DeleteResourceTypeCommandRequest(resourceType.Id), CancellationToken.None));

        Assert.Equal("ResourceType.InUse", exception.ErrorCode);
        _resourceTypeRepository.Verify(r => r.RemoveAsync(It.IsAny<ResourceType>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
