using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Domain.Enums;
using FluentValidation;

namespace BookSpace.Application.Resources;

public sealed record UpdateResourceCommandRequest(
    Guid Id,
    Guid ResourceTypeId,
    string Name,
    string? Description,
    int Capacity,
    bool RequiresApproval,
    string TimeZoneId,
    ResourceStatus Status) : IRequest<UpdateResourceResponse>;

public sealed record UpdateResourceResponse(
    Guid Id,
    Guid ResourceTypeId,
    string Name,
    string? Description,
    int Capacity,
    bool RequiresApproval,
    ResourceStatus Status,
    string TimeZoneId);

public sealed class UpdateResourceCommandRequestValidator : AbstractValidator<UpdateResourceCommandRequest>
{
    public UpdateResourceCommandRequestValidator()
    {
        RuleFor(command => command.Id).NotEmpty();
        RuleFor(command => command.ResourceTypeId).NotEmpty();
        RuleFor(command => command.Name).NotEmpty().MaximumLength(200);
        RuleFor(command => command.Description).MaximumLength(2000);
        RuleFor(command => command.Capacity).GreaterThan(0);
        RuleFor(command => command.TimeZoneId).NotEmpty()
            .Must(TimeZoneValidation.BeAValidTimeZoneId)
            .WithMessage("'{PropertyName}' is not a recognized time zone identifier.");
        RuleFor(command => command.Status).IsInEnum()
            .NotEqual(ResourceStatus.Archived)
            .WithMessage("Use DELETE /resources/{id} to archive a resource.");
    }
}

public sealed class UpdateResourceCommandHandler(IResourceRepository resourceRepository)
    : IRequestHandler<UpdateResourceCommandRequest, UpdateResourceResponse>
{
    public async Task<UpdateResourceResponse> Handle(UpdateResourceCommandRequest request, CancellationToken cancellationToken)
    {
        var resource = await resourceRepository.FindByIdAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"Resource {request.Id} was not found.");

        // Archived is terminal: DeleteResourceCommand is the only way in, and there is deliberately no
        // way back out via a general-purpose field edit - a full field rewrite bundled with a status
        // flip would let Update silently "reactivate" a resource nobody asked to reactivate.
        if (resource.Status == ResourceStatus.Archived)
        {
            throw new ConflictException($"Resource {request.Id} is archived and cannot be updated.");
        }

        if (!await resourceRepository.ResourceTypeExistsAsync(request.ResourceTypeId, cancellationToken))
        {
            throw new NotFoundException($"Resource type {request.ResourceTypeId} was not found.");
        }

        if (await resourceRepository.ExistsByNameAsync(request.Name, excludingResourceId: request.Id, cancellationToken))
        {
            throw new ConflictException($"A resource named '{request.Name}' already exists.");
        }

        resource.ResourceTypeId = request.ResourceTypeId;
        resource.Name = request.Name;
        resource.Description = request.Description;
        resource.Capacity = request.Capacity;
        resource.RequiresApproval = request.RequiresApproval;
        resource.TimeZoneId = request.TimeZoneId;
        resource.Status = request.Status;

        await resourceRepository.SaveChangesAsync(cancellationToken);

        return new UpdateResourceResponse(
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
