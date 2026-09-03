using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using FluentValidation;

namespace BookSpace.Application.ResourceTypes;

public sealed record UpdateResourceTypeCommandRequest(Guid Id, string Name) : IRequest<UpdateResourceTypeResponse>;

public sealed record UpdateResourceTypeResponse(Guid Id, string Name);

public sealed class UpdateResourceTypeCommandRequestValidator : AbstractValidator<UpdateResourceTypeCommandRequest>
{
    public UpdateResourceTypeCommandRequestValidator()
    {
        RuleFor(command => command.Id).NotEmpty();
        RuleFor(command => command.Name).NotEmpty().MaximumLength(100);
    }
}

public sealed class UpdateResourceTypeCommandHandler(IResourceTypeRepository resourceTypeRepository)
    : IRequestHandler<UpdateResourceTypeCommandRequest, UpdateResourceTypeResponse>
{
    public async Task<UpdateResourceTypeResponse> Handle(UpdateResourceTypeCommandRequest request, CancellationToken cancellationToken)
    {
        var resourceType = await resourceTypeRepository.FindByIdAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"Resource type {request.Id} was not found.", ErrorCodes.ResourceTypeNotFound);

        if (await resourceTypeRepository.ExistsByNameAsync(request.Name, excludingResourceTypeId: request.Id, cancellationToken))
        {
            throw new ConflictException($"A resource type named '{request.Name}' already exists.", ErrorCodes.ResourceTypeNameConflict);
        }

        resourceType.Name = request.Name;
        await resourceTypeRepository.SaveChangesAsync(cancellationToken);

        return new UpdateResourceTypeResponse(resourceType.Id, resourceType.Name);
    }
}
