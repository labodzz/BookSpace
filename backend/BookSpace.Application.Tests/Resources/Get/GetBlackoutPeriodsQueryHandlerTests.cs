using BookSpace.Application.Common;
using BookSpace.Application.Resources;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Resources;

public sealed class GetBlackoutPeriodsQueryHandlerTests
{
    private readonly Mock<IBlackoutPeriodRepository> _blackoutPeriodRepository = new();
    private readonly Mock<IResourceRepository> _resourceRepository = new();

    private GetBlackoutPeriodsQueryHandler CreateSut() =>
        new(_blackoutPeriodRepository.Object, _resourceRepository.Object);

    [Fact]
    public async Task Handle_WithExistingResource_ReturnsMappedPeriods()
    {
        var resource = new Resource { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceTypeId = Guid.NewGuid(), Name = "Desk 1", Capacity = 1, Status = ResourceStatus.Active, TimeZoneId = "UTC" };
        var period = new BlackoutPeriod
        {
            Id = Guid.NewGuid(),
            TenantId = resource.TenantId,
            ResourceId = resource.Id,
            StartUtc = DateTimeOffset.UtcNow.AddDays(1),
            EndUtc = DateTimeOffset.UtcNow.AddDays(1).AddHours(1),
            Reason = "Maintenance",
        };
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _blackoutPeriodRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([period]);
        var sut = CreateSut();

        var result = await sut.Handle(new GetBlackoutPeriodsQueryRequest(resource.Id), CancellationToken.None);

        var item = Assert.Single(result);
        Assert.Equal(period.Id, item.Id);
        Assert.Equal(period.ResourceId, item.ResourceId);
        Assert.Equal(period.StartUtc, item.StartUtc);
        Assert.Equal(period.EndUtc, item.EndUtc);
        Assert.Equal(period.Reason, item.Reason);
    }

    [Fact]
    public async Task Handle_WithNoBlackoutPeriods_ReturnsEmptyList()
    {
        var resource = new Resource { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceTypeId = Guid.NewGuid(), Name = "Desk 1", Capacity = 1, Status = ResourceStatus.Active, TimeZoneId = "UTC" };
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _blackoutPeriodRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var sut = CreateSut();

        var result = await sut.Handle(new GetBlackoutPeriodsQueryRequest(resource.Id), CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Handle_WithMultiplePeriods_ReturnsAllOfThem()
    {
        var resource = new Resource { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceTypeId = Guid.NewGuid(), Name = "Desk 1", Capacity = 1, Status = ResourceStatus.Active, TimeZoneId = "UTC" };
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var first = new BlackoutPeriod { Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id, StartUtc = start, EndUtc = start.AddHours(1), Reason = "First" };
        var second = new BlackoutPeriod { Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id, StartUtc = start.AddDays(1), EndUtc = start.AddDays(1).AddHours(1), Reason = "Second" };
        _resourceRepository.Setup(r => r.FindByIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resource);
        _blackoutPeriodRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([first, second]);
        var sut = CreateSut();

        var result = await sut.Handle(new GetBlackoutPeriodsQueryRequest(resource.Id), CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, item => item.Id == first.Id);
        Assert.Contains(result, item => item.Id == second.Id);
    }

    [Fact]
    public async Task Handle_WithUnknownResource_ThrowsNotFoundException()
    {
        _resourceRepository.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Resource?)null);
        var sut = CreateSut();

        await Assert.ThrowsAsync<NotFoundException>(() => sut.Handle(new GetBlackoutPeriodsQueryRequest(Guid.NewGuid()), CancellationToken.None));
    }
}
