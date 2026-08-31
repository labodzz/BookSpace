using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using FluentValidation;

namespace BookSpace.Application.Resources;

public sealed record CreateResourceCommand(
    Guid ResourceTypeId,
    string Name,
    string? Description,
    int Capacity,
    bool RequiresApproval,
    string TimeZoneId) : IRequest<ResourceResponse>;

public sealed class CreateResourceCommandValidator : AbstractValidator<CreateResourceCommand>
{
    public CreateResourceCommandValidator()
    {
        RuleFor(command => command.ResourceTypeId).NotEmpty();
        RuleFor(command => command.Name).NotEmpty().MaximumLength(200);
        RuleFor(command => command.Description).MaximumLength(2000);
        RuleFor(command => command.Capacity).GreaterThan(0);
        RuleFor(command => command.TimeZoneId).NotEmpty()
            .Must(TimeZoneValidation.BeAValidTimeZoneId)
            .WithMessage("'{PropertyName}' is not a recognized time zone identifier.");
    }
}

public sealed class CreateResourceCommandHandler(IResourceRepository resourceRepository, ICurrentUserContext currentUserContext)
    : IRequestHandler<CreateResourceCommand, ResourceResponse>
{
    public async Task<ResourceResponse> Handle(CreateResourceCommand request, CancellationToken cancellationToken)
    {
        if (!await resourceRepository.ResourceTypeExistsAsync(request.ResourceTypeId, cancellationToken))
        {
            throw new NotFoundException($"Resource type {request.ResourceTypeId} was not found.");
        }

        if (await resourceRepository.ExistsByNameAsync(request.Name, excludingResourceId: null, cancellationToken))
        {
            throw new ConflictException($"A resource named '{request.Name}' already exists.");
        }

        var resource = new Resource
        {
            Id = Guid.NewGuid(),
            TenantId = currentUserContext.TenantId!.Value,
            ResourceTypeId = request.ResourceTypeId,
            Name = request.Name,
            Description = request.Description,
            Capacity = request.Capacity,
            RequiresApproval = request.RequiresApproval,
            Status = ResourceStatus.Active,
            TimeZoneId = request.TimeZoneId,
        };

        await resourceRepository.AddAsync(resource, cancellationToken);
        await resourceRepository.SaveChangesAsync(cancellationToken);

        return resource.ToResponse();
    }
}
