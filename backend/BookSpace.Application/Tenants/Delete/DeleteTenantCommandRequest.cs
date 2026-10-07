using BookSpace.Application.Auth;
using BookSpace.Application.Calendar;
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
// Never physically deletes anything - every Tenant/User/Booking/... row stays, forever, under its
// original id. Not reversible through this API (see TenantStatus's own doc comment) - there is no
// "undelete" action in this task's scope.
public sealed record DeleteTenantCommandRequest(Guid Id, string ConfirmName) : IRequest<TenantStatusResponse>;

public sealed class DeleteTenantCommandRequestValidator : AbstractValidator<DeleteTenantCommandRequest>
{
    public DeleteTenantCommandRequestValidator()
    {
        RuleFor(command => command.Id).NotEmpty();
        RuleFor(command => command.ConfirmName).NotEmpty().MaximumLength(200);
    }
}

// Releases the tenant's Name and every one of its users' Email addresses for reuse, while cutting off
// every form of access the tenant's former members could otherwise retain - see docs/tenant-onboarding.md
// ("Tenant lifecycle") for the full design. Five effects, staged on the SAME DbContext and committed
// together by the single SaveChangesAsync call at the end (see the "Atomic" doc note below) - partial
// application (e.g. users withdrawn but invitations still accepting) would reopen exactly the access this
// handler exists to close:
//  1. Tenant.Status -> Deleted (releases the tenant Name - see BookSpaceModelConfiguration's filtered
//     unique index).
//  2. Every one of the tenant's users -> UserStatus.Withdrawn (releases each Email - same filtered-index
//     mechanism). This is NOT the same as Inactive - see UserStatus's own doc comment.
//  3. Every still-active invitation for the tenant is revoked - an unaccepted invite link must not still
//     work after this.
//  4. Every still-active calendar subscription for the tenant is revoked - an external calendar client's
//     feed URL must stop resolving.
//  5. Every still-active refresh token belonging to one of the tenant's (now-withdrawn) users is revoked -
//     closes the RefreshAsync path. A withdrawn/deleted-tenant access token that is still, technically,
//     unexpired is separately rejected on every subsequent API call by IDeletedTenantAccessGuard (see its
//     own doc comment) - revoking refresh tokens alone would only stop a FUTURE refresh, not an
//     already-issued access token already in a caller's hand.
//
// Idempotent and side-effect-free on a repeat call: the tenant.Status guard below throws before any of the
// five effects run a second time, and every one of the four repository methods used here is itself a
// no-op against rows it already transitioned (see their own doc comments) - so even a third-party caller
// who, somehow, got past that guard would change nothing further.
public sealed class DeleteTenantCommandHandler(
    ITenantRepository tenantRepository,
    IUserRepository userRepository,
    IInvitationRepository invitationRepository,
    ICalendarSubscriptionRepository calendarSubscriptionRepository,
    IRefreshTokenRepository refreshTokenRepository)
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

        var withdrawnUserIds = await userRepository.WithdrawAllForTenantAsync(tenant.Id, cancellationToken);
        await invitationRepository.RevokeAllActiveForTenantAsync(tenant.Id, cancellationToken);
        await calendarSubscriptionRepository.RevokeAllActiveForTenantAsync(tenant.Id, cancellationToken);
        await refreshTokenRepository.RevokeAllForUsersAsync(withdrawnUserIds, cancellationToken);

        // Atomic: every repository above shares this same scoped DbContext (see
        // BookSpace.Infrastructure/DependencyInjection.cs) and none of them calls SaveChangesAsync itself -
        // this is the single commit point for all five effects. A failure here (e.g. a genuine, unrelated
        // concurrency conflict) rolls back every staged change together, never a partial release.
        await tenantRepository.SaveChangesAsync(cancellationToken);

        return new TenantStatusResponse(tenant.Id, tenant.Name, tenant.Status);
    }
}
