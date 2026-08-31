using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Domain.Enums;
using FluentValidation;

namespace BookSpace.Application.Resources;

public sealed record UpdateResourceCommand(
    Guid Id,
    Guid ResourceTypeId,
    string Name,
    string? Description,
    int Capacity,
    bool RequiresApproval,
    string TimeZoneId,
    ResourceStatus Status) : IRequest<ResourceResponse>;

public sealed class UpdateResourceCommandValidator : AbstractValidator<UpdateResourceCommand>
{
    public UpdateResourceCommandValidator()
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
    : IRequestHandler<UpdateResourceCommand, ResourceResponse>
{
    public async Task<ResourceResponse> Handle(UpdateResourceCommand request, CancellationToken cancellationToken)
    {
        var resource = await resourceRepository.FindByIdAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"Resource {request.Id} was not found.");

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

        return resource.ToResponse();
    }
}
