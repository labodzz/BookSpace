using BookSpace.Application.Calendar;
using BookSpace.Application.Common;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Calendar;

public sealed class CreateCalendarSubscriptionCommandHandlerTests
{
    private readonly Mock<ICalendarSubscriptionRepository> _calendarSubscriptionRepository = new();
    private readonly Mock<ICurrentUserContext> _currentUserContext = new();

    private CreateCalendarSubscriptionCommandHandler CreateSut(string publicBaseUrl = "https://bookspace.example.com") =>
        new(_calendarSubscriptionRepository.Object, _currentUserContext.Object, Options.Create(new CalendarFeedOptions { PublicBaseUrl = publicBaseUrl }));

    [Fact]
    public async Task Handle_WithNoExistingSubscription_CreatesOneAndReturnsAnAbsoluteFeedUrlContainingTheRawToken()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        _currentUserContext.SetupGet(c => c.TenantId).Returns(tenantId);
        _currentUserContext.SetupGet(c => c.UserId).Returns(userId);
        _calendarSubscriptionRepository.Setup(r => r.FindActiveByUserAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync((CalendarSubscription?)null);
        var sut = CreateSut();

        var result = await sut.Handle(new CreateCalendarSubscriptionCommandRequest(), CancellationToken.None);

        Assert.StartsWith("https://bookspace.example.com/api/calendar-feed?token=", result.FeedUrl);
        _calendarSubscriptionRepository.Verify(r => r.AddAsync(
            It.Is<CalendarSubscription>(s => s.TenantId == tenantId && s.UserId == userId && s.RevokedAtUtc == null && s.TokenHash.Length > 0),
            It.IsAny<CancellationToken>()), Times.Once);
        _calendarSubscriptionRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_CalledTwice_NeverReturnsTheSameRawTokenInTheFeedUrl()
    {
        _currentUserContext.SetupGet(c => c.TenantId).Returns(Guid.NewGuid());
        _currentUserContext.SetupGet(c => c.UserId).Returns(Guid.NewGuid());
        _calendarSubscriptionRepository.Setup(r => r.FindActiveByUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((CalendarSubscription?)null);
        var sut = CreateSut();

        var first = await sut.Handle(new CreateCalendarSubscriptionCommandRequest(), CancellationToken.None);
        var second = await sut.Handle(new CreateCalendarSubscriptionCommandRequest(), CancellationToken.None);

        Assert.NotEqual(first.FeedUrl, second.FeedUrl);
    }

    // The global SysAdmin has no TenantId and can never have bookings to export - this must throw a
    // clean, documented ConflictException, never an unhandled NullReferenceException/
    // InvalidOperationException from a bare TenantId!.Value further down.
    [Fact]
    public async Task Handle_ForTheGlobalSysAdminWithNoTenantId_ThrowsConflictExceptionWithoutSaving()
    {
        _currentUserContext.SetupGet(c => c.TenantId).Returns((Guid?)null);
        _currentUserContext.SetupGet(c => c.UserId).Returns(Guid.NewGuid());
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<ConflictException>(() => sut.Handle(new CreateCalendarSubscriptionCommandRequest(), CancellationToken.None));

        Assert.Equal("CalendarSubscription.RequiresTenantMembership", exception.ErrorCode);
        _calendarSubscriptionRepository.Verify(r => r.FindActiveByUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _calendarSubscriptionRepository.Verify(r => r.AddAsync(It.IsAny<CalendarSubscription>(), It.IsAny<CancellationToken>()), Times.Never);
        _calendarSubscriptionRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WithAnExistingActiveSubscription_ThrowsConflictExceptionWithoutSaving()
    {
        _currentUserContext.SetupGet(c => c.TenantId).Returns(Guid.NewGuid());
        _currentUserContext.SetupGet(c => c.UserId).Returns(Guid.NewGuid());
        _calendarSubscriptionRepository.Setup(r => r.FindActiveByUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CalendarSubscription { Id = Guid.NewGuid() });
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<ConflictException>(() => sut.Handle(new CreateCalendarSubscriptionCommandRequest(), CancellationToken.None));

        Assert.Equal("CalendarSubscription.AlreadyExists", exception.ErrorCode);
        _calendarSubscriptionRepository.Verify(r => r.AddAsync(It.IsAny<CalendarSubscription>(), It.IsAny<CancellationToken>()), Times.Never);
        _calendarSubscriptionRepository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
