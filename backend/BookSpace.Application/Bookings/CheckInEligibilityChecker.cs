using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Bookings;

internal enum CheckInEligibility
{
    Eligible,
    AlreadyCheckedIn,
    NotConfirmed,
    TooEarly,
    TooLatePastGracePeriod,
}

// Pure, static, and deliberately takes `nowUtc` as an explicit parameter (same shape as
// BookingEligibilityChecker) rather than reading DateTimeOffset.UtcNow itself - that's what lets the exact
// boundary tests (CheckInEligibilityCheckerTests) assert precise window-open/window-close instants without
// any clock abstraction or timing-dependent waits.
//
// Window: opens CheckInLeadMinutes before StartUtc, closes NoShowGraceMinutes after StartUtc - both
// boundaries INCLUSIVE (nowUtc == opensAtUtc is already open; nowUtc == closesAtUtc is not yet closed; see
// docs/bookings-and-concurrency.md, "Booking check-in - window boundaries"). AlreadyCheckedIn is checked
// FIRST and short-circuits every other check: a booking already checked in is never re-evaluated against
// the window, so confirming an existing check-in never fails just because the window has since closed.
internal static class CheckInEligibilityChecker
{
    public static CheckInEligibility Evaluate(Booking booking, Tenant tenant, DateTimeOffset nowUtc)
    {
        if (booking.CheckedInAtUtc is not null)
        {
            return CheckInEligibility.AlreadyCheckedIn;
        }

        if (booking.Status != BookingStatus.Confirmed)
        {
            return CheckInEligibility.NotConfirmed;
        }

        if (nowUtc < booking.StartUtc.AddMinutes(-tenant.CheckInLeadMinutes))
        {
            return CheckInEligibility.TooEarly;
        }

        if (nowUtc > booking.StartUtc.AddMinutes(tenant.NoShowGraceMinutes))
        {
            return CheckInEligibility.TooLatePastGracePeriod;
        }

        return CheckInEligibility.Eligible;
    }
}
