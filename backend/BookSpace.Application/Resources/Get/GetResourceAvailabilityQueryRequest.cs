using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
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

        var bookableSlots = openPeriods
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

    // Converts each window's local start/end to UTC independently. Correct unless a window straddles
    // a DST transition instant in `timeZone` (rare for normal business hours) - in that case the UTC
    // duration would be off by the DST delta. Blackouts/bookings need no such handling since they're
    // already stored as absolute UTC. Accepted as a documented v1 simplification.
    private static DateTimeOffset ConvertLocalToUtc(DateOnly date, TimeOnly time, TimeZoneInfo timeZone)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(local, timeZone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }
}
