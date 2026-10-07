using BookSpace.Application.Auth;
using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Tenants;

public sealed record GetTenantQueryRequest(Guid Id) : IRequest<TenantDetailResponse>;

// Platform data and aggregates a SysAdmin needs to administer this one tenant - never full tenant
// business content (no bookings, no resources, no ordinary member list - see docs/tenant-onboarding.md,
// "Limited user view for SysAdmin"). DefaultTimeZoneId is the one field beyond TenantSummaryResponse's
// own shape, since the (currently name-only) tenant edit form still needs to show it even though this
// task doesn't make it editable.
public sealed record TenantDetailResponse(
    Guid Id,
    string Name,
    string DefaultTimeZoneId,
    TenantStatus Status,
    DateTimeOffset CreatedAtUtc,
    int TotalUserCount,
    int ActiveUserCount,
    int InvitedUserCount,
    int InactiveUserCount,
    IReadOnlyList<string> ActiveTenantAdminEmails,
    bool HasActiveTenantAdmin,
    // Only surfaced on the detail response, not the list (TenantSummaryResponse) - exactly enough for
    // the platform-recovery resend/revoke actions, never exposed just to pad out the list screen.
    IReadOnlyList<PendingTenantAdminContact> PendingTenantAdminInvitations);

public sealed class GetTenantQueryHandler(ITenantRepository tenantRepository, IUserRepository userRepository)
    : IRequestHandler<GetTenantQueryRequest, TenantDetailResponse>
{
    public async Task<TenantDetailResponse> Handle(GetTenantQueryRequest request, CancellationToken cancellationToken)
    {
        var tenant = await tenantRepository.FindByIdAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"Tenant {request.Id} was not found.", ErrorCodes.TenantNotFound);

        var aggregatesByTenantId = await userRepository.GetTenantUserAggregatesAsync([tenant.Id], cancellationToken);
        var aggregate = aggregatesByTenantId.GetValueOrDefault(tenant.Id, TenantUserAggregate.Empty);

        return new TenantDetailResponse(
            tenant.Id,
            tenant.Name,
            tenant.DefaultTimeZoneId,
            tenant.Status,
            tenant.CreatedAtUtc,
            aggregate.TotalUserCount,
            aggregate.ActiveUserCount,
            aggregate.InvitedUserCount,
            aggregate.InactiveUserCount,
            aggregate.ActiveTenantAdminEmails,
            aggregate.ActiveTenantAdminEmails.Count > 0,
            aggregate.PendingTenantAdminInvitations);
    }
}
