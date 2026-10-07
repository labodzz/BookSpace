using BookSpace.Application.Bookings;
using BookSpace.Application.Common;
using BookSpace.Application.Resources;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Bookings;

public sealed class GetOwnBookingsQueryHandlerTests
{
    private readonly Mock<IBookingRepository> _bookingRepository = new();
    private readonly Mock<IResourceRepository> _resourceRepository = new();
    private readonly Mock<ITenantRepository> _tenantRepository = new();
    private readonly Mock<ICurrentUserContext> _currentUserContext = new();

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid TenantId = Guid.NewGuid();

    // TenantId/the tenant lookup are wired here (not per-test) since every test shares the same caller's
    // tenant and none of them care about a non-default CheckInLeadMinutes/NoShowGraceMinutes - the one test
    // that does (Handle_MapsCheckInFields_FromTheCallersTenantSettings) overrides the tenant explicitly.
    private GetOwnBookingsQueryHandler CreateSut()
    {
        _currentUserContext.SetupGet(c => c.TenantId).Returns(TenantId);
        _tenantRepository.Setup(r => r.FindByIdAsync(TenantId, It.IsAny<CancellationToken>())).ReturnsAsync(CreateTenant());
        return new GetOwnBookingsQueryHandler(_bookingRepository.Object, _resourceRepository.Object, _tenantRepository.Object, _currentUserContext.Object);
    }

    private static Resource CreateResource(Guid id, string timeZoneId) => new()
    {
        Id = id, TenantId = Guid.NewGuid(), ResourceTypeId = Guid.NewGuid(), Name = "Falcon Room",
        Capacity = 4, Status = ResourceStatus.Active, TimeZoneId = timeZoneId,
    };

    private static Tenant CreateTenant(int checkInLeadMinutes = 15, int noShowGraceMinutes = 15) => new()
    {
        Id = TenantId, Name = "Test Tenant", DefaultTimeZoneId = "UTC", CreatedAtUtc = DateTimeOffset.UtcNow,
        CheckInLeadMinutes = checkInLeadMinutes, NoShowGraceMinutes = noShowGraceMinutes,
    };

    [Fact]
    public async Task Handle_QueriesByTheCallersOwnUserId_NeverAClientSuppliedOne()
    {
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _bookingRepository
            .Setup(r => r.GetOwnBookingsAsync(UserId, null, null, null, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<Booking>([], 1, 20, 0));
        var sut = CreateSut();

        await sut.Handle(new GetOwnBookingsQueryRequest(), CancellationToken.None);

        _bookingRepository.Verify(r => r.GetOwnBookingsAsync(UserId, null, null, null, 1, 20, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithSeriesIdFilter_PassesItThrough()
    {
        var seriesId = Guid.NewGuid();
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _bookingRepository
            .Setup(r => r.GetOwnBookingsAsync(UserId, seriesId, null, null, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<Booking>([], 1, 20, 0));
        var sut = CreateSut();

        await sut.Handle(new GetOwnBookingsQueryRequest(SeriesId: seriesId), CancellationToken.None);

        _bookingRepository.Verify(r => r.GetOwnBookingsAsync(UserId, seriesId, null, null, 1, 20, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithDateRangeFilter_PassesItThrough()
    {
        var fromUtc = DateTimeOffset.UtcNow;
        var toUtc = fromUtc.AddDays(30);
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _bookingRepository
            .Setup(r => r.GetOwnBookingsAsync(UserId, null, fromUtc, toUtc, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<Booking>([], 1, 20, 0));
        var sut = CreateSut();

        await sut.Handle(new GetOwnBookingsQueryRequest(FromUtc: fromUtc, ToUtc: toUtc), CancellationToken.None);

        _bookingRepository.Verify(r => r.GetOwnBookingsAsync(UserId, null, fromUtc, toUtc, 1, 20, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_MapsBookingsToResponseItemsPreservingPaging()
    {
        var booking = new Booking
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceId = Guid.NewGuid(), UserId = UserId,
            StartUtc = DateTimeOffset.UtcNow, EndUtc = DateTimeOffset.UtcNow.AddHours(1),
            Quantity = 2, Status = BookingStatus.Confirmed, CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _bookingRepository
            .Setup(r => r.GetOwnBookingsAsync(UserId, null, null, null, 2, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<Booking>([booking], 2, 10, 1));
        var sut = CreateSut();

        var result = await sut.Handle(new GetOwnBookingsQueryRequest(Page: 2, PageSize: 10), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(booking.Id, item.Id);
        Assert.Equal(booking.Status, item.Status);
        Assert.Equal(2, result.Page);
        Assert.Equal(10, result.PageSize);
        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public async Task Handle_WithBookingCancelledByItsOwner_MapsCancelledByAdminAsFalse()
    {
        var booking = new Booking
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceId = Guid.NewGuid(), UserId = UserId,
            StartUtc = DateTimeOffset.UtcNow, EndUtc = DateTimeOffset.UtcNow.AddHours(1), Quantity = 1,
            Status = BookingStatus.Cancelled, CreatedAtUtc = DateTimeOffset.UtcNow,
            CancelledAtUtc = DateTimeOffset.UtcNow, CancelledByUserId = UserId, CancellationReason = null,
        };
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _bookingRepository
            .Setup(r => r.GetOwnBookingsAsync(UserId, null, null, null, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<Booking>([booking], 1, 20, 1));
        var sut = CreateSut();

        var result = await sut.Handle(new GetOwnBookingsQueryRequest(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.False(item.CancelledByAdmin);
        Assert.Null(item.CancellationReason);
        Assert.NotNull(item.CancelledAtUtc);
    }

    [Fact]
    public async Task Handle_WithBookingCancelledByATenantAdmin_MapsCancelledByAdminAsTrueWithReason()
    {
        var booking = new Booking
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceId = Guid.NewGuid(), UserId = UserId,
            StartUtc = DateTimeOffset.UtcNow, EndUtc = DateTimeOffset.UtcNow.AddHours(1), Quantity = 1,
            Status = BookingStatus.Cancelled, CreatedAtUtc = DateTimeOffset.UtcNow,
            CancelledAtUtc = DateTimeOffset.UtcNow, CancelledByUserId = Guid.NewGuid(), // a different user - the admin
            CancellationReason = "Resource decommissioned",
        };
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _bookingRepository
            .Setup(r => r.GetOwnBookingsAsync(UserId, null, null, null, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<Booking>([booking], 1, 20, 1));
        var sut = CreateSut();

        var result = await sut.Handle(new GetOwnBookingsQueryRequest(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.True(item.CancelledByAdmin);
        Assert.Equal("Resource decommissioned", item.CancellationReason);
    }

    [Fact]
    public async Task Handle_MapsSeriesId_SoTheClientCanTellRecurringOccurrencesApart()
    {
        var seriesId = Guid.NewGuid();
        var booking = new Booking
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceId = Guid.NewGuid(), UserId = UserId,
            StartUtc = DateTimeOffset.UtcNow, EndUtc = DateTimeOffset.UtcNow.AddHours(1),
            Quantity = 1, Status = BookingStatus.Confirmed, CreatedAtUtc = DateTimeOffset.UtcNow, SeriesId = seriesId,
        };
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _bookingRepository
            .Setup(r => r.GetOwnBookingsAsync(UserId, null, null, null, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<Booking>([booking], 1, 20, 1));
        var sut = CreateSut();

        var result = await sut.Handle(new GetOwnBookingsQueryRequest(), CancellationToken.None);

        Assert.Equal(seriesId, Assert.Single(result.Items).SeriesId);
    }

    [Fact]
    public async Task Handle_MapsTimeZoneId_FromABatchedResourceLookup()
    {
        var resourceId = Guid.NewGuid();
        var booking = new Booking
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceId = resourceId, UserId = UserId,
            StartUtc = DateTimeOffset.UtcNow, EndUtc = DateTimeOffset.UtcNow.AddHours(1),
            Quantity = 1, Status = BookingStatus.Confirmed, CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _bookingRepository
            .Setup(r => r.GetOwnBookingsAsync(UserId, null, null, null, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<Booking>([booking], 1, 20, 1));
        _resourceRepository
            .Setup(r => r.GetByIdsAsync(It.Is<IReadOnlyList<Guid>>(ids => ids.Count == 1 && ids[0] == resourceId), It.IsAny<CancellationToken>()))
            .ReturnsAsync([CreateResource(resourceId, "Europe/Sarajevo")]);
        var sut = CreateSut();

        var result = await sut.Handle(new GetOwnBookingsQueryRequest(), CancellationToken.None);

        Assert.Equal("Europe/Sarajevo", Assert.Single(result.Items).TimeZoneId);
    }

    [Fact]
    public async Task Handle_WithBookingsAcrossMultipleResources_QueriesResourceRepositoryOnceWithAllDistinctResourceIds()
    {
        var resourceId1 = Guid.NewGuid();
        var resourceId2 = Guid.NewGuid();
        // A duplicate ResourceId (two bookings on the same resource) proves the lookup is de-duplicated,
        // not batched once per booking.
        var bookings = new[]
        {
            new Booking { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceId = resourceId1, UserId = UserId, StartUtc = DateTimeOffset.UtcNow, EndUtc = DateTimeOffset.UtcNow.AddHours(1), Quantity = 1, Status = BookingStatus.Confirmed, CreatedAtUtc = DateTimeOffset.UtcNow },
            new Booking { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceId = resourceId1, UserId = UserId, StartUtc = DateTimeOffset.UtcNow, EndUtc = DateTimeOffset.UtcNow.AddHours(1), Quantity = 1, Status = BookingStatus.Confirmed, CreatedAtUtc = DateTimeOffset.UtcNow },
            new Booking { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceId = resourceId2, UserId = UserId, StartUtc = DateTimeOffset.UtcNow, EndUtc = DateTimeOffset.UtcNow.AddHours(1), Quantity = 1, Status = BookingStatus.Confirmed, CreatedAtUtc = DateTimeOffset.UtcNow },
        };
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _bookingRepository
            .Setup(r => r.GetOwnBookingsAsync(UserId, null, null, null, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<Booking>(bookings, 1, 20, 3));
        _resourceRepository
            .Setup(r => r.GetByIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([CreateResource(resourceId1, "UTC"), CreateResource(resourceId2, "Asia/Tokyo")]);
        var sut = CreateSut();

        await sut.Handle(new GetOwnBookingsQueryRequest(), CancellationToken.None);

        _resourceRepository.Verify(
            r => r.GetByIdsAsync(It.Is<IReadOnlyList<Guid>>(ids => ids.Count == 2 && ids.Contains(resourceId1) && ids.Contains(resourceId2)), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // Required scenario 7: display-only additions (TimeZoneId) must never alter what's actually stored/
    // exchanged for a booking's own start/end instants.
    [Fact]
    public async Task Handle_NeverAltersStartUtcOrEndUtc_RegardlessOfResourceTimeZone()
    {
        var resourceId = Guid.NewGuid();
        var startUtc = DateTimeOffset.Parse("2026-07-15T08:15:00Z");
        var endUtc = DateTimeOffset.Parse("2026-07-15T09:15:00Z");
        var booking = new Booking
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceId = resourceId, UserId = UserId,
            StartUtc = startUtc, EndUtc = endUtc, Quantity = 1, Status = BookingStatus.Confirmed, CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _bookingRepository
            .Setup(r => r.GetOwnBookingsAsync(UserId, null, null, null, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<Booking>([booking], 1, 20, 1));
        _resourceRepository
            .Setup(r => r.GetByIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([CreateResource(resourceId, "Asia/Tokyo")]);
        var sut = CreateSut();

        var result = await sut.Handle(new GetOwnBookingsQueryRequest(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(startUtc, item.StartUtc);
        Assert.Equal(endUtc, item.EndUtc);
    }

    [Fact]
    public async Task Handle_MapsCheckInFields_FromTheCallersTenantSettings()
    {
        var startUtc = DateTimeOffset.Parse("2026-07-15T08:00:00Z");
        var checkedInAtUtc = startUtc.AddMinutes(-5);
        var booking = new Booking
        {
            Id = Guid.NewGuid(), TenantId = TenantId, ResourceId = Guid.NewGuid(), UserId = UserId,
            StartUtc = startUtc, EndUtc = startUtc.AddHours(1), Quantity = 1, Status = BookingStatus.Confirmed,
            CreatedAtUtc = DateTimeOffset.UtcNow, CheckedInAtUtc = checkedInAtUtc,
        };
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _bookingRepository
            .Setup(r => r.GetOwnBookingsAsync(UserId, null, null, null, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<Booking>([booking], 1, 20, 1));
        var sut = CreateSut();
        // Overrides CreateSut()'s default tenant with non-default lead/grace minutes, so the window
        // boundaries below can only be right if they're genuinely computed from THIS tenant's settings.
        _tenantRepository.Setup(r => r.FindByIdAsync(TenantId, It.IsAny<CancellationToken>())).ReturnsAsync(CreateTenant(checkInLeadMinutes: 20, noShowGraceMinutes: 10));

        var result = await sut.Handle(new GetOwnBookingsQueryRequest(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(checkedInAtUtc, item.CheckedInAtUtc);
        Assert.Equal(startUtc.AddMinutes(-20), item.CheckInWindowOpensAtUtc);
        Assert.Equal(startUtc.AddMinutes(10), item.CheckInWindowClosesAtUtc);
    }

    // The global SysAdmin has no TenantId and can never have a booking - "my bookings" is simply always
    // empty for it, not an error (unlike the write-side handlers, which reject the attempt outright - see
    // TenantMembershipGuard). Never queries the booking repository or looks up a tenant at all.
    [Fact]
    public async Task Handle_ForTheGlobalSysAdminWithNoTenantId_ReturnsAnEmptyPageWithoutQueryingAnything()
    {
        var sut = CreateSut();
        _currentUserContext.SetupGet(c => c.TenantId).Returns((Guid?)null);

        var result = await sut.Handle(new GetOwnBookingsQueryRequest(Page: 2, PageSize: 10), CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Equal(2, result.Page);
        Assert.Equal(10, result.PageSize);
        Assert.Equal(0, result.TotalCount);
        _bookingRepository.Verify(
            r => r.GetOwnBookingsAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _tenantRepository.Verify(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
