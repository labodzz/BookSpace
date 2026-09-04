using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Domain.Enums;
using FluentValidation;

namespace BookSpace.Application.Resources;

public sealed record GetResourceAvailabilityQueryRequest(Guid ResourceId, DateOnly FromDate, DateOnly ToDate)
    : IRequest<GetResourceAvailabilityResponse>;

public sealed record OpenPeriodResponse(DateTimeOffset StartUtc, DateTimeOffset EndUtc);

public sealed record BlackoutResponse(DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Reason);

public sealed record BusyPeriodResponse(DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Quantity);

public sealed record BookableSlotResponse(DateTimeOffset StartUtc, DateTimeOffset EndUtc, int AvailableCapacity);

public sealed record GetResourceAvailabilityResponse(
    Guid ResourceId,
    DateOnly FromDate,
    DateOnly ToDate,
    string TimeZoneId,
    int Capacity,
    IReadOnlyList<OpenPeriodResponse> OpenPeriods,
    IReadOnlyList<BlackoutResponse> Blackouts,
    IReadOnlyList<BusyPeriodResponse> BusyPeriods,
    IReadOnlyList<BookableSlotResponse> BookableSlots);

public sealed class GetResourceAvailabilityQueryRequestValidator : AbstractValidator<GetResourceAvailabilityQueryRequest>
{
    private const int MaxRangeDays = 92;

    public GetResourceAvailabilityQueryRequestValidator()
    {
        RuleFor(query => query.ResourceId).NotEmpty();
        RuleFor(query => query.ToDate).GreaterThanOrEqualTo(query => query.FromDate);
        RuleFor(query => query)
            .Must(query => query.ToDate.DayNumber - query.FromDate.DayNumber < MaxRangeDays)
            .WithMessage($"Date range cannot exceed {MaxRangeDays} days.")
            .WithName(nameof(GetResourceAvailabilityQueryRequest.ToDate));
    }
}

// Capacity-aware: a slot stays bookable as long as some capacity remains, not just when it's fully
// free - e.g. an 8-laptop resource with one 5-laptop booking still reports 3 available for that
// overlap, rather than treating the whole slot as blocked. Blackout periods are the one exception -
// they consume the resource's FULL capacity for their duration regardless of what capacity value is
// configured, since a blackout means the resource itself is unavailable, not merely busy.
public sealed class GetResourceAvailabilityQueryHandler(
    IResourceRepository resourceRepository,
    IAvailabilityRuleRepository availabilityRuleRepository,
    IBlackoutPeriodRepository blackoutPeriodRepository,
    IBookingAvailabilityRepository bookingAvailabilityRepository)
    : IRequestHandler<GetResourceAvailabilityQueryRequest, GetResourceAvailabilityResponse>
{
    public async Task<GetResourceAvailabilityResponse> Handle(GetResourceAvailabilityQueryRequest request, CancellationToken cancellationToken)
    {
        var resource = await resourceRepository.FindByIdAsync(request.ResourceId, cancellationToken)
            ?? throw new NotFoundException($"Resource {request.ResourceId} was not found.", ErrorCodes.ResourceNotFound);

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(resource.TimeZoneId);

        var rangeStartUtc = ConvertLocalToUtc(request.FromDate, TimeOnly.MinValue, timeZone);
        var rangeEndUtcExclusive = ConvertLocalToUtc(request.ToDate.AddDays(1), TimeOnly.MinValue, timeZone);

        var rules = await availabilityRuleRepository.GetByResourceIdAsync(resource.Id, cancellationToken);
        var rulesByDay = rules.GroupBy(rule => rule.DayOfWeek).ToDictionary(group => group.Key, group => group.ToList());

        var blackoutsInRange = (await blackoutPeriodRepository.GetByResourceIdAsync(resource.Id, cancellationToken))
            .Where(period => period.StartUtc < rangeEndUtcExclusive && period.EndUtc > rangeStartUtc)
            .ToList();

        var bookings = await bookingAvailabilityRepository.GetActiveBookingsAsync(
            resource.Id, rangeStartUtc, rangeEndUtcExclusive, cancellationToken);

        var openPeriods = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        for (var date = request.FromDate; date <= request.ToDate; date = date.AddDays(1))
        {
            if (!rulesByDay.TryGetValue(date.DayOfWeek, out var dayRules))
            {
                continue;
            }

            var dayWindows = dayRules.Select(rule =>
                (ConvertLocalToUtc(date, rule.StartTime, timeZone), ConvertLocalToUtc(date, rule.EndTime, timeZone)));
            openPeriods.AddRange(IntervalMath.Merge(dayWindows));
        }

        var occupancies = blackoutsInRange
            .Select(period => (period.StartUtc, period.EndUtc, Amount: resource.Capacity))
            .Concat(bookings.Select(booking => (booking.StartUtc, booking.EndUtc, Amount: booking.Quantity)))
            .ToList();

        // A resource that isn't Active isn't bookable at all, regardless of what its schedule/blackouts/
        // bookings would otherwise compute - Maintenance and Inactive both mean "not available for use
        // right now", the same way a blackout means "unavailable" independent of configured capacity.
        // OpenPeriods/Blackouts/BusyPeriods still reflect the resource's configured schedule and real
        // occupancy either way, so a caller can see why nothing is bookable.
        var bookableSlots = resource.Status != ResourceStatus.Active
            ? []
            : openPeriods
                .SelectMany(window => IntervalMath.ComputeAvailableCapacity(window, resource.Capacity, occupancies))
                .OrderBy(slot => slot.Start)
                .Select(slot => new BookableSlotResponse(slot.Start, slot.End, slot.AvailableCapacity))
                .ToList();

        return new GetResourceAvailabilityResponse(
            resource.Id,
            request.FromDate,
            request.ToDate,
            resource.TimeZoneId,
            resource.Capacity,
            openPeriods.OrderBy(period => period.Start).Select(period => new OpenPeriodResponse(period.Start, period.End)).ToList(),
            blackoutsInRange.Select(period => new BlackoutResponse(period.StartUtc, period.EndUtc, period.Reason)).ToList(),
            bookings.Select(booking => new BusyPeriodResponse(booking.StartUtc, booking.EndUtc, booking.Quantity)).ToList(),
            bookableSlots);
    }

    // Converts each window's local start/end to UTC independently - each endpoint resolves to ITS OWN
    // correct offset for that specific instant, which is not an approximation. A window that straddles
    // a DST transition still produces the physically correct UTC duration: independently resolving
    // each endpoint inherently accounts for the transition (a window spanning a spring-forward gap is
    // genuinely shorter by the gap's size in real elapsed time, and one spanning a fall-back is
    // genuinely longer by the same amount - the code reflects both correctly, verified with an exact
    // expected duration by Handle_WithRuleSpanningTheSpringForwardTransition_ProducesThePhysicallyCorrectDuration
    // and Handle_WithRuleSpanningTheFallBackTransition_ProducesThePhysicallyCorrectDuration). There is
    // no "duration off by the DST delta" defect here, contrary to an earlier, unverified assumption in
    // this comment - the only real hazard is a single ENDPOINT landing inside a gap or ambiguous hour,
    // which the two rules below resolve to one well-defined instant before any duration math happens.
    //
    // Explicit DST edge-case policy (there is no public .NET API to hand this off to - see the
    // TimeZoneInfoOptions history in this file's git blame for why a "just pass NoThrowOnInvalidTime"
    // fix doesn't compile):
    //  - Spring-forward gap (a local time that never occurred, e.g. 02:30 on the day clocks jump from
    //    02:00 to 03:00): normalized forward past the gap by the gap's own size, rather than throwing.
    //    Real-world DST gaps are 1 hour; IsDaylightSavingTime one hour later than a still-invalid time
    //    confirms the gap has been fully crossed even in the rare case of a larger historical offset
    //    change, without hardcoding "1 hour" as a magic constant.
    //  - Fall-back ambiguity (a local time that occurred twice, e.g. 02:30 on the day clocks fall from
    //    03:00 to 02:00): resolved via .NET's own documented default for ConvertTimeToUtc - the
    //    standard (post-transition, non-daylight) offset is used, i.e. the LATER of the two occurrences.
    //    This is an explicit choice to rely on, not an accident: proven by
    //    Handle_WithRuleStartingInsideFallBackAmbiguousHour_ResolvesToStandardOffsetWithoutThrowing.
    private static DateTimeOffset ConvertLocalToUtc(DateOnly date, TimeOnly time, TimeZoneInfo timeZone)
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
