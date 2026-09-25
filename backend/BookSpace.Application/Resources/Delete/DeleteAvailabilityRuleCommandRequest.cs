using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Resources;

public sealed record DeleteAvailabilityRuleCommandRequest(Guid ResourceId, Guid RuleId) : IRequest<Unit>;

public sealed class DeleteAvailabilityRuleCommandHandler(
    IAvailabilityRuleRepository availabilityRuleRepository, IResourceRepository resourceRepository)
    : IRequestHandler<DeleteAvailabilityRuleCommandRequest, Unit>
{
    public async Task<Unit> Handle(DeleteAvailabilityRuleCommandRequest request, CancellationToken cancellationToken)
    {
        var rule = await availabilityRuleRepository.FindByIdAsync(request.RuleId, cancellationToken);

        // A rule that exists but belongs to a different resource is exactly as "not found" from the
        // caller's point of view as one that doesn't exist at all - never let a mismatched URL pair
        // succeed just because the rule id happens to be valid for some other resource.
        if (rule is null || rule.ResourceId != request.ResourceId)
        {
            throw new NotFoundException(
                $"Availability rule {request.RuleId} was not found for resource {request.ResourceId}.", ErrorCodes.AvailabilityRuleNotFound);
        }

        // An Active resource must always keep at least one availability rule - otherwise it would report
        // Status == Active while having no open hours at all, exactly the confusing state
        // UpdateResourceCommandHandler's own activation check exists to prevent from the other direction.
        // Deactivate the resource first, then its rules can be removed freely (Inactive/Maintenance/
        // Archived resources have no such restriction).
        var resource = await resourceRepository.FindByIdAsync(request.ResourceId, cancellationToken);
        if (resource?.Status == ResourceStatus.Active)
        {
            var existingRules = await availabilityRuleRepository.GetByResourceIdAsync(request.ResourceId, cancellationToken);
            if (existingRules.Count <= 1)
            {
                throw new ConflictException(
                    "Deactivate this resource before removing its last availability rule.", ErrorCodes.ResourceAvailabilityRuleRequired);
            }
        }

        await availabilityRuleRepository.RemoveAsync(rule, cancellationToken);
        await availabilityRuleRepository.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }
}
