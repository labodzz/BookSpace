using BookSpace.Application.Common;
using BookSpace.Application.Mediator;

namespace BookSpace.Application.Resources;

public sealed record GetAvailabilityRulesQuery(Guid ResourceId) : IRequest<IReadOnlyList<AvailabilityRuleResponse>>;

public sealed class GetAvailabilityRulesQueryHandler(
    IAvailabilityRuleRepository availabilityRuleRepository,
    IResourceRepository resourceRepository) : IRequestHandler<GetAvailabilityRulesQuery, IReadOnlyList<AvailabilityRuleResponse>>
{
    public async Task<IReadOnlyList<AvailabilityRuleResponse>> Handle(GetAvailabilityRulesQuery request, CancellationToken cancellationToken)
    {
        if (await resourceRepository.FindByIdAsync(request.ResourceId, cancellationToken) is null)
        {
            throw new NotFoundException($"Resource {request.ResourceId} was not found.");
        }

        var rules = await availabilityRuleRepository.GetByResourceIdAsync(request.ResourceId, cancellationToken);
        return rules.Select(rule => rule.ToResponse()).ToList();
    }
}
