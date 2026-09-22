using BookSpace.Application.Bookings;
using BookSpace.Application.Common;
using BookSpace.Application.Mediator;

namespace BookSpace.Application.Resources;

public sealed record RemoveResourceApproverCommandRequest(Guid ResourceId, Guid UserId) : IRequest<Unit>;

// Wrapped in the same IResourceBookingLock boundary UpdateResourceCommandHandler's own capacity check
// uses, keyed by the same Resources.Id - the "count remaining approvers, reject if this removal would
// leave none" check below is a cross-row invariant over the WHOLE ResourceApprover set for this
// resource, not something a single row's constraint can express. Without the lock, two admins removing
// two DIFFERENT approvers from the same two-approver, RequiresApproval=true resource at the same moment
// could both read "2 remaining" before either commits, both pass the <=1 check, and both succeed -
// leaving zero approvers despite the guard existing. The lock serializes them: the second removal's
// count is always read fresh, after the first either committed or rolled back.
public sealed class RemoveResourceApproverCommandHandler(
    IResourceApproverRepository resourceApproverRepository, IResourceRepository resourceRepository, IResourceBookingLock resourceBookingLock)
    : IRequestHandler<RemoveResourceApproverCommandRequest, Unit>
{
    public Task<Unit> Handle(RemoveResourceApproverCommandRequest request, CancellationToken cancellationToken) =>
        resourceBookingLock.RunExclusiveAsync(request.ResourceId, ct => RemoveUnderLockAsync(request, ct), cancellationToken);

    private async Task<Unit> RemoveUnderLockAsync(RemoveResourceApproverCommandRequest request, CancellationToken cancellationToken)
    {
        var approver = await resourceApproverRepository.FindByResourceAndUserAsync(request.ResourceId, request.UserId, cancellationToken)
            ?? throw new NotFoundException(
                $"User {request.UserId} is not an approver for resource {request.ResourceId}.",
                ErrorCodes.ResourceApproverNotFound);

        // A resource that still requires approval must always have somewhere for that approval to go.
        // Without this guard, removing the last approver would leave the resource RequiresApproval=true
        // with zero approvers - every future booking against it would then 409 with
        // Booking.NoApproverConfigured (see CreateBookingCommandHandler), silently making the resource
        // unbookable with no signal at the moment the removal actually happened.
        //
        // Fresh read, taken only after the lock is held - never a value computed before lock
        // acquisition, so this is guaranteed current with respect to any concurrent
        // Assign/RemoveResourceApproverCommandHandler call for the same resource.
        var resource = await resourceRepository.FindByIdAsync(request.ResourceId, cancellationToken);
        if (resource is { RequiresApproval: true })
        {
            var remainingApprovers = await resourceApproverRepository.GetByResourceIdAsync(request.ResourceId, cancellationToken);
            if (remainingApprovers.Count <= 1)
            {
                throw new ConflictException(
                    $"Resource {request.ResourceId} requires approval and cannot be left with no approver assigned.",
                    ErrorCodes.ResourceApproverLastRemaining);
            }
        }

        await resourceApproverRepository.RemoveAsync(approver, cancellationToken);
        await resourceApproverRepository.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }
}
