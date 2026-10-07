using BookSpace.Application.Auth;
using BookSpace.Application.Common;
using BookSpace.Application.Mediator;

namespace BookSpace.Application.Tenants;

// SysAdmin-only platform recovery action (see docs/tenant-onboarding.md, "Tenant lifecycle") - opposite
// number of ResendTenantAdminInvitationCommandHandler: instead of reissuing a fresh token, this simply
// revokes the current active one without replacing it, e.g. before inviting a different person via
// InviteReplacementTenantAdminCommandRequest for the same still-Invited slot, or to shut off a link sent
// to the wrong address.
public sealed record RevokeTenantAdminInvitationCommandRequest(Guid TenantId, Guid UserId) : IRequest<Unit>;

public sealed class RevokeTenantAdminInvitationCommandHandler(
    IUserRepository userRepository, IInvitationRepository invitationRepository)
    : IRequestHandler<RevokeTenantAdminInvitationCommandRequest, Unit>
{
    public async Task<Unit> Handle(RevokeTenantAdminInvitationCommandRequest request, CancellationToken cancellationToken)
    {
        // Confirms the user actually belongs to this tenant before touching anything - same
        // FindByIdForTenantAdministrationAsync bypass ResendTenantAdminInvitationCommandHandler uses, for
        // the identical reason (the SysAdmin's own ambient tenant is never the one being administered).
        var adminUser = await userRepository.FindByIdForTenantAdministrationAsync(request.TenantId, request.UserId, cancellationToken)
            ?? throw new NotFoundException($"User {request.UserId} was not found in tenant {request.TenantId}.", ErrorCodes.UserNotFound);

        var invitation = await invitationRepository.FindActiveByUserIdForTenantAdministrationAsync(request.TenantId, request.UserId, cancellationToken)
            ?? throw new ConflictException(
                $"User {adminUser.Id} has no active invitation to revoke.", ErrorCodes.TenantAdminInvitationNotPending);

        invitation.RevokedAtUtc = DateTimeOffset.UtcNow;
        await invitationRepository.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }
}
