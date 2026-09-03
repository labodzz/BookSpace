using BookSpace.Application.Common;
using BookSpace.Application.ResourceTypes;
using BookSpace.Domain.Entities;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.ResourceTypes;

public sealed class UpdateResourceTypeCommandHandlerTests
{
    private readonly Mock<IResourceTypeRepository> _resourceTypeRepository = new();

    private UpdateResourceTypeCommandHandler CreateSut() => new(_resourceTypeRepository.Object);

    private static ResourceType CreateResourceType(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        Name = "Meeting Room",
    };

    [Fact]
    public async Task Handle_WithValidCommand_RenamesAndSaves()
    {
        var resourceType = CreateResourceType();
        _resourceTypeRepository.Setup(r => r.FindByIdAsync(resourceType.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resourceType);
        _resourceTypeRepository.Setup(r => r.ExistsByNameAsync("Boardroom", resourceType.Id, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var sut = CreateSut();

        var result = await sut.Handle(new UpdateResourceTypeCommandRequest(resourceType.Id, "Boardroom"), CancellationToken.None);

        Assert.Equal("Boardroom", result.Name);
        Assert.Equal("Boardroom", resourceType.Name);
        _resourceTypeRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithUnknownId_ThrowsNotFoundException()
    {
        _resourceTypeRepository.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((ResourceType?)null);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new UpdateResourceTypeCommandRequest(Guid.NewGuid(), "Boardroom"), CancellationToken.None));

        Assert.Equal("Resource.ResourceTypeNotFound", exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WithDuplicateNameInTenant_ThrowsConflictExceptionWithoutSaving()
    {
        var resourceType = CreateResourceType();
        _resourceTypeRepository.Setup(r => r.FindByIdAsync(resourceType.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resourceType);
        _resourceTypeRepository.Setup(r => r.ExistsByNameAsync("Desk", resourceType.Id, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new UpdateResourceTypeCommandRequest(resourceType.Id, "Desk"), CancellationToken.None));

        Assert.Equal("ResourceType.NameConflict", exception.ErrorCode);
        _resourceTypeRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
