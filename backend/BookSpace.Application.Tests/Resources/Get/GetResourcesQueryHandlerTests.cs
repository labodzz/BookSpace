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
            Description = "A single desk",
            Capacity = 1,
            RequiresApproval = true,
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
        Assert.Equal(resource.ResourceTypeId, item.ResourceTypeId);
        Assert.Equal(resource.Name, item.Name);
        Assert.Equal(resource.Description, item.Description);
        Assert.Equal(resource.Capacity, item.Capacity);
        Assert.Equal(resource.RequiresApproval, item.RequiresApproval);
        Assert.Equal(resource.Status, item.Status);
        Assert.Equal(resource.TimeZoneId, item.TimeZoneId);
        Assert.Equal(1, result.TotalCount);
    }

    // The test above uses the request's own DEFAULT values (Page=1, PageSize=20, ResourceTypeId=null,
    // Status=null), so it can't tell "correctly forwards the request" apart from "hardcodes 1/20/null/
    // null" - this uses non-default values for every parameter to actually prove pass-through.
    [Fact]
    public async Task Handle_ForwardsNonDefaultPageSizeAndFilterParametersToTheRepository()
    {
        var resourceTypeId = Guid.NewGuid();
        _resourceRepository
            .Setup(r => r.GetPagedAsync(3, 5, resourceTypeId, ResourceStatus.Maintenance, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<Resource>([], 3, 5, 0));
        var sut = CreateSut();

        var result = await sut.Handle(new GetResourcesQueryRequest(Page: 3, PageSize: 5, ResourceTypeId: resourceTypeId, Status: ResourceStatus.Maintenance), CancellationToken.None);

        Assert.Equal(3, result.Page);
        Assert.Equal(5, result.PageSize);
        _resourceRepository.Verify(
            r => r.GetPagedAsync(3, 5, resourceTypeId, ResourceStatus.Maintenance, It.IsAny<CancellationToken>()), Times.Once);
    }
}
