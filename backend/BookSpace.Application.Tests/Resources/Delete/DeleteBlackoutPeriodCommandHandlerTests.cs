using BookSpace.Application.Common;
using BookSpace.Application.Resources;
using BookSpace.Domain.Entities;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Resources;

public sealed class DeleteBlackoutPeriodCommandHandlerTests
{
    private readonly Mock<IBlackoutPeriodRepository> _blackoutPeriodRepository = new();

    private DeleteBlackoutPeriodCommandHandler CreateSut() => new(_blackoutPeriodRepository.Object);

    [Fact]
    public async Task Handle_WithMatchingPeriod_RemovesItAndSaves()
    {
        var resourceId = Guid.NewGuid();
        var period = new BlackoutPeriod
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            ResourceId = resourceId,
            StartUtc = DateTimeOffset.UtcNow.AddDays(1),
            EndUtc = DateTimeOffset.UtcNow.AddDays(1).AddHours(1),
            Reason = "Maintenance",
        };
        _blackoutPeriodRepository.Setup(r => r.FindByIdAsync(period.Id, It.IsAny<CancellationToken>())).ReturnsAsync(period);
        var sut = CreateSut();

        await sut.Handle(new DeleteBlackoutPeriodCommandRequest(resourceId, period.Id), CancellationToken.None);

        _blackoutPeriodRepository.Verify(r => r.RemoveAsync(period, It.IsAny<CancellationToken>()), Times.Once);
        _blackoutPeriodRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithUnknownPeriod_ThrowsNotFoundException()
    {
        _blackoutPeriodRepository.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((BlackoutPeriod?)null);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new DeleteBlackoutPeriodCommandRequest(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None));

        Assert.Equal("BlackoutPeriod.NotFound", exception.ErrorCode);
    }

    // The identical "wrong resource" mismatch check is already tested for the sibling
    // DeleteAvailabilityRuleCommandHandler and UpdateBlackoutPeriodCommandHandler - this handler's own
    // ResourceId != request.ResourceId branch had no test at all before this.
    [Fact]
    public async Task Handle_WithPeriodBelongingToADifferentResource_ThrowsNotFoundException()
    {
        var period = new BlackoutPeriod
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceId = Guid.NewGuid(),
            StartUtc = DateTimeOffset.UtcNow.AddDays(1), EndUtc = DateTimeOffset.UtcNow.AddDays(1).AddHours(1), Reason = "Maintenance",
        };
        _blackoutPeriodRepository.Setup(r => r.FindByIdAsync(period.Id, It.IsAny<CancellationToken>())).ReturnsAsync(period);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new DeleteBlackoutPeriodCommandRequest(Guid.NewGuid(), period.Id), CancellationToken.None));

        Assert.Equal("BlackoutPeriod.NotFound", exception.ErrorCode);
        _blackoutPeriodRepository.Verify(r => r.RemoveAsync(It.IsAny<BlackoutPeriod>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
