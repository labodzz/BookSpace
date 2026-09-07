using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Application.Security;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Bookings;

public sealed record GetRecurringSeriesQueryRequest(Guid Id) : IRequest<GetRecurringSeriesResponse>;

public sealed record GetRecurringSeriesResponse(
    Guid Id,
    Guid ResourceId,
    DateOnly StartDate,
    TimeOnly StartTime,
    TimeOnly EndTime,
    RecurrenceFrequency Frequency,
    int Interval,
    DateOnly? EndDate,
    int? OccurrenceCount,
    int Quantity,
    IReadOnlyList<GetRecurringSeriesOccurrenceResponse> Occurrences);

public sealed record GetRecurringSeriesOccurrenceResponse(Guid Id, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Quantity, BookingStatus Status);

// Owner-only, same "NotFound not Forbidden" convention as booking cancellation - a series belonging to
// another user (or another tenant, via the global query filter on IRecurringSeriesRepository) looks
// identical to a series that doesn't exist.
public sealed class GetRecurringSeriesQueryHandler(
    IRecurringSeriesRepository recurringSeriesRepository, IBookingRepository bookingRepository, ICurrentUserContext currentUserContext)
    : IRequestHandler<GetRecurringSeriesQueryRequest, GetRecurringSeriesResponse>
{
    public async Task<GetRecurringSeriesResponse> Handle(GetRecurringSeriesQueryRequest request, CancellationToken cancellationToken)
    {
        var series = await recurringSeriesRepository.FindByIdAsync(request.Id, cancellationToken);
        if (series is null || series.UserId != currentUserContext.UserId)
        {
            throw new NotFoundException($"Recurring series {request.Id} was not found.", ErrorCodes.RecurringSeriesNotFound);
        }

        var occurrences = await bookingRepository.GetBySeriesIdAsync(series.Id, cancellationToken);

        return new GetRecurringSeriesResponse(
            series.Id,
            series.ResourceId,
            series.StartDate,
            series.StartTime,
            series.EndTime,
            series.Frequency,
            series.Interval,
            series.EndDate,
            series.OccurrenceCount,
            series.Quantity,
            occurrences
                .OrderBy(occurrence => occurrence.StartUtc)
                .Select(occurrence => new GetRecurringSeriesOccurrenceResponse(occurrence.Id, occurrence.StartUtc, occurrence.EndUtc, occurrence.Quantity, occurrence.Status))
                .ToList());
    }
}
