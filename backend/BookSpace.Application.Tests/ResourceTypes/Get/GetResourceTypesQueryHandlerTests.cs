using BookSpace.Application.ResourceTypes;
using BookSpace.Domain.Entities;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.ResourceTypes;

public sealed class GetResourceTypesQueryHandlerTests
{
    private readonly Mock<IResourceTypeRepository> _resourceTypeRepository = new();

    private GetResourceTypesQueryHandler CreateSut() => new(_resourceTypeRepository.Object);

    [Fact]
    public async Task Handle_ReturnsResourceTypesOrderedByName()
    {
        var tenantId = Guid.NewGuid();
        _resourceTypeRepository.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new ResourceType { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Meeting Room" },
            new ResourceType { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Desk" },
        ]);
        var sut = CreateSut();

        var result = await sut.Handle(new GetResourceTypesQueryRequest(), CancellationToken.None);

        Assert.Equal(["Desk", "Meeting Room"], result.Select(item => item.Name));
    }
}
