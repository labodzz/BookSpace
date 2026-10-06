using BookSpace.Application.Auth;
using BookSpace.Application.Bookings;
using BookSpace.Application.Calendar;
using BookSpace.Application.Common;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Calendar;

public sealed class GetCalendarFeedQueryHandlerTests
{
    private readonly Mock<ICalendarSubscriptionRepository> _calendarSubscriptionRepository = new();
    private readonly Mock<IUserRepository> _userRepository = new();
    private readonly Mock<ITenantRepository> _tenantRepository = new();
    private readonly Mock<IBookingRepository> _bookingRepository = new();

    private GetCalendarFeedQueryHandler CreateSut(int pastWindowDays = 30, int futureWindowDays = 365) =>
        new(_calendarSubscriptionRepository.Object, _userRepository.Object, _tenantRepository.Object, _bookingRepository.Object,
            Options.Create(new CalendarFeedOptions { PublicBaseUrl = "https://bookspace.example.com", PastWindowDays = pastWindowDays, FutureWindowDays = futureWindowDays }));

    private static CalendarSubscription ActiveSubscription(Guid tenantId, Guid userId) =>
        new() { Id = Guid.NewGuid(), TenantId = tenantId, UserId = userId, TokenHash = "hash-of-raw-token" };

    [Fact]
    public async Task Handle_WithAValidTokenForAnActiveUserAndTenant_ReturnsAnIcsCalendarContainingTheirBookings()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        _calendarSubscriptionRepository.Setup(r => r.FindActiveByTokenHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ActiveSubscription(tenantId, userId));
        _userRepository.Setup(r => r.FindByIdForAuthenticationAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User { Id = userId, Status = UserStatus.Active });
        _tenantRepository.Setup(r => r.FindByIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Tenant { Id = tenantId, Status = TenantStatus.Active });
        var bookingId = Guid.NewGuid();
        _bookingRepository.Setup(r => r.GetForCalendarFeedAsync(tenantId, userId, It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CalendarFeedBooking(bookingId, "Conference Room A", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), 1, BookingStatus.Confirmed)]);
        var sut = CreateSut();

        var result = await sut.Handle(new GetCalendarFeedQueryRequest("raw-token"), CancellationToken.None);

        Assert.Contains("BEGIN:VCALENDAR", result.IcsContent);
        Assert.Contains($"UID:{bookingId:D}@bookspace", result.IcsContent);
    }

    [Fact]
    public async Task Handle_DerivesTheQueryWindowFromConfiguredPastAndFutureWindowDays()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        _calendarSubscriptionRepository.Setup(r => r.FindActiveByTokenHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ActiveSubscription(tenantId, userId));
        _userRepository.Setup(r => r.FindByIdForAuthenticationAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User { Id = userId, Status = UserStatus.Active });
        _tenantRepository.Setup(r => r.FindByIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Tenant { Id = tenantId, Status = TenantStatus.Active });
        _bookingRepository.Setup(r => r.GetForCalendarFeedAsync(tenantId, userId, It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var sut = CreateSut(pastWindowDays: 10, futureWindowDays: 20);
        var beforeCall = DateTimeOffset.UtcNow;

        await sut.Handle(new GetCalendarFeedQueryRequest("raw-token"), CancellationToken.None);

        // "now" is read inside Handle, strictly at or after beforeCall (captured just before calling it) -
        // so the actual window bounds are always slightly ON OR AFTER beforeCall +/- the configured days,
        // never before it. A one-minute tolerance absorbs that gap without asserting an exact instant.
        _bookingRepository.Verify(r => r.GetForCalendarFeedAsync(
            tenantId, userId,
            It.Is<DateTimeOffset>(start => start >= beforeCall.AddDays(-10) && start < beforeCall.AddDays(-10).AddMinutes(1)),
            It.Is<DateTimeOffset>(end => end >= beforeCall.AddDays(20) && end < beforeCall.AddDays(20).AddMinutes(1)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Handle_WithAMissingToken_ThrowsNotFoundException(string? token)
    {
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() => sut.Handle(new GetCalendarFeedQueryRequest(token), CancellationToken.None));

        Assert.Equal("CalendarSubscription.NotFound", exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WithAnUnknownOrRevokedToken_ThrowsNotFoundException()
    {
        _calendarSubscriptionRepository.Setup(r => r.FindActiveByTokenHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((CalendarSubscription?)null);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() => sut.Handle(new GetCalendarFeedQueryRequest("some-token"), CancellationToken.None));

        Assert.Equal("CalendarSubscription.NotFound", exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_ForADeactivatedUser_ThrowsNotFoundExceptionEvenThoughTheTokenItselfIsStillValid()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        _calendarSubscriptionRepository.Setup(r => r.FindActiveByTokenHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ActiveSubscription(tenantId, userId));
        _userRepository.Setup(r => r.FindByIdForAuthenticationAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User { Id = userId, Status = UserStatus.Inactive });
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() => sut.Handle(new GetCalendarFeedQueryRequest("raw-token"), CancellationToken.None));

        Assert.Equal("CalendarSubscription.NotFound", exception.ErrorCode);
        _bookingRepository.Verify(r => r.GetForCalendarFeedAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_ForASuspendedTenant_ThrowsNotFoundExceptionEvenThoughTheTokenItselfIsStillValid()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        _calendarSubscriptionRepository.Setup(r => r.FindActiveByTokenHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ActiveSubscription(tenantId, userId));
        _userRepository.Setup(r => r.FindByIdForAuthenticationAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User { Id = userId, Status = UserStatus.Active });
        _tenantRepository.Setup(r => r.FindByIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Tenant { Id = tenantId, Status = TenantStatus.Suspended });
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() => sut.Handle(new GetCalendarFeedQueryRequest("raw-token"), CancellationToken.None));

        Assert.Equal("CalendarSubscription.NotFound", exception.ErrorCode);
        _bookingRepository.Verify(r => r.GetForCalendarFeedAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
