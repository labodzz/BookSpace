using BookSpace.Application.Auth;
using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Domain.Enums;
using FluentValidation;

namespace BookSpace.Application.Tenants;

// The SysAdmin tenant list (see docs/tenant-onboarding.md, "Tenant lifecycle"). Paginated and server-side
// filtered like GetUsersQueryRequest, for the same reason: a platform's tenant roster grows over time and
// is never pruned.
public sealed record GetTenantsQueryRequest(int Page = 1, int PageSize = 20, string? Search = null, TenantStatus? Status = null)
    : IRequest<PagedResult<TenantSummaryResponse>>;

// Deliberately platform data and aggregates only - never a booking/resource count, never any user's name
// beyond an active TenantAdmin's own contact email (needed for platform recovery - see
// docs/tenant-onboarding.md). HasActiveTenantAdmin is surfaced as its own bool (not left for the client to
// derive from ActiveTenantAdminEmails.Length > 0) so a "needs attention" list filter/sort never has to
// know that detail of this shape.
public sealed record TenantSummaryResponse(
    Guid Id,
    string Name,
    TenantStatus Status,
    DateTimeOffset CreatedAtUtc,
    int TotalUserCount,
    int ActiveUserCount,
    int InvitedUserCount,
    int InactiveUserCount,
    IReadOnlyList<string> ActiveTenantAdminEmails,
    bool HasActiveTenantAdmin);

public sealed class GetTenantsQueryRequestValidator : AbstractValidator<GetTenantsQueryRequest>
{
    public GetTenantsQueryRequestValidator()
    {
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, 100);
        RuleFor(query => query.Search).MaximumLength(200);
    }
}

public sealed class GetTenantsQueryRequestHandler(ITenantRepository tenantRepository, IUserRepository userRepository)
    : IRequestHandler<GetTenantsQueryRequest, PagedResult<TenantSummaryResponse>>
{
    public async Task<PagedResult<TenantSummaryResponse>> Handle(GetTenantsQueryRequest request, CancellationToken cancellationToken)
    {
        var paged = await tenantRepository.GetPagedAsync(request.Page, request.PageSize, request.Search, request.Status, cancellationToken);

        // One batched aggregate query for the whole page, never one per tenant - see
        // IUserRepository.GetTenantUserAggregatesAsync's own doc comment.
        var aggregatesByTenantId = await userRepository.GetTenantUserAggregatesAsync(
            paged.Items.Select(tenant => tenant.Id).ToList(), cancellationToken);

        return new PagedResult<TenantSummaryResponse>(
            paged.Items.Select(tenant =>
            {
                var aggregate = aggregatesByTenantId.GetValueOrDefault(tenant.Id, TenantUserAggregate.Empty);
                return new TenantSummaryResponse(
                    tenant.Id,
                    tenant.Name,
                    tenant.Status,
                    tenant.CreatedAtUtc,
                    aggregate.TotalUserCount,
                    aggregate.ActiveUserCount,
                    aggregate.InvitedUserCount,
                    aggregate.InactiveUserCount,
                    aggregate.ActiveTenantAdminEmails,
                    aggregate.ActiveTenantAdminEmails.Count > 0);
            }).ToList(),
            paged.Page,
            paged.PageSize,
            paged.TotalCount);
    }
}
