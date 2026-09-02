using BookSpace.Application.Common;
using BookSpace.Application.Resources;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Resources;

public sealed class GetResourceApproversQueryHandlerTests
{
    private readonly Mock<IResourceApproverRepository> _resourceApproverRepository = new();
    private readonly Mock<IResourceRepository> _resourceRepository = new();

    private GetResourceApproversQueryHandler CreateSut() =>
        new(_resourceApproverRepository.Object, _resourceRepository.Object);

    [Fact]
    public async Task Handle_WithExistingResource_ReturnsMappedApprovers()
    {
        var resource = new Resource { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceTypeId = Guid.NewGuid(), Name = "Desk 1", Capacity = 1, Status = ResourceStatus.Active, TimeZoneId = "UTC" };
        var approver = new ResourceApprover { Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id, UserId = Guid.NewGuid() };
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _resourceApproverRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([approver]);
        var sut = CreateSut();

        var result = await sut.Handle(new GetResourceApproversQueryRequest(resource.Id), CancellationToken.None);

        var item = Assert.Single(result);
        Assert.Equal(approver.Id, item.Id);
        Assert.Equal(approver.UserId, item.UserId);
    }

    [Fact]
    public async Task Handle_WithUnknownResource_ThrowsNotFoundException()
    {
        _resourceRepository.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Resource?)null);
        var sut = CreateSut();

        await Assert.ThrowsAsync<NotFoundException>(() => sut.Handle(new GetResourceApproversQueryRequest(Guid.NewGuid()), CancellationToken.None));
    }
}
