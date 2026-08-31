using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Domain.Enums;
using FluentValidation;

namespace BookSpace.Application.Resources;

public sealed record GetResourcesQuery(int Page = 1, int PageSize = 20, Guid? ResourceTypeId = null, ResourceStatus? Status = null)
    : IRequest<PagedResult<ResourceResponse>>;

public sealed class GetResourcesQueryValidator : AbstractValidator<GetResourcesQuery>
{
    public GetResourcesQueryValidator()
    {
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, 100);
    }
}

public sealed class GetResourcesQueryHandler(IResourceRepository resourceRepository)
    : IRequestHandler<GetResourcesQuery, PagedResult<ResourceResponse>>
{
    public async Task<PagedResult<ResourceResponse>> Handle(GetResourcesQuery request, CancellationToken cancellationToken)
    {
        var paged = await resourceRepository.GetPagedAsync(
            request.Page, request.PageSize, request.ResourceTypeId, request.Status, cancellationToken);

        return new PagedResult<ResourceResponse>(
            paged.Items.Select(resource => resource.ToResponse()).ToList(),
            paged.Page,
            paged.PageSize,
            paged.TotalCount);
    }
}
