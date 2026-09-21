using BookSpace.Application.Common;
using BookSpace.Application.Mediator;

namespace BookSpace.Application.Resources;

public sealed record RemoveResourceApproverCommandRequest(Guid ResourceId, Guid UserId) : IRequest<Unit>;

public sealed class RemoveResourceApproverCommandHandler(
    IResourceApproverRepository resourceApproverRepository, IResourceRepository resourceRepository)
    : IRequestHandler<RemoveResourceApproverCommandRequest, Unit>
{
    public async Task<Unit> Handle(RemoveResourceApproverCommandRequest request, CancellationToken cancellationToken)
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
