using BookSpace.Application.Common;
using BookSpace.Application.Resources;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Resources;

public sealed class GetResourceQueryHandlerTests
{
    private readonly Mock<IResourceRepository> _resourceRepository = new();

    private GetResourceQueryHandler CreateSut() => new(_resourceRepository.Object);

    [Fact]
    public async Task Handle_WithExistingResource_ReturnsResponse()
    {
        var resource = new Resource
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            ResourceTypeId = Guid.NewGuid(),
            Name = "Desk 1",
            Capacity = 1,
            Status = ResourceStatus.Active,
            TimeZoneId = "UTC",
        };
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        var sut = CreateSut();

        var result = await sut.Handle(new GetResourceQuery(resource.Id), CancellationToken.None);

        Assert.Equal(resource.Id, result.Id);
        Assert.Equal(resource.Name, result.Name);
    }

    [Fact]
    public async Task Handle_WithUnknownResource_ThrowsNotFoundException()
    {
        _resourceRepository.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Resource?)null);
        var sut = CreateSut();

        await Assert.ThrowsAsync<NotFoundException>(() => sut.Handle(new GetResourceQuery(Guid.NewGuid()), CancellationToken.None));
    }
}
