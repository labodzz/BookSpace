using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using FluentValidation;

namespace BookSpace.Application.Resources;

public sealed record CreateResourceCommandRequest(
    Guid ResourceTypeId,
    string Name,
    string? Description,
    int Capacity,
    bool RequiresApproval,
    string TimeZoneId) : IRequest<CreateResourceResponse>;

public sealed record CreateResourceResponse(
    Guid Id,
    Guid ResourceTypeId,
    string Name,
    string? Description,
    int Capacity,
    bool RequiresApproval,
    ResourceStatus Status,
    string TimeZoneId);

public sealed class CreateResourceCommandRequestValidator : AbstractValidator<CreateResourceCommandRequest>
{
    public CreateResourceCommandRequestValidator()
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
    : IRequestHandler<CreateResourceCommandRequest, CreateResourceResponse>
{
    public async Task<CreateResourceResponse> Handle(CreateResourceCommandRequest request, CancellationToken cancellationToken)
    {
        if (!await resourceRepository.ResourceTypeExistsAsync(request.ResourceTypeId, cancellationToken))
        {
            throw new NotFoundException($"Resource type {request.ResourceTypeId} was not found.", ErrorCodes.ResourceTypeNotFound);
        }

        if (await resourceRepository.ExistsByNameAsync(request.Name, excludingResourceId: null, cancellationToken))
        {
            throw new ConflictException($"A resource named '{request.Name}' already exists.", ErrorCodes.ResourceNameConflict);
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
            // Starts Inactive, never Active: a brand-new resource has no AvailabilityRule yet, so letting
            // it look bookable immediately would let a Member reach the booking form only to be rejected
            // at submit time for a reason the UI never told them about. UpdateResourceCommandHandler
            // rejects flipping this to Active until at least one rule exists - see
            // docs/resource-lifecycle-and-capacity.md.
            Status = ResourceStatus.Inactive,
            TimeZoneId = request.TimeZoneId,
        };

        await resourceRepository.AddAsync(resource, cancellationToken);
        await resourceRepository.SaveChangesAsync(cancellationToken);

        return new CreateResourceResponse(
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
