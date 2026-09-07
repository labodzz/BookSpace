using BookSpace.Application.Bookings;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Xunit;

namespace BookSpace.Application.Tests.Bookings;

public sealed class RecurringOccurrenceGeneratorTests
{
    private static readonly DateOnly StartDate = new(2026, 1, 5); // a Monday

    private static RecurringSeries CreateSeries(
        RecurrenceFrequency frequency, int interval, DateOnly? endDate = null, int? occurrenceCount = null, DateOnly? startDate = null) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        ResourceId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        StartDate = startDate ?? StartDate,
        StartTime = new TimeOnly(9, 0),
        EndTime = new TimeOnly(10, 0),
        TimeZoneId = "UTC",
        Frequency = frequency,
        Interval = interval,
        EndDate = endDate,
        OccurrenceCount = occurrenceCount,
        Quantity = 1,
    };

    [Fact]
    public void GenerateCandidateDates_Daily_ProducesConsecutiveDates()
    {
        var series = CreateSeries(RecurrenceFrequency.Daily, interval: 1, occurrenceCount: 3);

        var dates = RecurringOccurrenceGenerator.GenerateCandidateDates(series);

        Assert.Equal([StartDate, StartDate.AddDays(1), StartDate.AddDays(2)], dates);
    }

    [Fact]
    public void GenerateCandidateDates_DailyWithInterval_SkipsDays()
    {
        var series = CreateSeries(RecurrenceFrequency.Daily, interval: 3, occurrenceCount: 3);

        var dates = RecurringOccurrenceGenerator.GenerateCandidateDates(series);

        Assert.Equal([StartDate, StartDate.AddDays(3), StartDate.AddDays(6)], dates);
    }

    [Fact]
    public void GenerateCandidateDates_Weekly_ProducesWeeklyDates()
    {
        var series = CreateSeries(RecurrenceFrequency.Weekly, interval: 1, occurrenceCount: 3);

        var dates = RecurringOccurrenceGenerator.GenerateCandidateDates(series);

        Assert.Equal([StartDate, StartDate.AddDays(7), StartDate.AddDays(14)], dates);
    }

    [Fact]
    public void GenerateCandidateDates_WeeklyWithInterval_SkipsWeeks()
    {
        var series = CreateSeries(RecurrenceFrequency.Weekly, interval: 2, occurrenceCount: 3);

        var dates = RecurringOccurrenceGenerator.GenerateCandidateDates(series);

        Assert.Equal([StartDate, StartDate.AddDays(14), StartDate.AddDays(28)], dates);
    }

    [Fact]
    public void GenerateCandidateDates_Monthly_ProducesMonthlyDatesOnTheSameDayOfMonth()
    {
        var series = CreateSeries(RecurrenceFrequency.Monthly, interval: 1, occurrenceCount: 3, startDate: new DateOnly(2026, 1, 15));

        var dates = RecurringOccurrenceGenerator.GenerateCandidateDates(series);

        Assert.Equal([new DateOnly(2026, 1, 15), new DateOnly(2026, 2, 15), new DateOnly(2026, 3, 15)], dates);
    }

    [Fact]
    public void GenerateCandidateDates_MonthlyFromTheThirtyFirst_ClampsToTheLastValidDayOfAShorterMonth()
    {
        // Jan 31 -> Feb has no 31st, clamp to Feb 28 (2026 is not a leap year).
        var series = CreateSeries(RecurrenceFrequency.Monthly, interval: 1, occurrenceCount: 2, startDate: new DateOnly(2026, 1, 31));

        var dates = RecurringOccurrenceGenerator.GenerateCandidateDates(series);

        Assert.Equal([new DateOnly(2026, 1, 31), new DateOnly(2026, 2, 28)], dates);
    }

    [Fact]
    public void GenerateCandidateDates_MonthlyAfterAClampedMonth_ReturnsToTheOriginalDayOfMonth()
    {
        // Jan 31 -> Feb 28 (clamped) -> Mar 31 (back to 31, computed fresh from the Jan 31 anchor, not
        // chained from the clamped Feb 28 - the exact drift bug this design deliberately avoids).
        var series = CreateSeries(RecurrenceFrequency.Monthly, interval: 1, occurrenceCount: 3, startDate: new DateOnly(2026, 1, 31));

        var dates = RecurringOccurrenceGenerator.GenerateCandidateDates(series);

        Assert.Equal([new DateOnly(2026, 1, 31), new DateOnly(2026, 2, 28), new DateOnly(2026, 3, 31)], dates);
    }

    [Fact]
    public void GenerateCandidateDates_WithEndDate_StopsAtOrBeforeEndDateInclusive()
    {
        var series = CreateSeries(RecurrenceFrequency.Daily, interval: 1, endDate: StartDate.AddDays(2));

        var dates = RecurringOccurrenceGenerator.GenerateCandidateDates(series);

        Assert.Equal([StartDate, StartDate.AddDays(1), StartDate.AddDays(2)], dates);
    }

    [Fact]
    public void GenerateCandidateDates_WithOccurrenceCount_ProducesExactlyThatManyDates()
    {
        var series = CreateSeries(RecurrenceFrequency.Daily, interval: 1, occurrenceCount: 5);

        var dates = RecurringOccurrenceGenerator.GenerateCandidateDates(series);

        Assert.Equal(5, dates.Count);
    }

    [Fact]
    public void GenerateCandidateDates_NeverExceedsMaxOccurrences()
    {
        // A pathologically far EndDate (validation should reject this at the API boundary, but the
        // generator itself is a hard backstop independent of that).
        var series = CreateSeries(RecurrenceFrequency.Daily, interval: 1, endDate: StartDate.AddYears(10));

        var dates = RecurringOccurrenceGenerator.GenerateCandidateDates(series);

        Assert.Equal(RecurringOccurrenceGenerator.MaxOccurrences, dates.Count);
    }

    [Fact]
    public void TryResolveOccurrence_ForAnOrdinaryTime_ResolvesToTheCorrectUtcInstant()
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("UTC");

        var resolution = RecurringOccurrenceGenerator.TryResolveOccurrence(StartDate, new TimeOnly(9, 0), new TimeOnly(10, 0), timeZone);

        Assert.True(resolution.Succeeded);
        Assert.Equal(new DateTimeOffset(StartDate.Year, StartDate.Month, StartDate.Day, 9, 0, 0, TimeSpan.Zero), resolution.StartUtc);
        Assert.Equal(new DateTimeOffset(StartDate.Year, StartDate.Month, StartDate.Day, 10, 0, 0, TimeSpan.Zero), resolution.EndUtc);
    }

    // DST tests use Europe/Sarajevo (CET/CEST, spring-forward last Sunday of March, fall-back last
    // Sunday of October) against a future year so the test stays valid regardless of which year it runs
    // in - same convention as GetResourceAvailabilityQueryHandlerTests' DST tests.
    private static DateOnly LastSundayOfMonth(int year, int month)
    {
        var lastDay = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        while (lastDay.DayOfWeek != DayOfWeek.Sunday)
        {
            lastDay = lastDay.AddDays(-1);
        }

        return lastDay;
    }

    [Fact]
    public void TryResolveOccurrence_WhenStartTimeFallsInASpringForwardGap_ReturnsNonexistentLocalTimeConflictWithoutShifting()
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Sarajevo");
        var springForwardDate = LastSundayOfMonth(DateTime.UtcNow.Year + 1, 3);

        // 02:30 never existed that day (clocks jump 02:00 -> 03:00 CET->CEST).
        var resolution = RecurringOccurrenceGenerator.TryResolveOccurrence(springForwardDate, new TimeOnly(2, 30), new TimeOnly(4, 0), timeZone);

        Assert.False(resolution.Succeeded);
        Assert.Equal("NonexistentLocalTime", resolution.ConflictReason);
        Assert.Null(resolution.StartUtc);
        Assert.Null(resolution.EndUtc);
    }

    [Fact]
    public void TryResolveOccurrence_WhenEndTimeFallsInASpringForwardGap_ReturnsNonexistentLocalTimeConflict()
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Sarajevo");
        var springForwardDate = LastSundayOfMonth(DateTime.UtcNow.Year + 1, 3);

        var resolution = RecurringOccurrenceGenerator.TryResolveOccurrence(springForwardDate, new TimeOnly(1, 0), new TimeOnly(2, 30), timeZone);

        Assert.False(resolution.Succeeded);
        Assert.Equal("NonexistentLocalTime", resolution.ConflictReason);
    }

    [Fact]
    public void TryResolveOccurrence_WhenLocalTimeIsAmbiguousAtFallBack_ResolvesToTheEarlierInstantDeterministically()
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Sarajevo");
        var fallBackDate = LastSundayOfMonth(DateTime.UtcNow.Year + 1, 10);

        // 02:30 occurs twice (clocks fall 03:00 -> 02:00 CEST->CET). The EARLIER of the two instants is
        // the daylight (CEST, UTC+2) occurrence: 02:30 - 2:00 = 00:30Z - not the later, standard-offset
        // (CET, UTC+1) occurrence AvailabilityCalculator.ConvertLocalToUtc would pick for an availability
        // window (01:30Z) - see docs/recurring-bookings-and-approvals.md for why these two policies
        // deliberately differ.
        var resolution = RecurringOccurrenceGenerator.TryResolveOccurrence(fallBackDate, new TimeOnly(2, 30), new TimeOnly(5, 0), timeZone);

        Assert.True(resolution.Succeeded);
        Assert.Equal(new DateTimeOffset(fallBackDate.Year, fallBackDate.Month, fallBackDate.Day, 0, 30, 0, TimeSpan.Zero), resolution.StartUtc);
    }

    [Fact]
    public void TryResolveOccurrence_ForTheSameAmbiguousLocalTime_AlwaysResolvesToTheSameSingleInstant_NeverTwo()
    {
        // Calling it twice for the identical input must be perfectly deterministic - no coin flip, no
        // silent duplicate occurrence.
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Sarajevo");
        var fallBackDate = LastSundayOfMonth(DateTime.UtcNow.Year + 1, 10);

        var first = RecurringOccurrenceGenerator.TryResolveOccurrence(fallBackDate, new TimeOnly(2, 30), new TimeOnly(5, 0), timeZone);
        var second = RecurringOccurrenceGenerator.TryResolveOccurrence(fallBackDate, new TimeOnly(2, 30), new TimeOnly(5, 0), timeZone);

        Assert.Equal(first.StartUtc, second.StartUtc);
    }

    [Fact]
    public void TryResolveOccurrence_ForANonDstDateInANonUtcTimeZone_ResolvesUsingThatZonesFixedOffset()
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Sarajevo");
        var winterDate = new DateOnly(DateTime.UtcNow.Year + 1, 1, 15); // unambiguously CET (UTC+1)

        var resolution = RecurringOccurrenceGenerator.TryResolveOccurrence(winterDate, new TimeOnly(9, 0), new TimeOnly(10, 0), timeZone);

        Assert.True(resolution.Succeeded);
        Assert.Equal(new DateTimeOffset(winterDate.Year, winterDate.Month, winterDate.Day, 8, 0, 0, TimeSpan.Zero), resolution.StartUtc);
    }
}
