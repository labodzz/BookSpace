using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using FluentValidation;

namespace BookSpace.Application.Resources;

public sealed record UpdateBlackoutPeriodCommandRequest(Guid ResourceId, Guid BlackoutId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Reason)
    : IRequest<UpdateBlackoutPeriodResponse>;

// ConflictingBookingIds - see CreateBlackoutPeriodResponse's identical field for why (surfaced, never
// blocking, never silently dropped).
public sealed record UpdateBlackoutPeriodResponse(
    Guid Id, Guid ResourceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Reason, IReadOnlyList<Guid> ConflictingBookingIds);

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

public sealed class UpdateBlackoutPeriodCommandHandler(
    IBlackoutPeriodRepository blackoutPeriodRepository, IResourceRepository resourceRepository,
    IBookingAvailabilityRepository bookingAvailabilityRepository)
    : IRequestHandler<UpdateBlackoutPeriodCommandRequest, UpdateBlackoutPeriodResponse>
{
    public async Task<UpdateBlackoutPeriodResponse> Handle(UpdateBlackoutPeriodCommandRequest request, CancellationToken cancellationToken)
    {
        var resource = await resourceRepository.FindByIdAsync(request.ResourceId, cancellationToken)
            ?? throw new NotFoundException($"Resource {request.ResourceId} was not found.", ErrorCodes.ResourceNotFound);

        // Archived is terminal, same as every other Create/Update handler in this feature
        // (CreateBlackoutPeriodCommandHandler, AssignResourceApproverCommandHandler,
        // UpdateResourceCommandHandler) - a resource effectively deleted from the caller's point of view
        // must not accept edits to its blackout periods either.
        ResourceGuard.EnsureNotArchived(resource);

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

        // Plain full replacement, then re-check the NEW window against active bookings (not a
        // diff-based recheck of only the newly-exposed range - simpler, and correct either way, since
        // the check is read-only and non-blocking; see CreateBlackoutPeriodCommandHandler for why this
        // never blocks the edit, only reports what it now conflicts with).
        period.StartUtc = request.StartUtc;
        period.EndUtc = request.EndUtc;
        period.Reason = request.Reason;

        await blackoutPeriodRepository.SaveChangesAsync(cancellationToken);

        var conflictingBookings = await bookingAvailabilityRepository.GetActiveBookingsAsync(
            period.ResourceId, period.StartUtc, period.EndUtc, cancellationToken);

        return new UpdateBlackoutPeriodResponse(
            period.Id, period.ResourceId, period.StartUtc, period.EndUtc, period.Reason,
            conflictingBookings.Select(booking => booking.Id).ToList());
    }
}
