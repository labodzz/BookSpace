using BookSpace.Application.Mediator;

namespace BookSpace.Application.ResourceTypes;

public sealed record GetResourceTypesQueryRequest : IRequest<IReadOnlyList<GetResourceTypesResponseItem>>;

public sealed record GetResourceTypesResponseItem(Guid Id, string Name);

public sealed class GetResourceTypesQueryHandler(IResourceTypeRepository resourceTypeRepository)
    : IRequestHandler<GetResourceTypesQueryRequest, IReadOnlyList<GetResourceTypesResponseItem>>
{
    public async Task<IReadOnlyList<GetResourceTypesResponseItem>> Handle(GetResourceTypesQueryRequest request, CancellationToken cancellationToken)
    {
        var resourceTypes = await resourceTypeRepository.GetAllAsync(cancellationToken);
        return resourceTypes
            .OrderBy(resourceType => resourceType.Name)
            .Select(resourceType => new GetResourceTypesResponseItem(resourceType.Id, resourceType.Name))
            .ToList();
    }
}
