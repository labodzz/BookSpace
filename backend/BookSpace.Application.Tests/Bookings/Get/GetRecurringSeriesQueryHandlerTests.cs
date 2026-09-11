using BookSpace.Application.Bookings;
using BookSpace.Application.Common;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.Bookings;

public sealed class GetRecurringSeriesQueryHandlerTests
{
    private readonly Mock<IRecurringSeriesRepository> _recurringSeriesRepository = new();
    private readonly Mock<IBookingRepository> _bookingRepository = new();
    private readonly Mock<ICurrentUserContext> _currentUserContext = new();

    private static readonly Guid UserId = Guid.NewGuid();

    private GetRecurringSeriesQueryHandler CreateSut() =>
        new(_recurringSeriesRepository.Object, _bookingRepository.Object, _currentUserContext.Object);

    private static RecurringSeries CreateSeries(Guid userId) => new()
    {
        Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ResourceId = Guid.NewGuid(), UserId = userId,
        StartDate = new DateOnly(2026, 1, 5), StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0),
        TimeZoneId = "UTC", Frequency = RecurrenceFrequency.Daily, Interval = 1, OccurrenceCount = 5, Quantity = 1,
    };

    [Fact]
    public async Task Handle_WithOwnSeries_ReturnsDefinitionAndOccurrences()
    {
        var series = CreateSeries(UserId);
        var occurrence = new Booking
        {
            Id = Guid.NewGuid(), TenantId = series.TenantId, ResourceId = series.ResourceId, UserId = UserId, SeriesId = series.Id,
            StartUtc = DateTimeOffset.UtcNow.AddDays(1), EndUtc = DateTimeOffset.UtcNow.AddDays(1).AddHours(1),
            Quantity = 1, Status = BookingStatus.Confirmed, CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _recurringSeriesRepository.Setup(r => r.FindByIdAsync(series.Id, It.IsAny<CancellationToken>())).ReturnsAsync(series);
        _bookingRepository.Setup(r => r.GetBySeriesIdAsync(series.Id, It.IsAny<CancellationToken>())).ReturnsAsync([occurrence]);
        var sut = CreateSut();

        var result = await sut.Handle(new GetRecurringSeriesQueryRequest(series.Id), CancellationToken.None);

        Assert.Equal(series.Id, result.Id);
        Assert.Equal(series.ResourceId, result.ResourceId);
        var occurrenceResponse = Assert.Single(result.Occurrences);
        Assert.Equal(occurrence.Id, occurrenceResponse.Id);
    }

    // OrderBy(StartUtc) was previously unproven - every other test uses exactly one occurrence.
    [Fact]
    public async Task Handle_WithMultipleOccurrences_OrdersThemByStartUtc()
    {
        var series = CreateSeries(UserId);
        var later = new Booking
        {
            Id = Guid.NewGuid(), TenantId = series.TenantId, ResourceId = series.ResourceId, UserId = UserId, SeriesId = series.Id,
            StartUtc = DateTimeOffset.UtcNow.AddDays(2), EndUtc = DateTimeOffset.UtcNow.AddDays(2).AddHours(1),
            Quantity = 1, Status = BookingStatus.Confirmed, CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        var earlier = new Booking
        {
            Id = Guid.NewGuid(), TenantId = series.TenantId, ResourceId = series.ResourceId, UserId = UserId, SeriesId = series.Id,
            StartUtc = DateTimeOffset.UtcNow.AddDays(1), EndUtc = DateTimeOffset.UtcNow.AddDays(1).AddHours(1),
            Quantity = 1, Status = BookingStatus.Confirmed, CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _recurringSeriesRepository.Setup(r => r.FindByIdAsync(series.Id, It.IsAny<CancellationToken>())).ReturnsAsync(series);
        // Deliberately returned out of order.
        _bookingRepository.Setup(r => r.GetBySeriesIdAsync(series.Id, It.IsAny<CancellationToken>())).ReturnsAsync([later, earlier]);
        var sut = CreateSut();

        var result = await sut.Handle(new GetRecurringSeriesQueryRequest(series.Id), CancellationToken.None);

        Assert.Equal(2, result.Occurrences.Count);
        Assert.Equal(earlier.Id, result.Occurrences[0].Id);
        Assert.Equal(later.Id, result.Occurrences[1].Id);
    }

    // Every existing test hardcodes OccurrenceCount with EndDate=null - the EndDate-terminated variant
    // of the response mapping was never asserted.
    [Fact]
    public async Task Handle_WithAnEndDateTerminatedSeries_MapsEndDateAndNullOccurrenceCount()
    {
        var series = CreateSeries(UserId);
        series.OccurrenceCount = null;
        series.EndDate = series.StartDate.AddMonths(1);
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _recurringSeriesRepository.Setup(r => r.FindByIdAsync(series.Id, It.IsAny<CancellationToken>())).ReturnsAsync(series);
        _bookingRepository.Setup(r => r.GetBySeriesIdAsync(series.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var sut = CreateSut();

        var result = await sut.Handle(new GetRecurringSeriesQueryRequest(series.Id), CancellationToken.None);

        Assert.Equal(series.EndDate, result.EndDate);
        Assert.Null(result.OccurrenceCount);
    }

    [Fact]
    public async Task Handle_WithUnknownSeries_ThrowsNotFoundException()
    {
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _recurringSeriesRepository.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((RecurringSeries?)null);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new GetRecurringSeriesQueryRequest(Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(ErrorCodes.RecurringSeriesNotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WithSeriesBelongingToAnotherUser_ThrowsNotFoundException()
    {
        var series = CreateSeries(Guid.NewGuid()); // a different user's series
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _recurringSeriesRepository.Setup(r => r.FindByIdAsync(series.Id, It.IsAny<CancellationToken>())).ReturnsAsync(series);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            sut.Handle(new GetRecurringSeriesQueryRequest(series.Id), CancellationToken.None));

        Assert.Equal(ErrorCodes.RecurringSeriesNotFound, exception.ErrorCode);
    }
}
