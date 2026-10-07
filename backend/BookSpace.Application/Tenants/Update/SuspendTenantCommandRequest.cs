using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Tenants;

// SysAdmin-only (see TenantsController). Only Active -> Suspended is valid - NOT idempotent, matching
// ReactivateUserCommandHandler's own precedent for a strict lifecycle transition: suspending an already-
// Suspended or already-Deleted tenant is a clear conflict, not silently accepted.
//
// Deliberately does NOT revoke every tenant member's refresh token family here. AuthenticationService's
// IsUsableAsync already checks the tenant's live status on every Login and Refresh (see
// docs/tenant-onboarding.md, "Tenant lifecycle") - an already-issued access token can still be used for
// up to AuthOptions.AccessTokenMinutes (15) after suspension, the same bounded, accepted window this
// codebase already lives with for a role change (see AssignUserRoleCommandHandler's own doc comment).
// Proactively revoking every refresh token for the tenant would close that window immediately, at the
// cost of a bulk cross-tenant token-revocation repository method nothing else in this codebase needs -
// not justified by anything this task's requirements call out as a concrete incident this is defending
// against.
public sealed record SuspendTenantCommandRequest(Guid Id) : IRequest<TenantStatusResponse>;

public sealed class SuspendTenantCommandHandler(ITenantRepository tenantRepository)
    : IRequestHandler<SuspendTenantCommandRequest, TenantStatusResponse>
{
    public async Task<TenantStatusResponse> Handle(SuspendTenantCommandRequest request, CancellationToken cancellationToken)
    {
        var tenant = await tenantRepository.FindByIdAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"Tenant {request.Id} was not found.", ErrorCodes.TenantNotFound);

        if (tenant.Status != TenantStatus.Active)
        {
            throw new ConflictException(
                $"Tenant {request.Id} is not Active (current status: {tenant.Status}).", ErrorCodes.TenantStatusConflict);
        }

        tenant.Status = TenantStatus.Suspended;
        await tenantRepository.SaveChangesAsync(cancellationToken);

        return new TenantStatusResponse(tenant.Id, tenant.Name, tenant.Status);
    }
}
