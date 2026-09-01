using BookSpace.Application.Common;
using BookSpace.Application.Mediator;

namespace BookSpace.Application.Resources;

public sealed record GetBlackoutPeriodsQuery(Guid ResourceId) : IRequest<IReadOnlyList<BlackoutPeriodResponse>>;

public sealed class GetBlackoutPeriodsQueryHandler(
    IBlackoutPeriodRepository blackoutPeriodRepository,
    IResourceRepository resourceRepository) : IRequestHandler<GetBlackoutPeriodsQuery, IReadOnlyList<BlackoutPeriodResponse>>
{
    public async Task<IReadOnlyList<BlackoutPeriodResponse>> Handle(GetBlackoutPeriodsQuery request, CancellationToken cancellationToken)
    {
        if (await resourceRepository.FindByIdAsync(request.ResourceId, cancellationToken) is null)
        {
            throw new NotFoundException($"Resource {request.ResourceId} was not found.", ErrorCodes.ResourceNotFound);
        }

        var periods = await blackoutPeriodRepository.GetByResourceIdAsync(request.ResourceId, cancellationToken);
        return periods.Select(period => period.ToResponse()).ToList();
    }
}
