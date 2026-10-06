using BookSpace.Application.Calendar;
using BookSpace.Application.Common;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Calendar;

public sealed class RegenerateCalendarSubscriptionCommandHandlerTests
{
    private readonly Mock<ICalendarSubscriptionRepository> _calendarSubscriptionRepository = new();
    private readonly Mock<ICurrentUserContext> _currentUserContext = new();

    private RegenerateCalendarSubscriptionCommandHandler CreateSut() =>
        new(_calendarSubscriptionRepository.Object, _currentUserContext.Object,
            Options.Create(new CalendarFeedOptions { PublicBaseUrl = "https://bookspace.example.com" }));

    [Fact]
    public async Task Handle_WithAnExistingActiveSubscription_RevokesItAndInsertsAReplacementWithANewToken()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        _currentUserContext.SetupGet(c => c.TenantId).Returns(tenantId);
        _currentUserContext.SetupGet(c => c.UserId).Returns(userId);
        var existing = new CalendarSubscription { Id = Guid.NewGuid(), TenantId = tenantId, UserId = userId, TokenHash = "old-hash" };
        _calendarSubscriptionRepository.Setup(r => r.FindActiveByUserAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        var sut = CreateSut();

        var result = await sut.Handle(new RegenerateCalendarSubscriptionCommandRequest(), CancellationToken.None);

        Assert.NotNull(existing.RevokedAtUtc);
        Assert.StartsWith("https://bookspace.example.com/api/calendar-feed?token=", result.FeedUrl);
        _calendarSubscriptionRepository.Verify(r => r.AddAsync(
            It.Is<CalendarSubscription>(s => s.TenantId == tenantId && s.UserId == userId && s.RevokedAtUtc == null && s.TokenHash != "old-hash"),
            It.IsAny<CancellationToken>()), Times.Once);
        _calendarSubscriptionRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithNoActiveSubscription_ThrowsNotFoundExceptionWithoutSaving()
    {
        _currentUserContext.SetupGet(c => c.UserId).Returns(Guid.NewGuid());
        _calendarSubscriptionRepository.Setup(r => r.FindActiveByUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((CalendarSubscription?)null);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() => sut.Handle(new RegenerateCalendarSubscriptionCommandRequest(), CancellationToken.None));

        Assert.Equal("CalendarSubscription.NotFound", exception.ErrorCode);
        _calendarSubscriptionRepository.Verify(r => r.AddAsync(It.IsAny<CalendarSubscription>(), It.IsAny<CancellationToken>()), Times.Never);
        _calendarSubscriptionRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
