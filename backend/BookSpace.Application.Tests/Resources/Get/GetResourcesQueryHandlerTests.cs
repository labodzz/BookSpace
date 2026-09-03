using BookSpace.Application.Common;
using BookSpace.Application.Resources;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Resources;

public sealed class GetResourcesQueryHandlerTests
{
    private readonly Mock<IResourceRepository> _resourceRepository = new();

    private GetResourcesQueryHandler CreateSut() => new(_resourceRepository.Object);

    [Fact]
    public async Task Handle_MapsPagedResourcesToResponses()
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
        _resourceRepository
            .Setup(r => r.GetPagedAsync(1, 20, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<Resource>([resource], 1, 20, 1));
        var sut = CreateSut();

        var result = await sut.Handle(new GetResourcesQueryRequest(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(resource.Id, item.Id);
        Assert.Equal(1, result.TotalCount);
    }
}
