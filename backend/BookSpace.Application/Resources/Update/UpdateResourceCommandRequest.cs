using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Domain.Enums;
using FluentValidation;

namespace BookSpace.Application.Resources;

public sealed record UpdateResourceCommandRequest(
    Guid Id,
    Guid ResourceTypeId,
    string Name,
    string? Description,
    int Capacity,
    bool RequiresApproval,
    string TimeZoneId,
    ResourceStatus Status) : IRequest<UpdateResourceResponse>;

public sealed record UpdateResourceResponse(
    Guid Id,
    Guid ResourceTypeId,
    string Name,
    string? Description,
    int Capacity,
    bool RequiresApproval,
    ResourceStatus Status,
    string TimeZoneId);

public sealed class UpdateResourceCommandRequestValidator : AbstractValidator<UpdateResourceCommandRequest>
{
    public UpdateResourceCommandRequestValidator()
    {
        RuleFor(command => command.Id).NotEmpty();
        RuleFor(command => command.ResourceTypeId).NotEmpty();
        RuleFor(command => command.Name).NotEmpty().MaximumLength(200);
        RuleFor(command => command.Description).MaximumLength(2000);
        RuleFor(command => command.Capacity).GreaterThan(0);
        RuleFor(command => command.TimeZoneId).NotEmpty()
            .Must(TimeZoneValidation.BeAValidTimeZoneId)
            .WithMessage("'{PropertyName}' is not a recognized time zone identifier.");
        RuleFor(command => command.Status).IsInEnum()
            .NotEqual(ResourceStatus.Archived)
            .WithMessage("Use DELETE /resources/{id} to archive a resource.");
    }
}

public sealed class UpdateResourceCommandHandler(
    IResourceRepository resourceRepository, IBookingAvailabilityRepository bookingAvailabilityRepository)
    : IRequestHandler<UpdateResourceCommandRequest, UpdateResourceResponse>
{
    public async Task<UpdateResourceResponse> Handle(UpdateResourceCommandRequest request, CancellationToken cancellationToken)
    {
        var resource = await resourceRepository.FindByIdAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"Resource {request.Id} was not found.");

        // Archived is terminal: DeleteResourceCommand is the only way in, and there is deliberately no
        // way back out via a general-purpose field edit - a full field rewrite bundled with a status
        // flip would let Update silently "reactivate" a resource nobody asked to reactivate.
        if (resource.Status == ResourceStatus.Archived)
        {
            throw new ConflictException($"Resource {request.Id} is archived and cannot be updated.");
        }

        if (!await resourceRepository.ResourceTypeExistsAsync(request.ResourceTypeId, cancellationToken))
        {
            throw new NotFoundException($"Resource type {request.ResourceTypeId} was not found.");
        }

        if (await resourceRepository.ExistsByNameAsync(request.Name, excludingResourceId: request.Id, cancellationToken))
        {
            throw new ConflictException($"A resource named '{request.Name}' already exists.");
        }

        if (request.Capacity < resource.Capacity)
        {
            await EnsureCapacityCoversExistingBookingsAsync(resource.Id, request.Capacity, cancellationToken);
        }

        resource.ResourceTypeId = request.ResourceTypeId;
        resource.Name = request.Name;
        resource.Description = request.Description;
        resource.Capacity = request.Capacity;
        resource.RequiresApproval = request.RequiresApproval;
        resource.TimeZoneId = request.TimeZoneId;
        resource.Status = request.Status;

        await resourceRepository.SaveChangesAsync(cancellationToken);

        return new UpdateResourceResponse(
            resource.Id,
            resource.ResourceTypeId,
            resource.Name,
            resource.Description,
            resource.Capacity,
            resource.RequiresApproval,
            resource.Status,
            resource.TimeZoneId);
    }

    // Booking-create doesn't exist yet in this codebase, so this only guards against the one write
    // path that already exists and could otherwise orphan a commitment: shrinking Capacity below what
    // active (Pending/Confirmed) bookings already require at some point in time. A sweep-line over
    // each booking's [Start, +Quantity) / [End, -Quantity) events finds the highest concurrent demand;
    // IntervalMath.ComputeAvailableCapacity isn't reused here since it answers a different question
    // (which sub-intervals remain bookable), not "what's the single worst-case peak".
    private async Task EnsureCapacityCoversExistingBookingsAsync(Guid resourceId, int newCapacity, CancellationToken cancellationToken)
    {
        var activeBookings = await bookingAvailabilityRepository.GetActiveBookingsAsync(
            resourceId, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue, cancellationToken);

        if (activeBookings.Count == 0)
        {
            return;
        }

        // IsStart breaks ties at an identical timestamp: an end event must be applied before a start
        // event at the same instant, or two back-to-back (non-overlapping) bookings - one ending
        // exactly when the next begins, entirely legal at full capacity - would be miscounted as
        // briefly overlapping and inflate the peak. This matches the half-open [Start, End) convention
        // used everywhere else in this feature (e.g. the availability query's own StartUtc</EndUtc>
        // overlap checks).
        var peakDemand = activeBookings
            .SelectMany(booking => new[]
            {
                (Time: booking.StartUtc, IsStart: true, Delta: booking.Quantity),
                (Time: booking.EndUtc, IsStart: false, Delta: -booking.Quantity),
            })
            .OrderBy(change => change.Time)
            .ThenBy(change => change.IsStart)
            .Aggregate((Running: 0, Peak: 0), (state, change) =>
            {
                var running = state.Running + change.Delta;
                return (running, Math.Max(state.Peak, running));
            })
            .Peak;

        if (peakDemand > newCapacity)
        {
            throw new ConflictException(
                $"Cannot reduce capacity to {newCapacity}: existing bookings require at least {peakDemand} at their peak overlap.");
        }
    }
}
