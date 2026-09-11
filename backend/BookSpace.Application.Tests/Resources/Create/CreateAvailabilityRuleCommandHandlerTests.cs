using BookSpace.Application.Common;
using BookSpace.Application.Resources;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Resources;

public sealed class CreateAvailabilityRuleCommandHandlerTests
{
    private readonly Mock<IAvailabilityRuleRepository> _availabilityRuleRepository = new();
    private readonly Mock<IResourceRepository> _resourceRepository = new();
    private readonly Mock<ICurrentUserContext> _currentUserContext = new();

    private CreateAvailabilityRuleCommandHandler CreateSut() =>
        new(_availabilityRuleRepository.Object, _resourceRepository.Object, _currentUserContext.Object);

    private static Resource CreateResource() => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        ResourceTypeId = Guid.NewGuid(),
        Name = "Desk 1",
        Capacity = 1,
        Status = ResourceStatus.Active,
        TimeZoneId = "UTC",
    };

    [Fact]
    public async Task Handle_WithValidCommand_CreatesAndReturnsRule()
    {
        var resource = CreateResource();
        var tenantId = Guid.NewGuid();
        _currentUserContext.SetupGet(c => c.TenantId).Returns(tenantId);
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _availabilityRuleRepository
            .Setup(r => r.ExistsAsync(resource.Id, DayOfWeek.Monday, new TimeOnly(8, 0), new TimeOnly(18, 0), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var sut = CreateSut();
        var request = new CreateAvailabilityRuleCommandRequest(resource.Id, DayOfWeek.Monday, new TimeOnly(8, 0), new TimeOnly(18, 0));

        var result = await sut.Handle(request, CancellationToken.None);

        Assert.Equal(resource.Id, result.ResourceId);
        Assert.Equal(DayOfWeek.Monday, result.DayOfWeek);
        _availabilityRuleRepository.Verify(r => r.AddAsync(
            It.Is<AvailabilityRule>(rule => rule.TenantId == tenantId && rule.ResourceId == resource.Id),
            It.IsAny<CancellationToken>()), Times.Once);
        _availabilityRuleRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithUnknownResource_ThrowsNotFoundExceptionWithoutSaving()
    {
        _resourceRepository.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Resource?)null);
        var sut = CreateSut();
        var request = new CreateAvailabilityRuleCommandRequest(Guid.NewGuid(), DayOfWeek.Monday, new TimeOnly(8, 0), new TimeOnly(18, 0));

        await Assert.ThrowsAsync<NotFoundException>(() => sut.Handle(request, CancellationToken.None));

        _availabilityRuleRepository.Verify(r => r.AddAsync(It.IsAny<AvailabilityRule>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WithExactDuplicateRule_ThrowsConflictExceptionWithoutSaving()
    {
        var resource = CreateResource();
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _availabilityRuleRepository
            .Setup(r => r.ExistsAsync(resource.Id, DayOfWeek.Monday, new TimeOnly(8, 0), new TimeOnly(18, 0), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var sut = CreateSut();
        var request = new CreateAvailabilityRuleCommandRequest(resource.Id, DayOfWeek.Monday, new TimeOnly(8, 0), new TimeOnly(18, 0));

        await Assert.ThrowsAsync<ConflictException>(() => sut.Handle(request, CancellationToken.None));

        _availabilityRuleRepository.Verify(r => r.AddAsync(It.IsAny<AvailabilityRule>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WithArchivedResource_ThrowsConflictExceptionWithoutSaving()
    {
        var resource = CreateResource();
        resource.Status = ResourceStatus.Archived;
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        var sut = CreateSut();
        var request = new CreateAvailabilityRuleCommandRequest(resource.Id, DayOfWeek.Monday, new TimeOnly(8, 0), new TimeOnly(18, 0));

        await Assert.ThrowsAsync<ConflictException>(() => sut.Handle(request, CancellationToken.None));

        _availabilityRuleRepository.Verify(r => r.AddAsync(It.IsAny<AvailabilityRule>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Maintenance is explicitly NOT blocked (per the handler's own code comment: "temporary, and an
    // admin may well want to adjust the schedule while it lasts") - only the rejection path had a test.
    [Fact]
    public async Task Handle_WithMaintenanceResource_StillCreatesTheRule()
    {
        var resource = CreateResource();
        resource.Status = ResourceStatus.Maintenance;
        _currentUserContext.SetupGet(c => c.TenantId).Returns(Guid.NewGuid());
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _availabilityRuleRepository
            .Setup(r => r.ExistsAsync(resource.Id, DayOfWeek.Monday, new TimeOnly(8, 0), new TimeOnly(18, 0), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var sut = CreateSut();
        var request = new CreateAvailabilityRuleCommandRequest(resource.Id, DayOfWeek.Monday, new TimeOnly(8, 0), new TimeOnly(18, 0));

        var result = await sut.Handle(request, CancellationToken.None);

        Assert.Equal(resource.Id, result.ResourceId);
        _availabilityRuleRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
