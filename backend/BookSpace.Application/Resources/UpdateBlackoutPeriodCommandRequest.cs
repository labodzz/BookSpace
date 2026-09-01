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
