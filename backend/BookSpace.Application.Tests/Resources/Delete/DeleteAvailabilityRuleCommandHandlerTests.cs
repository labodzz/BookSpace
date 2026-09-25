using BookSpace.Application.Common;
using BookSpace.Application.Resources;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Resources;

public sealed class DeleteAvailabilityRuleCommandHandlerTests
{
    private readonly Mock<IAvailabilityRuleRepository> _availabilityRuleRepository = new();
    private readonly Mock<IResourceRepository> _resourceRepository = new();

    private DeleteAvailabilityRuleCommandHandler CreateSut() => new(_availabilityRuleRepository.Object, _resourceRepository.Object);

    private static Resource CreateResource(Guid resourceId, ResourceStatus status) => new()
    {
        Id = resourceId,
        TenantId = Guid.NewGuid(),
        ResourceTypeId = Guid.NewGuid(),
        Name = "Some Resource",
        Capacity = 1,
        Status = status,
        TimeZoneId = "UTC",
    };

    [Fact]
    public async Task Handle_WithMatchingRuleOnAnInactiveResource_RemovesItAndSaves()
    {
        var resourceId = Guid.NewGuid();
        var rule = new AvailabilityRule { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceId = resourceId, DayOfWeek = DayOfWeek.Monday, StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(18, 0) };
        _availabilityRuleRepository.Setup(r => r.FindByIdAsync(rule.Id, It.IsAny<CancellationToken>())).ReturnsAsync(rule);
        _resourceRepository.Setup(r => r.FindByIdAsync(resourceId, It.IsAny<CancellationToken>())).ReturnsAsync(CreateResource(resourceId, ResourceStatus.Inactive));
        var sut = CreateSut();

        await sut.Handle(new DeleteAvailabilityRuleCommandRequest(resourceId, rule.Id), CancellationToken.None);

        _availabilityRuleRepository.Verify(r => r.RemoveAsync(rule, It.IsAny<CancellationToken>()), Times.Once);
        _availabilityRuleRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_RemovingTheLastRuleOfAnActiveResource_ThrowsConflictExceptionWithoutRemoving()
    {
        var resourceId = Guid.NewGuid();
        var rule = new AvailabilityRule { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceId = resourceId, DayOfWeek = DayOfWeek.Monday, StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(18, 0) };
        _availabilityRuleRepository.Setup(r => r.FindByIdAsync(rule.Id, It.IsAny<CancellationToken>())).ReturnsAsync(rule);
        _resourceRepository.Setup(r => r.FindByIdAsync(resourceId, It.IsAny<CancellationToken>())).ReturnsAsync(CreateResource(resourceId, ResourceStatus.Active));
        _availabilityRuleRepository.Setup(r => r.GetByResourceIdAsync(resourceId, It.IsAny<CancellationToken>())).ReturnsAsync([rule]);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            sut.Handle(new DeleteAvailabilityRuleCommandRequest(resourceId, rule.Id), CancellationToken.None));

        Assert.Equal(ErrorCodes.ResourceAvailabilityRuleRequired, exception.ErrorCode);
        _availabilityRuleRepository.Verify(r => r.RemoveAsync(It.IsAny<AvailabilityRule>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_RemovingOneOfSeveralRulesOfAnActiveResource_Succeeds()
    {
        var resourceId = Guid.NewGuid();
        var rule = new AvailabilityRule { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceId = resourceId, DayOfWeek = DayOfWeek.Monday, StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(18, 0) };
        var otherRule = new AvailabilityRule { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceId = resourceId, DayOfWeek = DayOfWeek.Tuesday, StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(18, 0) };
        _availabilityRuleRepository.Setup(r => r.FindByIdAsync(rule.Id, It.IsAny<CancellationToken>())).ReturnsAsync(rule);
        _resourceRepository.Setup(r => r.FindByIdAsync(resourceId, It.IsAny<CancellationToken>())).ReturnsAsync(CreateResource(resourceId, ResourceStatus.Active));
        _availabilityRuleRepository.Setup(r => r.GetByResourceIdAsync(resourceId, It.IsAny<CancellationToken>())).ReturnsAsync([rule, otherRule]);
        var sut = CreateSut();

        await sut.Handle(new DeleteAvailabilityRuleCommandRequest(resourceId, rule.Id), CancellationToken.None);

        _availabilityRuleRepository.Verify(r => r.RemoveAsync(rule, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithUnknownRule_ThrowsNotFoundException()
    {
        _availabilityRuleRepository.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((AvailabilityRule?)null);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new DeleteAvailabilityRuleCommandRequest(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(ErrorCodes.AvailabilityRuleNotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WithRuleBelongingToADifferentResource_ThrowsNotFoundException()
    {
        var rule = new AvailabilityRule { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceId = Guid.NewGuid(), DayOfWeek = DayOfWeek.Monday, StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(18, 0) };
        _availabilityRuleRepository.Setup(r => r.FindByIdAsync(rule.Id, It.IsAny<CancellationToken>())).ReturnsAsync(rule);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new DeleteAvailabilityRuleCommandRequest(Guid.NewGuid(), rule.Id), CancellationToken.None));

        Assert.Equal(ErrorCodes.AvailabilityRuleNotFound, exception.ErrorCode);
        _availabilityRuleRepository.Verify(r => r.RemoveAsync(It.IsAny<AvailabilityRule>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
