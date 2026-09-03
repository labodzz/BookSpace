using BookSpace.Application.Common;
using BookSpace.Application.Mediator;

namespace BookSpace.Application.ResourceTypes;

public sealed record GetResourceTypeQueryRequest(Guid Id) : IRequest<GetResourceTypeResponse>;

public sealed record GetResourceTypeResponse(Guid Id, string Name);

public sealed class GetResourceTypeQueryHandler(IResourceTypeRepository resourceTypeRepository)
    : IRequestHandler<GetResourceTypeQueryRequest, GetResourceTypeResponse>
{
    public async Task<GetResourceTypeResponse> Handle(GetResourceTypeQueryRequest request, CancellationToken cancellationToken)
    {
        var resourceType = await resourceTypeRepository.FindByIdAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"Resource type {request.Id} was not found.", ErrorCodes.ResourceTypeNotFound);

        return new GetResourceTypeResponse(resourceType.Id, resourceType.Name);
    }
}
