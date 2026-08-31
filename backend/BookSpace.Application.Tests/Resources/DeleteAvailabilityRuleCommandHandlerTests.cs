using BookSpace.Application.Common;
using BookSpace.Application.Resources;
using BookSpace.Domain.Entities;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Resources;

public sealed class DeleteAvailabilityRuleCommandHandlerTests
{
    private readonly Mock<IAvailabilityRuleRepository> _availabilityRuleRepository = new();

    private DeleteAvailabilityRuleCommandHandler CreateSut() => new(_availabilityRuleRepository.Object);

    [Fact]
    public async Task Handle_WithMatchingRule_RemovesItAndSaves()
    {
        var resourceId = Guid.NewGuid();
        var rule = new AvailabilityRule { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceId = resourceId, DayOfWeek = DayOfWeek.Monday, StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(18, 0) };
        _availabilityRuleRepository.Setup(r => r.FindByIdAsync(rule.Id, It.IsAny<CancellationToken>())).ReturnsAsync(rule);
        var sut = CreateSut();

        await sut.Handle(new DeleteAvailabilityRuleCommand(resourceId, rule.Id), CancellationToken.None);

        _availabilityRuleRepository.Verify(r => r.RemoveAsync(rule, It.IsAny<CancellationToken>()), Times.Once);
        _availabilityRuleRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithUnknownRule_ThrowsNotFoundException()
    {
        _availabilityRuleRepository.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((AvailabilityRule?)null);
        var sut = CreateSut();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new DeleteAvailabilityRuleCommand(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None));
    }

    [Fact]
    public async Task Handle_WithRuleBelongingToADifferentResource_ThrowsNotFoundException()
    {
        var rule = new AvailabilityRule { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceId = Guid.NewGuid(), DayOfWeek = DayOfWeek.Monday, StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(18, 0) };
        _availabilityRuleRepository.Setup(r => r.FindByIdAsync(rule.Id, It.IsAny<CancellationToken>())).ReturnsAsync(rule);
        var sut = CreateSut();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new DeleteAvailabilityRuleCommand(Guid.NewGuid(), rule.Id), CancellationToken.None));

        _availabilityRuleRepository.Verify(r => r.RemoveAsync(It.IsAny<AvailabilityRule>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
