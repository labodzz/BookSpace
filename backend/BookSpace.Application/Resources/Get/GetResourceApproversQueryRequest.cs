using BookSpace.Application.Common;
using BookSpace.Application.Mediator;

namespace BookSpace.Application.Resources;

public sealed record GetResourceApproversQueryRequest(Guid ResourceId) : IRequest<IReadOnlyList<GetResourceApproversResponseItem>>;

public sealed record GetResourceApproversResponseItem(Guid Id, Guid ResourceId, Guid UserId);

public sealed class GetResourceApproversQueryHandler(
    IResourceApproverRepository resourceApproverRepository,
    IResourceRepository resourceRepository) : IRequestHandler<GetResourceApproversQueryRequest, IReadOnlyList<GetResourceApproversResponseItem>>
{
    public async Task<IReadOnlyList<GetResourceApproversResponseItem>> Handle(GetResourceApproversQueryRequest request, CancellationToken cancellationToken)
    {
        if (await resourceRepository.FindByIdAsync(request.ResourceId, cancellationToken) is null)
        {
            throw new NotFoundException($"Resource {request.ResourceId} was not found.", ErrorCodes.ResourceNotFound);
        }

        var approvers = await resourceApproverRepository.GetByResourceIdAsync(request.ResourceId, cancellationToken);
        return approvers.Select(approver => new GetResourceApproversResponseItem(approver.Id, approver.ResourceId, approver.UserId)).ToList();
    }
}
