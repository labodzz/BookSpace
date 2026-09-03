using BookSpace.Application.Common;
using BookSpace.Application.ResourceTypes;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.ResourceTypes;

public sealed class CreateResourceTypeCommandHandlerTests
{
    private readonly Mock<IResourceTypeRepository> _resourceTypeRepository = new();
    private readonly Mock<ICurrentUserContext> _currentUserContext = new();

    private CreateResourceTypeCommandHandler CreateSut() => new(_resourceTypeRepository.Object, _currentUserContext.Object);

    [Fact]
    public async Task Handle_WithValidCommand_CreatesAndReturnsResourceType()
    {
        var tenantId = Guid.NewGuid();
        _currentUserContext.SetupGet(c => c.TenantId).Returns(tenantId);
        _resourceTypeRepository.Setup(r => r.ExistsByNameAsync("Meeting Room", null, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var sut = CreateSut();

        var result = await sut.Handle(new CreateResourceTypeCommandRequest("Meeting Room"), CancellationToken.None);

        Assert.Equal("Meeting Room", result.Name);
        _resourceTypeRepository.Verify(r => r.AddAsync(
            It.Is<ResourceType>(type => type.TenantId == tenantId && type.Name == "Meeting Room"),
            It.IsAny<CancellationToken>()), Times.Once);
        _resourceTypeRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithDuplicateNameInTenant_ThrowsConflictExceptionWithoutSaving()
    {
        _currentUserContext.SetupGet(c => c.TenantId).Returns(Guid.NewGuid());
        _resourceTypeRepository.Setup(r => r.ExistsByNameAsync("Desk", null, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new CreateResourceTypeCommandRequest("Desk"), CancellationToken.None));

        Assert.Equal("ResourceType.NameConflict", exception.ErrorCode);
        _resourceTypeRepository.Verify(r => r.AddAsync(It.IsAny<ResourceType>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
