using BookSpace.Application.Common;
using BookSpace.Application.Resources;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Bookings;

internal enum BookingEligibility { Eligible, ResourceUnavailable, OutsideAvailability, BlackoutConflict, CapacityExceeded }

// The single check sequence (status -> availability -> blackout -> capacity) shared by
// CreateBookingCommandHandler, CreateRecurringSeriesCommandHandler (once per candidate occurrence), and
// ApproveBookingCommandHandler (re-check before flipping Pending -> Confirmed) - written once so all
// three can never silently drift apart. Pure read-only checking - callers own the transaction/lock and
// the actual write.
//
// Takes already-loaded rules/blackouts rather than fetching them itself: a single booking/approval check
// only ever calls this once, so the caller fetching them first costs nothing extra, but a recurring
// series checks every candidate occurrence (up to RecurringOccurrenceGenerator.MaxOccurrences) against
// the SAME resource's rules/blackouts - fetching those once outside the loop instead of once per
// occurrence avoids hundreds of redundant, identical round-trips for a long series.
internal static class BookingEligibilityChecker
{
    public static async Task<BookingEligibility> CheckAsync(
        Resource resource,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int quantity,
        Guid? excludeBookingId,
        IReadOnlyList<AvailabilityRule> rules,
        IReadOnlyList<BlackoutPeriod> blackouts,
        IBookingAvailabilityRepository bookingAvailabilityRepository,
        CancellationToken cancellationToken)
    {
        if (resource.Status != ResourceStatus.Active)
        {
            return BookingEligibility.ResourceUnavailable;
        }

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(resource.TimeZoneId);
        var fromDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(startUtc, timeZone).Date);
        var toDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(endUtc, timeZone).Date);

        var openPeriods = AvailabilityCalculator.ComputeOpenPeriodsUtc(rules, fromDate, toDate, timeZone);
        var window = (startUtc, endUtc);

        if (!IntervalMath.Covers(window, openPeriods))
        {
            return BookingEligibility.OutsideAvailability;
        }

        if (blackouts.Any(period => period.StartUtc < endUtc && period.EndUtc > startUtc))
        {
            return BookingEligibility.BlackoutConflict;
        }

        var overlappingBookings = await bookingAvailabilityRepository.GetActiveBookingsAsync(resource.Id, startUtc, endUtc, cancellationToken);
        // Excludes the booking being re-checked (e.g. at approval time) from its own demand sum - it's
        // already counted as active (Pending counts the same as Confirmed), so including it here would
        // make every approval recheck fail by double-counting the booking against itself.
        var occupancies = overlappingBookings
            .Where(booking => booking.Id != excludeBookingId)
            .Select(booking => (booking.StartUtc, booking.EndUtc, Amount: booking.Quantity))
            .ToList();
        var availableSlots = IntervalMath.ComputeAvailableCapacity(window, resource.Capacity, occupancies);
        var usableSlots = availableSlots.Where(slot => slot.AvailableCapacity >= quantity).Select(slot => (slot.Start, slot.End));

        return IntervalMath.Covers(window, usableSlots) ? BookingEligibility.Eligible : BookingEligibility.CapacityExceeded;
    }

    // The single place that maps a rejection to its client-facing ErrorCode - CreateRecurringSeriesCommandHandler
    // uses this directly to label a per-occurrence conflict without throwing (a series keeps evaluating
    // every candidate occurrence); Create/ApproveBookingCommandHandler go through ThrowIfNotEligible below.
    public static string ToErrorCode(BookingEligibility eligibility) => eligibility switch
    {
        BookingEligibility.ResourceUnavailable => ErrorCodes.BookingResourceUnavailable,
        BookingEligibility.OutsideAvailability => ErrorCodes.BookingOutsideAvailability,
        BookingEligibility.BlackoutConflict => ErrorCodes.BookingBlackoutConflict,
        BookingEligibility.CapacityExceeded => ErrorCodes.BookingCapacityExceeded,
        _ => throw new InvalidOperationException($"'{eligibility}' is not a rejection and has no ErrorCode."),
    };

    public static void ThrowIfNotEligible(BookingEligibility eligibility, Guid resourceId)
    {
        if (eligibility == BookingEligibility.Eligible)
        {
            return;
        }

        var message = eligibility switch
        {
            BookingEligibility.ResourceUnavailable => $"Resource {resourceId} is not available for booking.",
            BookingEligibility.OutsideAvailability => "The requested time is outside the resource's availability.",
            BookingEligibility.BlackoutConflict => "The requested time overlaps a blackout period.",
            BookingEligibility.CapacityExceeded => "The requested time does not have enough remaining capacity.",
            _ => throw new InvalidOperationException($"Unhandled booking eligibility status '{eligibility}'."),
        };

        throw new ConflictException(message, ToErrorCode(eligibility));
    }
}
