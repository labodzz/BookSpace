using BookSpace.Application.Common;
using BookSpace.Application.Mediator;

namespace BookSpace.Application.Resources;

public sealed record DeleteAvailabilityRuleCommandRequest(Guid ResourceId, Guid RuleId) : IRequest<Unit>;

public sealed class DeleteAvailabilityRuleCommandHandler(IAvailabilityRuleRepository availabilityRuleRepository)
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
            throw new NotFoundException($"Availability rule {request.RuleId} was not found for resource {request.ResourceId}.");
        }

        await availabilityRuleRepository.RemoveAsync(rule, cancellationToken);
        await availabilityRuleRepository.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }
}
