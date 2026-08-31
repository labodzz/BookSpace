using BookSpace.Application.Common;
using BookSpace.Application.Resources;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Resources;

public sealed class CreateResourceCommandHandlerTests
{
    private readonly Mock<IResourceRepository> _resourceRepository = new();
    private readonly Mock<ICurrentUserContext> _currentUserContext = new();

    private CreateResourceCommandHandler CreateSut() => new(_resourceRepository.Object, _currentUserContext.Object);

    [Fact]
    public async Task Handle_WithValidCommand_CreatesAndReturnsResource()
    {
        var tenantId = Guid.NewGuid();
        var resourceTypeId = Guid.NewGuid();
        _currentUserContext.SetupGet(c => c.TenantId).Returns(tenantId);
        _resourceRepository.Setup(r => r.ResourceTypeExistsAsync(resourceTypeId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _resourceRepository.Setup(r => r.ExistsByNameAsync("Conference Room A", null, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var sut = CreateSut();
        var command = new CreateResourceCommand(resourceTypeId, "Conference Room A", "8-seat room", 8, true, "UTC");

        var result = await sut.Handle(command, CancellationToken.None);

        Assert.Equal("Conference Room A", result.Name);
        Assert.Equal(ResourceStatus.Active, result.Status);
        Assert.Equal(resourceTypeId, result.ResourceTypeId);
        _resourceRepository.Verify(r => r.AddAsync(
            It.Is<Resource>(res => res.TenantId == tenantId && res.Name == "Conference Room A" && res.Status == ResourceStatus.Active),
            It.IsAny<CancellationToken>()), Times.Once);
        _resourceRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithUnknownResourceType_ThrowsNotFoundExceptionWithoutSaving()
    {
        _currentUserContext.SetupGet(c => c.TenantId).Returns(Guid.NewGuid());
        _resourceRepository.Setup(r => r.ResourceTypeExistsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var sut = CreateSut();
        var command = new CreateResourceCommand(Guid.NewGuid(), "Desk 1", null, 1, false, "UTC");

        await Assert.ThrowsAsync<NotFoundException>(() => sut.Handle(command, CancellationToken.None));

        _resourceRepository.Verify(r => r.AddAsync(It.IsAny<Resource>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WithDuplicateNameInTenant_ThrowsConflictExceptionWithoutSaving()
    {
        _currentUserContext.SetupGet(c => c.TenantId).Returns(Guid.NewGuid());
        _resourceRepository.Setup(r => r.ResourceTypeExistsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _resourceRepository.Setup(r => r.ExistsByNameAsync("Desk 1", null, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var sut = CreateSut();
        var command = new CreateResourceCommand(Guid.NewGuid(), "Desk 1", null, 1, false, "UTC");

        await Assert.ThrowsAsync<ConflictException>(() => sut.Handle(command, CancellationToken.None));

        _resourceRepository.Verify(r => r.AddAsync(It.IsAny<Resource>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
