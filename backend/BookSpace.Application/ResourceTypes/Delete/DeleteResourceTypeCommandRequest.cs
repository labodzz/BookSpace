using BookSpace.Application.Common;
using BookSpace.Application.Mediator;

namespace BookSpace.Application.ResourceTypes;

public sealed record DeleteResourceTypeCommandRequest(Guid Id) : IRequest<Unit>;

// Hard delete, not a soft-delete like Resource - a ResourceType is a pure taxonomy row with no
// history of its own to preserve. It's only blocked, not archived, when a Resource still uses it.
public sealed class DeleteResourceTypeCommandHandler(IResourceTypeRepository resourceTypeRepository)
    : IRequestHandler<DeleteResourceTypeCommandRequest, Unit>
{
    public async Task<Unit> Handle(DeleteResourceTypeCommandRequest request, CancellationToken cancellationToken)
    {
        var resourceType = await resourceTypeRepository.FindByIdAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"Resource type {request.Id} was not found.", ErrorCodes.ResourceTypeNotFound);

        if (await resourceTypeRepository.IsReferencedByAnyResourceAsync(request.Id, cancellationToken))
        {
            throw new ConflictException(
                $"Resource type {request.Id} is still in use by at least one resource and cannot be deleted.",
                ErrorCodes.ResourceTypeInUse);
        }

        await resourceTypeRepository.RemoveAsync(resourceType, cancellationToken);
        await resourceTypeRepository.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }
}
