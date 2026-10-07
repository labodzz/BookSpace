using BookSpace.Application.Auth;
using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Application.Notifications;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Microsoft.Extensions.Options;

namespace BookSpace.Application.Tenants;

// SysAdmin only - see docs/tenant-onboarding.md ("Resending an invitation"). TenantId/UserId are both
// explicit, caller-known values (CreateTenantResponse already returned both) rather than an ambiguous
// "find the pending admin for this tenant" lookup - a tenant could, in principle, later have more than one
// Invited TenantAdmin (its first admin invites a second before accepting their own), so this never guesses
// which one the caller means.
public sealed record ResendTenantAdminInvitationCommandRequest(Guid TenantId, Guid UserId) : IRequest<ResendTenantAdminInvitationResponse>;

// Mirrors CreateTenantResponse's own shape - never the raw token/URL (see docs/tenant-onboarding.md).
public sealed record ResendTenantAdminInvitationResponse(Guid TenantId, Guid AdminUserId, string AdminEmail, DateTimeOffset InvitationExpiresAtUtc);

// Reissue, not a password reset: this is the EXACT "revoke the previous active invitation, insert a new
// one" convention InviteUserCommandHandler already established for its own reissue branch - deliberately
// reused rather than inventing a second one. Requires the target to still be Invited (never valid for an
// already-Active account - see docs/tenant-onboarding.md for why this must never become a de facto
// password-reset mechanism for an established administrator).
public sealed class ResendTenantAdminInvitationCommandHandler(
    IUserRepository userRepository,
    IInvitationRepository invitationRepository,
    INotificationOutboxWriter notificationOutboxWriter,
    ITenantRepository tenantRepository,
    ICurrentUserContext currentUserContext,
    IOptions<AppOptions> appOptions)
    : IRequestHandler<ResendTenantAdminInvitationCommandRequest, ResendTenantAdminInvitationResponse>
{
    private static readonly TimeSpan InvitationLifetime = TimeSpan.FromHours(72);

    public async Task<ResendTenantAdminInvitationResponse> Handle(ResendTenantAdminInvitationCommandRequest request, CancellationToken cancellationToken)
    {
        var tenant = await tenantRepository.FindByIdAsync(request.TenantId, cancellationToken)
            ?? throw new NotFoundException($"Tenant {request.TenantId} was not found.", ErrorCodes.TenantNotFound);

        var adminUser = await userRepository.FindByIdForTenantAdministrationAsync(request.TenantId, request.UserId, cancellationToken)
            ?? throw new NotFoundException($"User {request.UserId} was not found in tenant {request.TenantId}.", ErrorCodes.UserNotFound);

        if (adminUser.Status != UserStatus.Invited)
        {
            // Covers both "already accepted" (Active) and "deactivated before ever accepting" (Inactive) -
            // neither is ever valid to resend an invitation for. Never a password-reset path: an Active
            // administrator who wants a new password has no mechanism here or anywhere else in this
            // codebase (see docs/user-administration.md §9 - unchanged by this feature).
            throw new ConflictException(
                $"User {adminUser.Id} no longer has a pending invitation (current status: {adminUser.Status}).",
                ErrorCodes.TenantAdminInvitationNotPending);
        }

        var previousActiveInvitation = await invitationRepository.FindActiveByUserIdForTenantAdministrationAsync(request.TenantId, request.UserId, cancellationToken);
        if (previousActiveInvitation is not null)
        {
            previousActiveInvitation.RevokedAtUtc = DateTimeOffset.UtcNow;
        }

        var rawToken = SecureTokenGenerator.GenerateRawToken();
        var invitation = new Invitation
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            UserId = adminUser.Id,
            TokenHash = SecureTokenGenerator.HashToken(rawToken),
            CreatedByUserId = currentUserContext.UserId!.Value,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.Add(InvitationLifetime),
        };
        await invitationRepository.AddAsync(invitation, cancellationToken);

        var invitationUrl = TenantInvitationUrlBuilder.Build(appOptions.Value.PublicBaseUrl!, rawToken);
        var outboxRequest = TenantNotificationFactory.AdminInvitation(tenant, adminUser, invitation, invitationUrl);

        // Same single-commit shape as CreateTenantCommandHandler: the revoked previous invitation, the new
        // invitation, and the new outbox row all land together via EnqueueAsync's own SaveChangesAsync, or
        // none of them do. A genuine race IS possible here too: two genuinely concurrent resend requests for
        // the same user both read the same previousActiveInvitation and both try to revoke it and insert a
        // new active row. Exactly one of the two SaveChangesAsync calls wins - the loser surfaces as EITHER
        // a RowVersion mismatch on the shared previousActiveInvitation row (an optimistic-concurrency
        // conflict - the far more likely outcome, since both requests update that SAME existing row) OR,
        // more rarely, a violation of Invitation's own filtered unique index on UserId ("at most one ACTIVE
        // invitation per user", see BookSpaceModelConfiguration) if the revoke somehow committed first on
        // both sides. NotificationOutboxWriter translates BOTH exception shapes into the identical
        // ConflictException (see its own doc comment for why it, not this handler, is the layer allowed to
        // detect either).
        await notificationOutboxWriter.EnqueueAsync(outboxRequest, cancellationToken);

        return new ResendTenantAdminInvitationResponse(tenant.Id, adminUser.Id, adminUser.Email, invitation.ExpiresAtUtc);
    }
}
