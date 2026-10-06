using BookSpace.Application.Bookings;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Xunit;

namespace BookSpace.Application.Tests.Bookings;

public sealed class CheckInEligibilityCheckerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly DateTimeOffset StartUtc = DateTimeOffset.Parse("2026-09-02T10:00:00Z");

    private static Booking CreateBooking(BookingStatus status = BookingStatus.Confirmed, DateTimeOffset? checkedInAtUtc = null) => new()
    {
        Id = Guid.NewGuid(), TenantId = TenantId, ResourceId = Guid.NewGuid(), UserId = Guid.NewGuid(),
        StartUtc = StartUtc, EndUtc = StartUtc.AddHours(1), Quantity = 1, Status = status,
        CheckedInAtUtc = checkedInAtUtc, CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private static Tenant CreateTenant(int checkInLeadMinutes = 15, int noShowGraceMinutes = 15) => new()
    {
        Id = TenantId, Name = "Test Tenant", DefaultTimeZoneId = "UTC", CreatedAtUtc = DateTimeOffset.UtcNow,
        CheckInLeadMinutes = checkInLeadMinutes, NoShowGraceMinutes = noShowGraceMinutes,
    };

    [Fact]
    public void Evaluate_ExactlyAtWindowOpen_IsEligible()
    {
        var result = CheckInEligibilityChecker.Evaluate(CreateBooking(), CreateTenant(), StartUtc.AddMinutes(-15));

        Assert.Equal(CheckInEligibility.Eligible, result);
    }

    [Fact]
    public void Evaluate_OneSecondBeforeWindowOpens_IsTooEarly()
    {
        var result = CheckInEligibilityChecker.Evaluate(CreateBooking(), CreateTenant(), StartUtc.AddMinutes(-15).AddSeconds(-1));

        Assert.Equal(CheckInEligibility.TooEarly, result);
    }

    [Fact]
    public void Evaluate_ExactlyAtWindowClose_IsEligible()
    {
        var result = CheckInEligibilityChecker.Evaluate(CreateBooking(), CreateTenant(), StartUtc.AddMinutes(15));

        Assert.Equal(CheckInEligibility.Eligible, result);
    }

    [Fact]
    public void Evaluate_OneSecondAfterWindowCloses_IsTooLate()
    {
        var result = CheckInEligibilityChecker.Evaluate(CreateBooking(), CreateTenant(), StartUtc.AddMinutes(15).AddSeconds(1));

        Assert.Equal(CheckInEligibility.TooLatePastGracePeriod, result);
    }

    [Fact]
    public void Evaluate_ExactlyAtStartUtc_IsEligible()
    {
        var result = CheckInEligibilityChecker.Evaluate(CreateBooking(), CreateTenant(), StartUtc);

        Assert.Equal(CheckInEligibility.Eligible, result);
    }

    [Theory]
    [InlineData(BookingStatus.Pending)]
    [InlineData(BookingStatus.Rejected)]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.Completed)]
    [InlineData(BookingStatus.NoShow)]
    [InlineData(BookingStatus.Expired)]
    public void Evaluate_NonConfirmedStatus_IsNotConfirmed_EvenWithinTheWindow(BookingStatus status)
    {
        var result = CheckInEligibilityChecker.Evaluate(CreateBooking(status), CreateTenant(), StartUtc);

        Assert.Equal(CheckInEligibility.NotConfirmed, result);
    }

    [Fact]
    public void Evaluate_AlreadyCheckedIn_IsAlreadyCheckedIn_EvenOutsideTheWindow()
    {
        // Far past the grace period - proves AlreadyCheckedIn short-circuits every other check: confirming
        // an existing check-in must never fail just because the window has since closed.
        var booking = CreateBooking(checkedInAtUtc: StartUtc.AddMinutes(5));

        var result = CheckInEligibilityChecker.Evaluate(booking, CreateTenant(), StartUtc.AddDays(1));

        Assert.Equal(CheckInEligibility.AlreadyCheckedIn, result);
    }

    [Fact]
    public void Evaluate_RespectsPerTenantCheckInLeadMinutes_NotAHardcodedValue()
    {
        var tenant = CreateTenant(checkInLeadMinutes: 30);

        Assert.Equal(CheckInEligibility.Eligible, CheckInEligibilityChecker.Evaluate(CreateBooking(), tenant, StartUtc.AddMinutes(-30)));
        Assert.Equal(CheckInEligibility.TooEarly, CheckInEligibilityChecker.Evaluate(CreateBooking(), tenant, StartUtc.AddMinutes(-31)));
    }

    [Fact]
    public void Evaluate_RespectsPerTenantNoShowGraceMinutes_NotAHardcodedValue()
    {
        var tenant = CreateTenant(noShowGraceMinutes: 5);

        Assert.Equal(CheckInEligibility.Eligible, CheckInEligibilityChecker.Evaluate(CreateBooking(), tenant, StartUtc.AddMinutes(5)));
        Assert.Equal(CheckInEligibility.TooLatePastGracePeriod, CheckInEligibilityChecker.Evaluate(CreateBooking(), tenant, StartUtc.AddMinutes(6)));
    }
}
