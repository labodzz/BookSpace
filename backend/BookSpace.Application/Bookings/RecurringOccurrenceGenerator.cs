using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Bookings;

// One resolved (or rejected) occurrence date. Deliberately does NOT reuse AvailabilityCalculator's
// ConvertLocalToUtc - recurrence generation needs a different DST policy than continuous availability
// windows do (see docs/recurring-bookings-and-approvals.md for the full reasoning): a spring-forward gap
// never silently shifts a discrete calendar occurrence to a different time, and a fall-back ambiguity is
// resolved deterministically to the EARLIER instant rather than the later/standard offset
// ConvertLocalToUtc uses for open-hours definitions.
internal readonly record struct OccurrenceResolution(DateOnly Date, DateTimeOffset? StartUtc, DateTimeOffset? EndUtc, string? ConflictReason)
{
    public bool Succeeded => ConflictReason is null;

    public static OccurrenceResolution Success(DateOnly date, DateTimeOffset startUtc, DateTimeOffset endUtc) => new(date, startUtc, endUtc, null);

    public static OccurrenceResolution Conflict(DateOnly date, string reason) => new(date, null, null, reason);
}

internal static class RecurringOccurrenceGenerator
{
    // Defense in depth against a pathological request generating an unbounded number of candidate
    // dates - CreateRecurringSeriesCommandRequestValidator already rejects a request that would exceed
    // this at the API boundary; this is a hard backstop inside the generator itself.
    public const int MaxOccurrences = 750;

    // Every candidate date is computed directly from series.StartDate (occurrenceIndex * step), never
    // chained from the previous date - this matters specifically for Monthly, where chaining would let
    // a clamped short month (e.g. Jan 31 -> Feb 28) permanently shift every later occurrence's day of
    // month instead of returning to 31 in March.
    public static IReadOnlyList<DateOnly> GenerateCandidateDates(RecurringSeries series)
    {
        var dates = new List<DateOnly>();

        for (var occurrenceIndex = 0; occurrenceIndex < MaxOccurrences; occurrenceIndex++)
        {
            var date = series.Frequency switch
            {
                RecurrenceFrequency.Daily => series.StartDate.AddDays(occurrenceIndex * series.Interval),
                RecurrenceFrequency.Weekly => series.StartDate.AddDays(occurrenceIndex * 7 * series.Interval),
                // DateOnly.AddMonths clamps the day to the target month's last valid day when the
                // anchor day doesn't exist there (e.g. Jan 31 + 1 month = Feb 28/29) - exactly this
                // series' documented monthly policy, with no extra clamping code needed.
                RecurrenceFrequency.Monthly => series.StartDate.AddMonths(occurrenceIndex * series.Interval),
                _ => throw new NotSupportedException($"Recurrence frequency '{series.Frequency}' is not supported for series generation."),
            };

            if (series.EndDate is { } endDate && date > endDate)
            {
                break;
            }

            if (series.OccurrenceCount is { } count && occurrenceIndex >= count)
            {
                break;
            }

            dates.Add(date);
        }

        return dates;
    }

    // Explicit DST edge-case policy for recurrence, deliberately different from ConvertLocalToUtc:
    //  - Spring-forward gap (StartTime or EndTime falls in a local time that never occurred that day):
    //    never shift - the occurrence is reported as a NonexistentLocalTime conflict for the caller to
    //    surface, not silently rescheduled.
    //  - Fall-back ambiguity (a local time that occurred twice): resolved to the EARLIER of the two
    //    valid UTC instants, deterministically, via IsAmbiguousTime/GetAmbiguousTimeOffsets - never both,
    //    never a coin flip.
    public static OccurrenceResolution TryResolveOccurrence(DateOnly date, TimeOnly startTime, TimeOnly endTime, TimeZoneInfo timeZone)
    {
        var localStart = DateTime.SpecifyKind(date.ToDateTime(startTime), DateTimeKind.Unspecified);
        var localEnd = DateTime.SpecifyKind(date.ToDateTime(endTime), DateTimeKind.Unspecified);

        if (timeZone.IsInvalidTime(localStart) || timeZone.IsInvalidTime(localEnd))
        {
            return OccurrenceResolution.Conflict(date, "NonexistentLocalTime");
        }

        return OccurrenceResolution.Success(date, ResolveToUtc(localStart, timeZone), ResolveToUtc(localEnd, timeZone));
    }

    private static DateTimeOffset ResolveToUtc(DateTime local, TimeZoneInfo timeZone)
    {
        if (timeZone.IsAmbiguousTime(local))
        {
            // UTC = local - offset, so the LARGER of the two possible offsets yields the EARLIER UTC
            // instant for the same local wall-clock time - that's the daylight (pre-transition) offset,
            // physically the first of the two times this local clock reading occurs that day.
            var earlierOffset = timeZone.GetAmbiguousTimeOffsets(local).Max();
            return new DateTimeOffset(local, earlierOffset).ToUniversalTime();
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone), TimeSpan.Zero);
    }
}
