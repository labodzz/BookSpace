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

        var rangeStartUtc = AvailabilityCalculator.ConvertLocalToUtc(request.FromDate, TimeOnly.MinValue, timeZone);
        var rangeEndUtcExclusive = AvailabilityCalculator.ConvertLocalToUtc(request.ToDate.AddDays(1), TimeOnly.MinValue, timeZone);

        var rules = await availabilityRuleRepository.GetByResourceIdAsync(resource.Id, cancellationToken);

        var blackoutsInRange = (await blackoutPeriodRepository.GetByResourceIdAsync(resource.Id, cancellationToken))
            .Where(period => period.StartUtc < rangeEndUtcExclusive && period.EndUtc > rangeStartUtc)
            .ToList();

        var bookings = await bookingAvailabilityRepository.GetActiveBookingsAsync(
            resource.Id, rangeStartUtc, rangeEndUtcExclusive, cancellationToken);

        var openPeriods = AvailabilityCalculator.ComputeOpenPeriodsUtc(rules, request.FromDate, request.ToDate, timeZone);

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
}
