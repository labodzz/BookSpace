using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Resources;

public sealed record DeleteResourceCommandRequest(Guid Id) : IRequest<DeleteResourceResponse>;

public sealed record DeleteResourceResponse(
    Guid Id,
    Guid ResourceTypeId,
    string Name,
    string? Description,
    int Capacity,
    bool RequiresApproval,
    ResourceStatus Status,
    string TimeZoneId);

// A soft-delete (Status -> Archived), never a real row delete: Booking.ResourceId has a Restrict FK,
// so a resource with any booking history can't be physically deleted without breaking that
// constraint. Archiving is also idempotent - deleting an already-archived resource just returns it.
public sealed class DeleteResourceCommandHandler(IResourceRepository resourceRepository)
    : IRequestHandler<DeleteResourceCommandRequest, DeleteResourceResponse>
{
    public async Task<DeleteResourceResponse> Handle(DeleteResourceCommandRequest request, CancellationToken cancellationToken)
    {
        var resource = await resourceRepository.FindByIdAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"Resource {request.Id} was not found.", ErrorCodes.ResourceNotFound);

        if (resource.Status != ResourceStatus.Archived)
        {
            resource.Status = ResourceStatus.Archived;
            await resourceRepository.SaveChangesAsync(cancellationToken);
        }

        return new DeleteResourceResponse(
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
