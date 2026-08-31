using BookSpace.Application.Common;
using BookSpace.Application.Resources;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Resources;

public sealed class DeleteResourceCommandHandlerTests
{
    private readonly Mock<IResourceRepository> _resourceRepository = new();

    private DeleteResourceCommandHandler CreateSut() => new(_resourceRepository.Object);

    private static Resource CreateResource(ResourceStatus status = ResourceStatus.Active) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        ResourceTypeId = Guid.NewGuid(),
        Name = "Desk 1",
        Capacity = 1,
        Status = status,
        TimeZoneId = "UTC",
    };

    [Fact]
    public async Task Handle_WithActiveResource_ArchivesAndSaves()
    {
        var resource = CreateResource();
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        var sut = CreateSut();

        var result = await sut.Handle(new DeleteResourceCommand(resource.Id), CancellationToken.None);

        Assert.Equal(ResourceStatus.Archived, result.Status);
        Assert.Equal(ResourceStatus.Archived, resource.Status);
        _resourceRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithAlreadyArchivedResource_IsIdempotentAndDoesNotSaveAgain()
    {
        var resource = CreateResource(ResourceStatus.Archived);
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        var sut = CreateSut();

        var result = await sut.Handle(new DeleteResourceCommand(resource.Id), CancellationToken.None);

        Assert.Equal(ResourceStatus.Archived, result.Status);
        _resourceRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WithUnknownResource_ThrowsNotFoundException()
    {
        _resourceRepository.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Resource?)null);
        var sut = CreateSut();

        await Assert.ThrowsAsync<NotFoundException>(() => sut.Handle(new DeleteResourceCommand(Guid.NewGuid()), CancellationToken.None));
    }
}
