using BookSpace.Application.Resources;
using Xunit;

namespace BookSpace.Application.Tests.Resources;

public sealed class IntervalMathTests
{
    private static readonly DateTimeOffset Anchor = new(2026, 9, 7, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int hour) => Anchor.AddHours(hour);

    [Fact]
    public void Merge_WithDisjointIntervals_ReturnsBothUnchanged()
    {
        var result = IntervalMath.Merge([(At(8), At(10)), (At(14), At(16))]);

        Assert.Equal(2, result.Count);
        Assert.Equal((At(8), At(10)), result[0]);
        Assert.Equal((At(14), At(16)), result[1]);
    }

    [Fact]
    public void Merge_WithAdjacentIntervals_MergesIntoOne()
    {
        var result = IntervalMath.Merge([(At(8), At(12)), (At(12), At(16))]);

        var merged = Assert.Single(result);
        Assert.Equal((At(8), At(16)), merged);
    }

    [Fact]
    public void Merge_WithOverlappingIntervals_MergesIntoOne()
    {
        var result = IntervalMath.Merge([(At(8), At(14)), (At(12), At(18))]);

        var merged = Assert.Single(result);
        Assert.Equal((At(8), At(18)), merged);
    }

    [Fact]
    public void Merge_WithEmptyInput_ReturnsEmpty()
    {
        var result = IntervalMath.Merge([]);

        Assert.Empty(result);
    }

    [Fact]
    public void ComputeAvailableCapacity_WithNoOccupancies_ReturnsFullWindowAtFullCapacity()
    {
        var result = IntervalMath.ComputeAvailableCapacity((At(8), At(18)), capacity: 8, occupancies: []);

        var slot = Assert.Single(result);
        Assert.Equal((At(8), At(18), 8), slot);
    }

    [Fact]
    public void ComputeAvailableCapacity_WithPartialOverlap_ReducesAvailableCapacityForThatSlotOnly()
    {
        // 8 laptops total; one booking takes 5 from 10:00-12:00 - matches the exact scenario that
        // drove the capacity-aware design decision.
        var result = IntervalMath.ComputeAvailableCapacity(
            (At(8), At(18)), capacity: 8, occupancies: [(At(10), At(12), 5)]);

        Assert.Equal(3, result.Count);
        Assert.Equal((At(8), At(10), 8), result[0]);
        Assert.Equal((At(10), At(12), 3), result[1]);
        Assert.Equal((At(12), At(18), 8), result[2]);
    }

    [Fact]
    public void ComputeAvailableCapacity_WithOccupancyConsumingFullCapacity_ExcludesThatSlot()
    {
        // Simulates a blackout, which is passed in with Amount == capacity regardless of the
        // resource's nominal capacity value.
        var result = IntervalMath.ComputeAvailableCapacity(
            (At(8), At(18)), capacity: 8, occupancies: [(At(12), At(13), 8)]);

        Assert.Equal(2, result.Count);
        Assert.Equal((At(8), At(12), 8), result[0]);
        Assert.Equal((At(13), At(18), 8), result[1]);
    }

    [Fact]
    public void ComputeAvailableCapacity_WithOverlappingOccupanciesExceedingCapacity_ExcludesOverbookedSlot()
    {
        var result = IntervalMath.ComputeAvailableCapacity(
            (At(8), At(18)), capacity: 8, occupancies: [(At(10), At(14), 5), (At(12), At(16), 4)]);

        // [10-12): 5 used, 3 left. [12-14): 5+4=9 used, over capacity, dropped. [14-16): 4 used, 4 left.
        Assert.Equal(4, result.Count);
        Assert.Equal((At(8), At(10), 8), result[0]);
        Assert.Equal((At(10), At(12), 3), result[1]);
        Assert.Equal((At(14), At(16), 4), result[2]);
        Assert.Equal((At(16), At(18), 8), result[3]);
    }

    [Fact]
    public void ComputeAvailableCapacity_WithOccupancyOutsideWindow_IsIgnored()
    {
        var result = IntervalMath.ComputeAvailableCapacity(
            (At(8), At(18)), capacity: 8, occupancies: [(At(20), At(22), 5)]);

        var slot = Assert.Single(result);
        Assert.Equal((At(8), At(18), 8), slot);
    }

    [Fact]
    public void Covers_WithOnePeriodFullyContainingWindow_ReturnsTrue()
    {
        Assert.True(IntervalMath.Covers((At(10), At(12)), [(At(8), At(18))]));
    }

    [Fact]
    public void Covers_WithTouchingPeriodsSpanningWindow_ReturnsTrue()
    {
        // Two periods that only touch at At(10), not overlap, still bridge the window - the same
        // adjacency-counts-as-covering semantics as Merge.
        Assert.True(IntervalMath.Covers((At(8), At(12)), [(At(8), At(10)), (At(10), At(12))]));
    }

    [Fact]
    public void Covers_WithGapInTheMiddle_ReturnsFalse()
    {
        Assert.False(IntervalMath.Covers((At(8), At(12)), [(At(8), At(9)), (At(10), At(12))]));
    }

    [Fact]
    public void Covers_WithNoPeriods_ReturnsFalse()
    {
        Assert.False(IntervalMath.Covers((At(8), At(12)), []));
    }

    [Fact]
    public void Covers_WithPeriodStartingAfterWindowStart_ReturnsFalse()
    {
        Assert.False(IntervalMath.Covers((At(8), At(12)), [(At(9), At(12))]));
    }

    [Fact]
    public void Covers_WithPeriodEndingExactlyAtWindowEnd_ReturnsTrue()
    {
        Assert.True(IntervalMath.Covers((At(8), At(12)), [(At(8), At(12))]));
    }

    [Fact]
    public void Covers_WithPeriodEndingJustBeforeWindowEnd_ReturnsFalse()
    {
        Assert.False(IntervalMath.Covers((At(8), At(12)), [(At(8), At(11).AddMinutes(59))]));
    }
}
