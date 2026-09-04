using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using FluentValidation;

namespace BookSpace.Application.Resources;

public sealed record UpdateBlackoutPeriodCommandRequest(Guid ResourceId, Guid BlackoutId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Reason)
    : IRequest<UpdateBlackoutPeriodResponse>;

public sealed record UpdateBlackoutPeriodResponse(Guid Id, Guid ResourceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Reason);

public sealed class UpdateBlackoutPeriodCommandRequestValidator : AbstractValidator<UpdateBlackoutPeriodCommandRequest>
{
    public UpdateBlackoutPeriodCommandRequestValidator()
    {
        RuleFor(command => command.ResourceId).NotEmpty();
        RuleFor(command => command.BlackoutId).NotEmpty();
        RuleFor(command => command.EndUtc).GreaterThan(command => command.StartUtc);
        RuleFor(command => command.Reason).NotEmpty().MaximumLength(1000);
    }
}

public sealed class UpdateBlackoutPeriodCommandHandler(IBlackoutPeriodRepository blackoutPeriodRepository)
    : IRequestHandler<UpdateBlackoutPeriodCommandRequest, UpdateBlackoutPeriodResponse>
{
    public async Task<UpdateBlackoutPeriodResponse> Handle(UpdateBlackoutPeriodCommandRequest request, CancellationToken cancellationToken)
    {
        var period = await blackoutPeriodRepository.FindByIdAsync(request.BlackoutId, cancellationToken);
        if (period is null || period.ResourceId != request.ResourceId)
        {
            throw new NotFoundException(
                $"Blackout period {request.BlackoutId} was not found for resource {request.ResourceId}.",
                ErrorCodes.BlackoutPeriodNotFound);
        }

        // Deliberately NOT "StartUtc must be >= now" (that would reject a harmless Reason-only edit on
        // a blackout that has already started or fully elapsed). The actual invariant is narrower: you
        // may not backdate a blackout further into the past than it already was. Moving StartUtc earlier
        // is fine as long as the result is still in the future (e.g. starting the block a day sooner),
        // and leaving StartUtc untouched or moving it later is always fine regardless of how far in the
        // past it already sits.
        if (request.StartUtc < period.StartUtc && request.StartUtc < DateTimeOffset.UtcNow)
        {
            throw new ConflictException("Cannot move a blackout period's start further into the past.");
        }

        // Plain full replacement - no booking-conflict recheck exists yet, since Booking read access
        // (IBookingAvailabilityRepository) doesn't land until the availability query work. Once it
        // does, this is the natural place for a diff-based recheck: only the newly-exposed time range
        // (new interval minus old interval) needs to be checked against future Confirmed bookings,
        // not the whole interval - shortening a blackout never needs a recheck, only widening it does.
        period.StartUtc = request.StartUtc;
        period.EndUtc = request.EndUtc;
        period.Reason = request.Reason;

        await blackoutPeriodRepository.SaveChangesAsync(cancellationToken);

        return new UpdateBlackoutPeriodResponse(period.Id, period.ResourceId, period.StartUtc, period.EndUtc, period.Reason);
    }
}
