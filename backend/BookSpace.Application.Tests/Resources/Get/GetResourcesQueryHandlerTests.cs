using BookSpace.Application.Common;
using BookSpace.Application.Resources;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Resources;

public sealed class GetResourcesQueryHandlerTests
{
    private readonly Mock<IResourceRepository> _resourceRepository = new();
    private readonly Mock<ICurrentUserContext> _currentUserContext = new();

    public GetResourcesQueryHandlerTests()
    {
        // Every existing test in this file predates role-based filtering and exercises the
        // pass-request-status-through-unchanged path - default to a privileged role so none of them need
        // to know about it; the tests specifically exercising the new filtering below override this.
        _currentUserContext.SetupGet(c => c.Roles).Returns(new[] { "TenantAdmin" });
    }

    private GetResourcesQueryHandler CreateSut() => new(_resourceRepository.Object, _currentUserContext.Object);

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

    [Theory]
    [InlineData("Member")]
    [InlineData("Approver")]
    public async Task Handle_AsAPlainMemberOrApprover_ForcesTheStatusFilterToActiveRegardlessOfWhatWasRequested(string role)
    {
        _currentUserContext.SetupGet(c => c.Roles).Returns(new[] { role });
        _resourceRepository
            .Setup(r => r.GetPagedAsync(1, 20, null, ResourceStatus.Active, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<Resource>([], 1, 20, 0));
        var sut = CreateSut();

        // Requests every Archived resource, hoping to see them - a Member/Approver must never actually get
        // anything but Active back, no matter what status the client asks for.
        await sut.Handle(new GetResourcesQueryRequest(Status: ResourceStatus.Archived), CancellationToken.None);

        _resourceRepository.Verify(
            r => r.GetPagedAsync(1, 20, null, ResourceStatus.Active, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_AsTenantAdmin_PassesTheRequestedStatusThroughUnchanged()
    {
        _currentUserContext.SetupGet(c => c.Roles).Returns(new[] { "TenantAdmin" });
        _resourceRepository
            .Setup(r => r.GetPagedAsync(1, 20, null, ResourceStatus.Inactive, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<Resource>([], 1, 20, 0));
        var sut = CreateSut();

        await sut.Handle(new GetResourcesQueryRequest(Status: ResourceStatus.Inactive), CancellationToken.None);

        _resourceRepository.Verify(
            r => r.GetPagedAsync(1, 20, null, ResourceStatus.Inactive, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_AsSysAdmin_PassesTheRequestedStatusThroughUnchanged()
    {
        _currentUserContext.SetupGet(c => c.Roles).Returns(new[] { "SysAdmin" });
        _resourceRepository
            .Setup(r => r.GetPagedAsync(1, 20, null, ResourceStatus.Archived, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<Resource>([], 1, 20, 0));
        var sut = CreateSut();

        await sut.Handle(new GetResourcesQueryRequest(Status: ResourceStatus.Archived), CancellationToken.None);

        _resourceRepository.Verify(
            r => r.GetPagedAsync(1, 20, null, ResourceStatus.Archived, It.IsAny<CancellationToken>()), Times.Once);
    }
}
