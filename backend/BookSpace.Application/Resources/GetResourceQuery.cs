using BookSpace.Application.Common;
using BookSpace.Application.Mediator;

namespace BookSpace.Application.Resources;

public sealed record GetResourceQuery(Guid Id) : IRequest<ResourceResponse>;

public sealed class GetResourceQueryHandler(IResourceRepository resourceRepository)
    : IRequestHandler<GetResourceQuery, ResourceResponse>
{
    public async Task<ResourceResponse> Handle(GetResourceQuery request, CancellationToken cancellationToken)
    {
        var resource = await resourceRepository.FindByIdAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"Resource {request.Id} was not found.");

        return resource.ToResponse();
    }
}
