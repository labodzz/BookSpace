using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Resources;

public sealed record GetResourceQueryRequest(Guid Id) : IRequest<GetResourceResponse>;

public sealed record GetResourceResponse(
    Guid Id,
    Guid ResourceTypeId,
    string Name,
    string? Description,
    int Capacity,
    bool RequiresApproval,
    ResourceStatus Status,
    string TimeZoneId);

public sealed class GetResourceQueryHandler(IResourceRepository resourceRepository)
    : IRequestHandler<GetResourceQueryRequest, GetResourceResponse>
{
    public async Task<GetResourceResponse> Handle(GetResourceQueryRequest request, CancellationToken cancellationToken)
    {
        var resource = await resourceRepository.FindByIdAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"Resource {request.Id} was not found.", ErrorCodes.ResourceNotFound);

        return new GetResourceResponse(
            resource.Id,
            resource.ResourceTypeId,
            resource.Name,
            resource.Description,
            resource.Capacity,
            resource.RequiresApproval,
            resource.Status,
            resource.TimeZoneId);
    }
}
