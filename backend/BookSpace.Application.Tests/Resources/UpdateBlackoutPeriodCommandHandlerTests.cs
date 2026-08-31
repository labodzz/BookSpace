using BookSpace.Application.Common;
using BookSpace.Application.Resources;
using BookSpace.Domain.Entities;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Resources;

public sealed class UpdateBlackoutPeriodCommandHandlerTests
{
    private readonly Mock<IBlackoutPeriodRepository> _blackoutPeriodRepository = new();

    private UpdateBlackoutPeriodCommandHandler CreateSut() => new(_blackoutPeriodRepository.Object);

    private static BlackoutPeriod CreatePeriod(Guid resourceId) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        ResourceId = resourceId,
        StartUtc = DateTimeOffset.UtcNow.AddDays(1),
        EndUtc = DateTimeOffset.UtcNow.AddDays(1).AddHours(2),
        Reason = "Old reason",
    };

    [Fact]
    public async Task Handle_WithMatchingPeriod_UpdatesAndSaves()
    {
        var resourceId = Guid.NewGuid();
        var period = CreatePeriod(resourceId);
        var newStart = DateTimeOffset.UtcNow.AddDays(2);
        var newEnd = newStart.AddHours(3);
        _blackoutPeriodRepository.Setup(r => r.FindByIdAsync(period.Id, It.IsAny<CancellationToken>())).ReturnsAsync(period);
        var sut = CreateSut();

        var result = await sut.Handle(new UpdateBlackoutPeriodCommand(resourceId, period.Id, newStart, newEnd, "New reason"), CancellationToken.None);

        Assert.Equal(newStart, result.StartUtc);
        Assert.Equal(newEnd, result.EndUtc);
        Assert.Equal("New reason", result.Reason);
        _blackoutPeriodRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithUnknownPeriod_ThrowsNotFoundException()
    {
        _blackoutPeriodRepository.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((BlackoutPeriod?)null);
        var sut = CreateSut();
        var start = DateTimeOffset.UtcNow.AddDays(1);

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new UpdateBlackoutPeriodCommand(Guid.NewGuid(), Guid.NewGuid(), start, start.AddHours(1), "Reason"), CancellationToken.None));

        Assert.Equal("BlackoutPeriod.NotFound", exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WithPeriodBelongingToADifferentResource_ThrowsNotFoundException()
    {
        var period = CreatePeriod(Guid.NewGuid());
        _blackoutPeriodRepository.Setup(r => r.FindByIdAsync(period.Id, It.IsAny<CancellationToken>())).ReturnsAsync(period);
        var sut = CreateSut();
        var start = DateTimeOffset.UtcNow.AddDays(1);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new UpdateBlackoutPeriodCommand(Guid.NewGuid(), period.Id, start, start.AddHours(1), "Reason"), CancellationToken.None));

        _blackoutPeriodRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
