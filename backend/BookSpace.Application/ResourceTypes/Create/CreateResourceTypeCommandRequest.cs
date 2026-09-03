using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using FluentValidation;

namespace BookSpace.Application.ResourceTypes;

public sealed record CreateResourceTypeCommandRequest(string Name) : IRequest<CreateResourceTypeResponse>;

public sealed record CreateResourceTypeResponse(Guid Id, string Name);

public sealed class CreateResourceTypeCommandRequestValidator : AbstractValidator<CreateResourceTypeCommandRequest>
{
    public CreateResourceTypeCommandRequestValidator()
    {
        RuleFor(command => command.Name).NotEmpty().MaximumLength(100);
    }
}

public sealed class CreateResourceTypeCommandHandler(
    IResourceTypeRepository resourceTypeRepository, ICurrentUserContext currentUserContext)
    : IRequestHandler<CreateResourceTypeCommandRequest, CreateResourceTypeResponse>
{
    public async Task<CreateResourceTypeResponse> Handle(CreateResourceTypeCommandRequest request, CancellationToken cancellationToken)
    {
        if (await resourceTypeRepository.ExistsByNameAsync(request.Name, excludingResourceTypeId: null, cancellationToken))
        {
            throw new ConflictException($"A resource type named '{request.Name}' already exists.", ErrorCodes.ResourceTypeNameConflict);
        }

        var resourceType = new ResourceType
        {
            Id = Guid.NewGuid(),
            TenantId = currentUserContext.TenantId!.Value,
            Name = request.Name,
        };

        await resourceTypeRepository.AddAsync(resourceType, cancellationToken);
        await resourceTypeRepository.SaveChangesAsync(cancellationToken);

        return new CreateResourceTypeResponse(resourceType.Id, resourceType.Name);
    }
}
