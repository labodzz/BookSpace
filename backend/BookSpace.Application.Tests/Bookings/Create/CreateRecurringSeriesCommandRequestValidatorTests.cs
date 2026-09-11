using BookSpace.Application.Bookings;
using BookSpace.Domain.Enums;
using Xunit;

namespace BookSpace.Application.Tests.Bookings;

public sealed class CreateRecurringSeriesCommandRequestValidatorTests
{
    private readonly CreateRecurringSeriesCommandRequestValidator _sut = new();

    private static readonly DateOnly StartDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(1));

    private static CreateRecurringSeriesCommandRequest ValidRequest(
        RecurrenceFrequency frequency = RecurrenceFrequency.Daily, int interval = 1,
        DateOnly? endDate = null, int? occurrenceCount = 10, int quantity = 1) =>
        new(Guid.NewGuid(), StartDate, new TimeOnly(9, 0), new TimeOnly(10, 0), frequency, interval, endDate, occurrenceCount, quantity);

    [Fact]
    public void Validate_WithValidRequest_HasNoErrors()
    {
        var result = _sut.Validate(ValidRequest());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WithEmptyResourceId_HasErrors()
    {
        var request = ValidRequest() with { ResourceId = Guid.Empty };

        Assert.False(_sut.Validate(request).IsValid);
    }

    [Fact]
    public void Validate_WithStartDateInThePast_HasErrors()
    {
        var request = ValidRequest() with { StartDate = StartDate.AddDays(-5) };

        Assert.False(_sut.Validate(request).IsValid);
    }

    [Fact]
    public void Validate_WithStartDateOfToday_HasNoErrors()
    {
        // The rule is startDate >= today - only strictly-past dates were previously tested; this proves
        // the boundary itself is inclusive, not just "not clearly in the past".
        var request = ValidRequest() with { StartDate = DateOnly.FromDateTime(DateTime.UtcNow) };

        Assert.True(_sut.Validate(request).IsValid);
    }

    [Fact]
    public void Validate_WithEndTimeNotAfterStartTime_HasErrors()
    {
        var request = ValidRequest() with { EndTime = new TimeOnly(9, 0) };

        Assert.False(_sut.Validate(request).IsValid);
    }

    [Fact]
    public void Validate_WithYearlyFrequency_HasErrors()
    {
        // Yearly exists in the RecurrenceFrequency enum (inherited from WP-1) but is deliberately not
        // wired up for series generation - only Daily/Weekly/Monthly are supported.
        var request = ValidRequest(frequency: RecurrenceFrequency.Yearly);

        Assert.False(_sut.Validate(request).IsValid);
    }

    [Fact]
    public void Validate_WithZeroInterval_HasErrors()
    {
        var request = ValidRequest(interval: 0);

        Assert.False(_sut.Validate(request).IsValid);
    }

    [Fact]
    public void Validate_WithZeroQuantity_HasErrors()
    {
        var request = ValidRequest(quantity: 0);

        Assert.False(_sut.Validate(request).IsValid);
    }

    [Fact]
    public void Validate_WithBothEndDateAndOccurrenceCount_HasErrors()
    {
        var request = ValidRequest(occurrenceCount: 10) with { EndDate = StartDate.AddMonths(1) };

        Assert.False(_sut.Validate(request).IsValid);
    }

    [Fact]
    public void Validate_WithNeitherEndDateNorOccurrenceCount_HasErrors()
    {
        var request = ValidRequest(occurrenceCount: null);

        Assert.False(_sut.Validate(request).IsValid);
    }

    [Fact]
    public void Validate_WithEndDateBeforeStartDate_HasErrors()
    {
        var request = ValidRequest(occurrenceCount: null) with { EndDate = StartDate.AddDays(-1) };

        Assert.False(_sut.Validate(request).IsValid);
    }

    [Fact]
    public void Validate_WithOccurrenceCountAboveMaxOccurrences_HasErrors()
    {
        var request = ValidRequest(occurrenceCount: RecurringOccurrenceGenerator.MaxOccurrences + 1);

        Assert.False(_sut.Validate(request).IsValid);
    }

    [Fact]
    public void Validate_WithOccurrenceCountAtExactlyMaxOccurrences_HasNoErrors()
    {
        var request = ValidRequest(occurrenceCount: RecurringOccurrenceGenerator.MaxOccurrences);

        Assert.True(_sut.Validate(request).IsValid);
    }

    [Fact]
    public void Validate_WithEndDateImplyingMoreThanMaxOccurrences_HasErrors()
    {
        // Daily for 3 years is well beyond MaxOccurrences (750).
        var request = ValidRequest(occurrenceCount: null) with { EndDate = StartDate.AddYears(3) };

        Assert.False(_sut.Validate(request).IsValid);
    }

    [Fact]
    public void Validate_DailyForTwoYears_HasNoErrors()
    {
        // The task's own reference ceiling: daily for 2 years is ~730 occurrences, within MaxOccurrences.
        var request = ValidRequest(occurrenceCount: null) with { EndDate = StartDate.AddYears(2) };

        Assert.True(_sut.Validate(request).IsValid);
    }

    // EstimateOccurrenceCount has a distinct arithmetic branch per frequency (totalDays/(7*interval)+1 for
    // Weekly, the conservative totalDays/(28*interval)+1 for Monthly) - only the Daily branch had a test
    // before this pair, so a wrong divisor in either would have gone undetected.
    [Fact]
    public void Validate_WeeklyEndDateImplyingMoreThanMaxOccurrences_HasErrors()
    {
        // MaxOccurrences (750) weeks is ~14.4 years; 20 years weekly is comfortably beyond that.
        var request = ValidRequest(frequency: RecurrenceFrequency.Weekly, occurrenceCount: null) with { EndDate = StartDate.AddYears(20) };

        Assert.False(_sut.Validate(request).IsValid);
    }

    [Fact]
    public void Validate_WeeklyForTwoYears_HasNoErrors()
    {
        // ~104 weekly occurrences over 2 years, well within MaxOccurrences.
        var request = ValidRequest(frequency: RecurrenceFrequency.Weekly, occurrenceCount: null) with { EndDate = StartDate.AddYears(2) };

        Assert.True(_sut.Validate(request).IsValid);
    }

    [Fact]
    public void Validate_MonthlyEndDateImplyingMoreThanMaxOccurrences_HasErrors()
    {
        // The conservative 28-day-per-month estimate means MaxOccurrences (750) months is ~57.5 years;
        // 100 years monthly is comfortably beyond that.
        var request = ValidRequest(frequency: RecurrenceFrequency.Monthly, occurrenceCount: null) with { EndDate = StartDate.AddYears(100) };

        Assert.False(_sut.Validate(request).IsValid);
    }

    [Fact]
    public void Validate_MonthlyForTwoYears_HasNoErrors()
    {
        var request = ValidRequest(frequency: RecurrenceFrequency.Monthly, occurrenceCount: null) with { EndDate = StartDate.AddYears(2) };

        Assert.True(_sut.Validate(request).IsValid);
    }
}
