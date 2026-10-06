using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Domain.Enums;
using FluentValidation;

namespace BookSpace.Application.Tenants;

// SysAdmin-only soft-delete (see TenantsController and docs/tenant-onboarding.md, "Tenant lifecycle").
// ConfirmName must match the tenant's CURRENT name exactly (ordinal, case-sensitive) - re-verified here,
// server-side, against the database's own current value, never trusted from whatever the frontend's
// confirmation dialog captured: the frontend is not a security boundary, and a stale client-held name
// (e.g. the tenant was renamed in another tab moments earlier) must not let a mistyped confirmation slip
// through on a name that's no longer even current.
//
// Never physically deletes anything - Status becomes Deleted, every row stays. Not reversible through
// this API (see TenantStatus's own doc comment) - there is no "undelete" action in this task's scope.
public sealed record DeleteTenantCommandRequest(Guid Id, string ConfirmName) : IRequest<TenantStatusResponse>;

public sealed class DeleteTenantCommandRequestValidator : AbstractValidator<DeleteTenantCommandRequest>
{
    public DeleteTenantCommandRequestValidator()
    {
        RuleFor(command => command.Id).NotEmpty();
        RuleFor(command => command.ConfirmName).NotEmpty().MaximumLength(200);
    }
}

public sealed class DeleteTenantCommandHandler(ITenantRepository tenantRepository)
    : IRequestHandler<DeleteTenantCommandRequest, TenantStatusResponse>
{
    public async Task<TenantStatusResponse> Handle(DeleteTenantCommandRequest request, CancellationToken cancellationToken)
    {
        var tenant = await tenantRepository.FindByIdAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"Tenant {request.Id} was not found.", ErrorCodes.TenantNotFound);

        if (tenant.Status == TenantStatus.Deleted)
        {
            throw new ConflictException($"Tenant {request.Id} has already been deleted.", ErrorCodes.TenantStatusConflict);
        }

        if (!string.Equals(request.ConfirmName, tenant.Name, StringComparison.Ordinal))
        {
            throw new ConflictException(
                "The confirmation name does not match this tenant's current name.", ErrorCodes.TenantDeleteConfirmationMismatch);
        }

        tenant.Status = TenantStatus.Deleted;
        await tenantRepository.SaveChangesAsync(cancellationToken);

        return new TenantStatusResponse(tenant.Id, tenant.Name, tenant.Status);
    }
}
