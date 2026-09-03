using BookSpace.Application.Common;
using BookSpace.Application.ResourceTypes;
using BookSpace.Domain.Entities;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.ResourceTypes;

public sealed class GetResourceTypeQueryHandlerTests
{
    private readonly Mock<IResourceTypeRepository> _resourceTypeRepository = new();

    private GetResourceTypeQueryHandler CreateSut() => new(_resourceTypeRepository.Object);

    [Fact]
    public async Task Handle_WithExistingId_ReturnsResourceType()
    {
        var resourceType = new ResourceType { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), Name = "Meeting Room" };
        _resourceTypeRepository.Setup(r => r.FindByIdAsync(resourceType.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resourceType);
        var sut = CreateSut();

        var result = await sut.Handle(new GetResourceTypeQueryRequest(resourceType.Id), CancellationToken.None);

        Assert.Equal(resourceType.Id, result.Id);
        Assert.Equal("Meeting Room", result.Name);
    }

    [Fact]
    public async Task Handle_WithUnknownId_ThrowsNotFoundException()
    {
        _resourceTypeRepository.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((ResourceType?)null);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new GetResourceTypeQueryRequest(Guid.NewGuid()), CancellationToken.None));

        Assert.Equal("Resource.ResourceTypeNotFound", exception.ErrorCode);
    }
}
