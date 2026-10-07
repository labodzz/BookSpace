using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using FluentValidation;

namespace BookSpace.Application.Tenants;

// Deliberately narrow, matching UpdateUserCommandRequest's own precedent: Name only. Status changes go
// through their own dedicated actions (Suspend/Reactivate/Delete below), not a side effect of this PUT -
// same "own explicit lifecycle action" reasoning docs/resource-lifecycle-and-capacity.md already gives,
// applied here to Tenant. DefaultTimeZoneId and every other Tenant column are left out too: nothing in
// this task calls for editing them, and adding a control for them now would be speculative, not
// requirement-driven.
public sealed record UpdateTenantCommandRequest(Guid Id, string Name) : IRequest<TenantStatusResponse>;

public sealed class UpdateTenantCommandRequestValidator : AbstractValidator<UpdateTenantCommandRequest>
{
    public UpdateTenantCommandRequestValidator()
    {
        RuleFor(command => command.Id).NotEmpty();
        RuleFor(command => command.Name).NotEmpty().MaximumLength(200);
    }
}

public sealed class UpdateTenantCommandHandler(ITenantRepository tenantRepository)
    : IRequestHandler<UpdateTenantCommandRequest, TenantStatusResponse>
{
    public async Task<TenantStatusResponse> Handle(UpdateTenantCommandRequest request, CancellationToken cancellationToken)
    {
        var tenant = await tenantRepository.FindByIdAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"Tenant {request.Id} was not found.", ErrorCodes.TenantNotFound);

        // Friendly pre-check only when the name is actually changing - the real, race-proof guarantee is
        // Tenant.Name's own unique index, same two-layer pattern CreateTenantCommandHandler already uses.
        if (!string.Equals(request.Name, tenant.Name, StringComparison.Ordinal)
            && await tenantRepository.ExistsByNameAsync(request.Name, cancellationToken))
        {
            throw new ConflictException($"A tenant named '{request.Name}' already exists.", ErrorCodes.TenantNameConflict);
        }

        tenant.Name = request.Name;
        await tenantRepository.SaveChangesAsync(cancellationToken);

        return new TenantStatusResponse(tenant.Id, tenant.Name, tenant.Status);
    }
}
