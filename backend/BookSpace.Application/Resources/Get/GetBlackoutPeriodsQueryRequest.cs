using BookSpace.Application.Common;
using BookSpace.Application.Mediator;

namespace BookSpace.Application.Resources;

public sealed record GetBlackoutPeriodsQueryRequest(Guid ResourceId) : IRequest<IReadOnlyList<GetBlackoutPeriodsResponseItem>>;

public sealed record GetBlackoutPeriodsResponseItem(Guid Id, Guid ResourceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Reason);

public sealed class GetBlackoutPeriodsQueryHandler(
    IBlackoutPeriodRepository blackoutPeriodRepository,
    IResourceRepository resourceRepository) : IRequestHandler<GetBlackoutPeriodsQueryRequest, IReadOnlyList<GetBlackoutPeriodsResponseItem>>
{
    public async Task<IReadOnlyList<GetBlackoutPeriodsResponseItem>> Handle(GetBlackoutPeriodsQueryRequest request, CancellationToken cancellationToken)
    {
        if (await resourceRepository.FindByIdAsync(request.ResourceId, cancellationToken) is null)
        {
            throw new NotFoundException($"Resource {request.ResourceId} was not found.", ErrorCodes.ResourceNotFound);
        }

        var periods = await blackoutPeriodRepository.GetByResourceIdAsync(request.ResourceId, cancellationToken);
        return periods.Select(period => new GetBlackoutPeriodsResponseItem(period.Id, period.ResourceId, period.StartUtc, period.EndUtc, period.Reason)).ToList();
    }
}
