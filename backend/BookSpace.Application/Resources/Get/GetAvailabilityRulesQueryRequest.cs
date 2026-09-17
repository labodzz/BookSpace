using BookSpace.Application.Common;
using BookSpace.Application.Mediator;

namespace BookSpace.Application.Resources;

public sealed record GetAvailabilityRulesQueryRequest(Guid ResourceId) : IRequest<IReadOnlyList<GetAvailabilityRulesResponseItem>>;

public sealed record GetAvailabilityRulesResponseItem(Guid Id, Guid ResourceId, DayOfWeek DayOfWeek, TimeOnly StartTime, TimeOnly EndTime);

public sealed class GetAvailabilityRulesQueryHandler(
    IAvailabilityRuleRepository availabilityRuleRepository,
    IResourceRepository resourceRepository) : IRequestHandler<GetAvailabilityRulesQueryRequest, IReadOnlyList<GetAvailabilityRulesResponseItem>>
{
    public async Task<IReadOnlyList<GetAvailabilityRulesResponseItem>> Handle(GetAvailabilityRulesQueryRequest request, CancellationToken cancellationToken)
    {
        if (await resourceRepository.FindByIdAsync(request.ResourceId, cancellationToken) is null)
        {
            throw new NotFoundException($"Resource {request.ResourceId} was not found.", ErrorCodes.ResourceNotFound);
        }

        var rules = await availabilityRuleRepository.GetByResourceIdAsync(request.ResourceId, cancellationToken);
        return rules.Select(rule => new GetAvailabilityRulesResponseItem(rule.Id, rule.ResourceId, rule.DayOfWeek, rule.StartTime, rule.EndTime)).ToList();
    }
}
