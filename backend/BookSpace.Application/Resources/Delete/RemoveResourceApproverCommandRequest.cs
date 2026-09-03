using BookSpace.Application.Common;
using BookSpace.Application.Mediator;

namespace BookSpace.Application.Resources;

public sealed record RemoveResourceApproverCommandRequest(Guid ResourceId, Guid UserId) : IRequest<Unit>;

public sealed class RemoveResourceApproverCommandHandler(IResourceApproverRepository resourceApproverRepository)
    : IRequestHandler<RemoveResourceApproverCommandRequest, Unit>
{
    public async Task<Unit> Handle(RemoveResourceApproverCommandRequest request, CancellationToken cancellationToken)
    {
        var approver = await resourceApproverRepository.FindByResourceAndUserAsync(request.ResourceId, request.UserId, cancellationToken)
            ?? throw new NotFoundException(
                $"User {request.UserId} is not an approver for resource {request.ResourceId}.",
                ErrorCodes.ResourceApproverNotFound);

        await resourceApproverRepository.RemoveAsync(approver, cancellationToken);
        await resourceApproverRepository.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }
}
