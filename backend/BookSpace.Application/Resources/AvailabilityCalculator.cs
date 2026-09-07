using BookSpace.Domain.Entities;

namespace BookSpace.Application.Resources;

// Shared between the availability read model (GetResourceAvailabilityQueryHandler), Booking-create's own
// pre-commit re-check (CreateBookingCommandHandler), and DevelopmentSeeder (Infrastructure - hence
// public, not internal), so all three compute "when is this resource open" the exact same way, including
// the DST edge-case policy on ConvertLocalToUtc - do not write a second, divergent conversion path (see
// docs/availability-and-timezones.md). RecurringOccurrenceGenerator deliberately does NOT reuse
// ConvertLocalToUtc - recurrence occurrence generation has its own, intentionally different DST policy;
// see docs/recurring-bookings-and-approvals.md for why.
public static class AvailabilityCalculator
{
    // Expands day-of-week AvailabilityRules into UTC open periods for each calendar date in
    // [fromDate, toDate], merging same-day rules that overlap or touch. Periods are NOT merged across
    // day boundaries, matching the original availability-query behavior this was extracted from.
    public static IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> ComputeOpenPeriodsUtc(
        IReadOnlyList<AvailabilityRule> rules, DateOnly fromDate, DateOnly toDate, TimeZoneInfo timeZone)
    {
        var rulesByDay = rules.GroupBy(rule => rule.DayOfWeek).ToDictionary(group => group.Key, group => group.ToList());
        var openPeriods = new List<(DateTimeOffset Start, DateTimeOffset End)>();

        for (var date = fromDate; date <= toDate; date = date.AddDays(1))
        {
            if (!rulesByDay.TryGetValue(date.DayOfWeek, out var dayRules))
            {
                continue;
            }

            var dayWindows = dayRules.Select(rule =>
                (ConvertLocalToUtc(date, rule.StartTime, timeZone), ConvertLocalToUtc(date, rule.EndTime, timeZone)));
            openPeriods.AddRange(IntervalMath.Merge(dayWindows));
        }

        return openPeriods;
    }

    // Converts each window's local start/end to UTC independently - each endpoint resolves to ITS OWN
    // correct offset for that specific instant, which is not an approximation. A window that straddles
    // a DST transition still produces the physically correct UTC duration: independently resolving
    // each endpoint inherently accounts for the transition (a window spanning a spring-forward gap is
    // genuinely shorter by the gap's size in real elapsed time, and one spanning a fall-back is
    // genuinely longer by the same amount).
    //
    // Explicit DST edge-case policy (there is no public .NET API to hand this off to):
    //  - Spring-forward gap (a local time that never occurred, e.g. 02:30 on the day clocks jump from
    //    02:00 to 03:00): normalized forward past the gap by the gap's own size, rather than throwing.
    //  - Fall-back ambiguity (a local time that occurred twice, e.g. 02:30 on the day clocks fall from
    //    03:00 to 02:00): resolved via .NET's own documented default for ConvertTimeToUtc - the
    //    standard (post-transition, non-daylight) offset is used, i.e. the LATER of the two occurrences.
    public static DateTimeOffset ConvertLocalToUtc(DateOnly date, TimeOnly time, TimeZoneInfo timeZone)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);

        while (timeZone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        var utc = TimeZoneInfo.ConvertTimeToUtc(local, timeZone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }
}
