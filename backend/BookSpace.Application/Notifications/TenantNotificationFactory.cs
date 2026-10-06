using System.Text.Json;
using BookSpace.Domain.Entities;

namespace BookSpace.Application.Notifications;

public static class TenantNotificationTypes
{
    public const string AdminInvitation = "Tenant.AdminInvitation";
}

// InvitationUrl is the raw invitation token's one and only at-rest home besides Invitation.TokenHash's
// irreversible SHA-256 hash - see docs/tenant-onboarding.md ("Where the raw token lives, and for how
// long") for exactly where this ends up (NotificationOutboxItem.PayloadJson, until that row is processed)
// and why: a future sender task (not yet wired into the worker cycle - see docs/background-jobs.md) needs
// the actual link to put in the email body, and the token cannot be recomputed from its hash. Deliberately
// nothing else sensitive: no password, no other credential, ever appears in this payload or anywhere in
// this notification's own lifecycle.
public sealed record TenantAdminInvitationNotificationPayload(
    Guid TenantId, string TenantName, string RecipientFirstName, string InvitationUrl, DateTimeOffset ExpiresAtUtc);

// Builds the NotificationOutboxRequest for the one notification type tenant onboarding ever enqueues - see
// docs/tenant-onboarding.md. Mirrors BookingNotificationFactory's own shape (one static builder method,
// one payload record, one deterministic IdempotencyKey format) for an entirely different, non-booking
// notification family.
public static class TenantNotificationFactory
{
    public static NotificationOutboxRequest AdminInvitation(Tenant tenant, User adminUser, Invitation invitation, string invitationUrl)
    {
        var payload = new TenantAdminInvitationNotificationPayload(tenant.Id, tenant.Name, adminUser.FirstName, invitationUrl, invitation.ExpiresAtUtc);

        // Keyed on the INVITATION's own id, not the user's or the tenant's: a resend
        // (ResendTenantAdminInvitationCommandHandler) revokes the previous Invitation row and inserts a
        // genuinely new one (the same "reissue" convention InviteUserCommandHandler already uses) - keying
        // on adminUser.Id or tenant.Id alone would make that second, deliberately distinct notification
        // collide with the first's IdempotencyKey forever, since both would describe "an invitation for
        // this same user/tenant." Invitation.Id is a fresh Guid for every issuance (including every
        // resend), so each one gets its own outbox row, while the SAME invitation can never be enqueued
        // twice (a handler retried after it already committed hits this exact same key again, correctly
        // reported as AlreadyExists rather than a duplicate email). See docs/tenant-onboarding.md ("Reissue
        // and the outbox idempotency key").
        return new NotificationOutboxRequest(
            tenant.Id,
            TenantNotificationTypes.AdminInvitation,
            adminUser.Id,
            JsonSerializer.Serialize(payload),
            $"tenant-admin-invitation:{invitation.Id}");
    }
}
