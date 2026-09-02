using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Domain.Enums;
using FluentValidation;

namespace BookSpace.Application.Resources;

public sealed record GetResourcesQueryRequest(int Page = 1, int PageSize = 20, Guid? ResourceTypeId = null, ResourceStatus? Status = null)
    : IRequest<PagedResult<GetResourcesResponseItem>>;

public sealed record GetResourcesResponseItem(
    Guid Id,
    Guid ResourceTypeId,
    string Name,
    string? Description,
    int Capacity,
    bool RequiresApproval,
    ResourceStatus Status,
    string TimeZoneId);

public sealed class GetResourcesQueryRequestValidator : AbstractValidator<GetResourcesQueryRequest>
{
    public GetResourcesQueryRequestValidator()
    {
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, 100);
    }
}

public sealed class GetResourcesQueryHandler(IResourceRepository resourceRepository)
    : IRequestHandler<GetResourcesQueryRequest, PagedResult<GetResourcesResponseItem>>
{
    public async Task<PagedResult<GetResourcesResponseItem>> Handle(GetResourcesQueryRequest request, CancellationToken cancellationToken)
    {
        var paged = await resourceRepository.GetPagedAsync(
            request.Page, request.PageSize, request.ResourceTypeId, request.Status, cancellationToken);

        return new PagedResult<GetResourcesResponseItem>(
            paged.Items.Select(resource => new GetResourcesResponseItem(
                resource.Id,
                resource.ResourceTypeId,
                resource.Name,
                resource.Description,
                resource.Capacity,
                resource.RequiresApproval,
                resource.Status,
                resource.TimeZoneId)).ToList(),
            paged.Page,
            paged.PageSize,
            paged.TotalCount);
    }
}
