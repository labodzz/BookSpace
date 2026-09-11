using BookSpace.Application.Common;
using BookSpace.Application.Resources;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Resources;

public sealed class GetAvailabilityRulesQueryHandlerTests
{
    private readonly Mock<IAvailabilityRuleRepository> _availabilityRuleRepository = new();
    private readonly Mock<IResourceRepository> _resourceRepository = new();

    private GetAvailabilityRulesQueryHandler CreateSut() =>
        new(_availabilityRuleRepository.Object, _resourceRepository.Object);

    [Fact]
    public async Task Handle_WithExistingResource_ReturnsMappedRules()
    {
        var resource = new Resource { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceTypeId = Guid.NewGuid(), Name = "Desk 1", Capacity = 1, Status = ResourceStatus.Active, TimeZoneId = "UTC" };
        var rule = new AvailabilityRule { Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id, DayOfWeek = DayOfWeek.Monday, StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(18, 0) };
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _availabilityRuleRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([rule]);
        var sut = CreateSut();

        var result = await sut.Handle(new GetAvailabilityRulesQueryRequest(resource.Id), CancellationToken.None);

        var item = Assert.Single(result);
        Assert.Equal(rule.Id, item.Id);
        Assert.Equal(rule.ResourceId, item.ResourceId);
        Assert.Equal(rule.DayOfWeek, item.DayOfWeek);
        Assert.Equal(rule.StartTime, item.StartTime);
        Assert.Equal(rule.EndTime, item.EndTime);
    }

    [Fact]
    public async Task Handle_WithNoRules_ReturnsEmptyList()
    {
        var resource = new Resource { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceTypeId = Guid.NewGuid(), Name = "Desk 1", Capacity = 1, Status = ResourceStatus.Active, TimeZoneId = "UTC" };
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _availabilityRuleRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var sut = CreateSut();

        var result = await sut.Handle(new GetAvailabilityRulesQueryRequest(resource.Id), CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Handle_WithMultipleRules_ReturnsAllOfThem()
    {
        var resource = new Resource { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceTypeId = Guid.NewGuid(), Name = "Desk 1", Capacity = 1, Status = ResourceStatus.Active, TimeZoneId = "UTC" };
        var first = new AvailabilityRule { Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id, DayOfWeek = DayOfWeek.Monday, StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(18, 0) };
        var second = new AvailabilityRule { Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id, DayOfWeek = DayOfWeek.Tuesday, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 0) };
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _availabilityRuleRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([first, second]);
        var sut = CreateSut();

        var result = await sut.Handle(new GetAvailabilityRulesQueryRequest(resource.Id), CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, item => item.Id == first.Id);
        Assert.Contains(result, item => item.Id == second.Id);
    }

    [Fact]
    public async Task Handle_WithUnknownResource_ThrowsNotFoundException()
    {
        _resourceRepository.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Resource?)null);
        var sut = CreateSut();

        await Assert.ThrowsAsync<NotFoundException>(() => sut.Handle(new GetAvailabilityRulesQueryRequest(Guid.NewGuid()), CancellationToken.None));
    }
}
