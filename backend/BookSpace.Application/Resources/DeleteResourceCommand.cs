using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Resources;

public sealed record DeleteResourceCommand(Guid Id) : IRequest<ResourceResponse>;

// A soft-delete (Status -> Archived), never a real row delete: Booking.ResourceId has a Restrict FK,
// so a resource with any booking history can't be physically deleted without breaking that
// constraint. Archiving is also idempotent - deleting an already-archived resource just returns it.
public sealed class DeleteResourceCommandHandler(IResourceRepository resourceRepository)
    : IRequestHandler<DeleteResourceCommand, ResourceResponse>
{
    public async Task<ResourceResponse> Handle(DeleteResourceCommand request, CancellationToken cancellationToken)
    {
        var resource = await resourceRepository.FindByIdAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"Resource {request.Id} was not found.");

        if (resource.Status != ResourceStatus.Archived)
        {
            resource.Status = ResourceStatus.Archived;
            await resourceRepository.SaveChangesAsync(cancellationToken);
        }

        return resource.ToResponse();
    }
}
