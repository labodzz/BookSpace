namespace BookSpace.Application.Resources;

// Pure interval-math helpers for the availability algorithm - no DB/DI dependencies, independently
// unit-testable.
internal static class IntervalMath
{
    // Sorts by Start and merges overlapping or touching intervals into their maximal span.
    public static IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> Merge(
        IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> intervals)
    {
        var merged = new List<(DateTimeOffset Start, DateTimeOffset End)>();

        foreach (var interval in intervals.OrderBy(i => i.Start))
        {
            if (merged.Count > 0 && interval.Start <= merged[^1].End)
            {
                var last = merged[^1];
                merged[^1] = (last.Start, interval.End > last.End ? interval.End : last.End);
            }
            else
            {
                merged.Add(interval);
            }
        }

        return merged;
    }

    // Splits `window` into sub-intervals at every occupancy boundary, then reports how much of
    // `capacity` is left over in each sub-interval - each occupancy consumes its full `Amount` for
    // as long as it overlaps a sub-interval (occupancies from different bookings stack additively).
    // Sub-intervals with nothing left over (AvailableCapacity <= 0) are dropped rather than returned
    // as zero, since the caller only cares about slots that are actually bookable.
    public static IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End, int AvailableCapacity)> ComputeAvailableCapacity(
        (DateTimeOffset Start, DateTimeOffset End) window,
        int capacity,
        IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End, int Amount)> occupancies)
    {
        var boundaries = new SortedSet<DateTimeOffset> { window.Start, window.End };
        var clipped = new List<(DateTimeOffset Start, DateTimeOffset End, int Amount)>();

        foreach (var occupancy in occupancies)
        {
            var start = occupancy.Start > window.Start ? occupancy.Start : window.Start;
            var end = occupancy.End < window.End ? occupancy.End : window.End;
            if (end <= start)
            {
                continue;
            }

            clipped.Add((start, end, occupancy.Amount));
            boundaries.Add(start);
            boundaries.Add(end);
        }

        var points = boundaries.ToList();
        var slots = new List<(DateTimeOffset Start, DateTimeOffset End, int AvailableCapacity)>();

        for (var i = 0; i < points.Count - 1; i++)
        {
            var (start, end) = (points[i], points[i + 1]);
            var used = clipped.Where(occupancy => occupancy.Start <= start && occupancy.End >= end).Sum(occupancy => occupancy.Amount);
            var available = capacity - used;

            if (available > 0)
            {
                slots.Add((start, end, available));
            }
        }

        return slots;
    }

    // True if `periods` (need not be pre-merged, pre-sorted, or non-overlapping) collectively cover the
    // entire [window.Start, window.End) span with no gap. Used by Booking-create to check "is my
    // requested window entirely within the resource's open hours" (passing the resource's open periods)
    // and, separately, "does capacity hold for my full requested duration" (passing only the capacity
    // sub-intervals that meet the requested quantity) - see CreateBookingCommandHandler.
    public static bool Covers(
        (DateTimeOffset Start, DateTimeOffset End) window, IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> periods)
    {
        var cursor = window.Start;

        foreach (var period in periods.OrderBy(period => period.Start))
        {
            if (period.Start > cursor)
            {
                break;
            }

            if (period.End > cursor)
            {
                cursor = period.End;
            }

            if (cursor >= window.End)
            {
                return true;
            }
        }

        return false;
    }
}
