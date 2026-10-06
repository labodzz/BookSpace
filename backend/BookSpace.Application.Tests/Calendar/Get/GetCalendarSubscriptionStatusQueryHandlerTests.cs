using BookSpace.Application.Calendar;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Calendar;

public sealed class GetCalendarSubscriptionStatusQueryHandlerTests
{
    private readonly Mock<ICalendarSubscriptionRepository> _calendarSubscriptionRepository = new();
    private readonly Mock<ICurrentUserContext> _currentUserContext = new();

    private GetCalendarSubscriptionStatusQueryHandler CreateSut() => new(_calendarSubscriptionRepository.Object, _currentUserContext.Object);

    [Fact]
    public async Task Handle_WithAnActiveSubscription_ReturnsTrueAndItsCreatedAtUtc_NeverTheTokenOrUrl()
    {
        var userId = Guid.NewGuid();
        var createdAtUtc = DateTimeOffset.UtcNow.AddDays(-1);
        _currentUserContext.SetupGet(c => c.UserId).Returns(userId);
        _calendarSubscriptionRepository.Setup(r => r.FindActiveByUserAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CalendarSubscription { Id = Guid.NewGuid(), UserId = userId, CreatedAtUtc = createdAtUtc });
        var sut = CreateSut();

        var result = await sut.Handle(new GetCalendarSubscriptionStatusQueryRequest(), CancellationToken.None);

        Assert.True(result.HasActiveSubscription);
        Assert.Equal(createdAtUtc, result.CreatedAtUtc);
    }

    [Fact]
    public async Task Handle_WithNoActiveSubscription_ReturnsFalseAndNullCreatedAtUtc()
    {
        _currentUserContext.SetupGet(c => c.UserId).Returns(Guid.NewGuid());
        _calendarSubscriptionRepository.Setup(r => r.FindActiveByUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((CalendarSubscription?)null);
        var sut = CreateSut();

        var result = await sut.Handle(new GetCalendarSubscriptionStatusQueryRequest(), CancellationToken.None);

        Assert.False(result.HasActiveSubscription);
        Assert.Null(result.CreatedAtUtc);
    }
}
