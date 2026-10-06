using BookSpace.Application.Calendar;
using BookSpace.Application.Common;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Calendar;

public sealed class RevokeCalendarSubscriptionCommandHandlerTests
{
    private readonly Mock<ICalendarSubscriptionRepository> _calendarSubscriptionRepository = new();
    private readonly Mock<ICurrentUserContext> _currentUserContext = new();

    private RevokeCalendarSubscriptionCommandHandler CreateSut() => new(_calendarSubscriptionRepository.Object, _currentUserContext.Object);

    [Fact]
    public async Task Handle_WithAnExistingActiveSubscription_SetsRevokedAtUtcAndSaves()
    {
        var userId = Guid.NewGuid();
        _currentUserContext.SetupGet(c => c.UserId).Returns(userId);
        var existing = new CalendarSubscription { Id = Guid.NewGuid(), UserId = userId };
        _calendarSubscriptionRepository.Setup(r => r.FindActiveByUserAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        var sut = CreateSut();

        await sut.Handle(new RevokeCalendarSubscriptionCommandRequest(), CancellationToken.None);

        Assert.NotNull(existing.RevokedAtUtc);
        _calendarSubscriptionRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithNoActiveSubscription_ThrowsNotFoundExceptionWithoutSaving()
    {
        _currentUserContext.SetupGet(c => c.UserId).Returns(Guid.NewGuid());
        _calendarSubscriptionRepository.Setup(r => r.FindActiveByUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((CalendarSubscription?)null);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() => sut.Handle(new RevokeCalendarSubscriptionCommandRequest(), CancellationToken.None));

        Assert.Equal("CalendarSubscription.NotFound", exception.ErrorCode);
        _calendarSubscriptionRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
