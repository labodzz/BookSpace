using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Tenants;

// SysAdmin-only (see TenantsController). Only Suspended -> Active is valid - a Deleted tenant can never
// be reactivated through this action (see TenantStatus's own doc comment: soft-delete is not reversible
// through this API), and an already-Active tenant is a clear conflict, not a silent no-op - same strict-
// transition precedent as SuspendTenantCommandHandler/ReactivateUserCommandHandler.
public sealed record ReactivateTenantCommandRequest(Guid Id) : IRequest<TenantStatusResponse>;

public sealed class ReactivateTenantCommandHandler(ITenantRepository tenantRepository)
    : IRequestHandler<ReactivateTenantCommandRequest, TenantStatusResponse>
{
    public async Task<TenantStatusResponse> Handle(ReactivateTenantCommandRequest request, CancellationToken cancellationToken)
    {
        var tenant = await tenantRepository.FindByIdAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"Tenant {request.Id} was not found.", ErrorCodes.TenantNotFound);

        if (tenant.Status != TenantStatus.Suspended)
        {
            var reason = tenant.Status == TenantStatus.Active
                ? "is already active."
                : "has been deleted and cannot be reactivated.";
            throw new ConflictException($"Tenant {request.Id} {reason}", ErrorCodes.TenantStatusConflict);
        }

        tenant.Status = TenantStatus.Active;
        await tenantRepository.SaveChangesAsync(cancellationToken);

        return new TenantStatusResponse(tenant.Id, tenant.Name, tenant.Status);
    }
}
