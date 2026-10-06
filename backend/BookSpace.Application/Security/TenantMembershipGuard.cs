using BookSpace.Application.Common;

namespace BookSpace.Application.Security;

// Shared by every handler whose action only makes sense for a tenant member - creating/listing bookings,
// creating a recurring series, and any similar feature reachable through a plain [Authorize] route (no
// role restriction, since any Member/Approver/TenantAdmin may call it) where the global SysAdmin would
// otherwise be let through all the way to a bare TenantId!.Value and crash with an unhandled
// InvalidOperationException. A tenant-scoped (role-gated) handler never needs this: the global SysAdmin
// cannot reach a [Authorize(Roles = "TenantAdmin")] endpoint at all - see docs/tenant-onboarding.md,
// "SysAdmin and tenant business data".
internal static class TenantMembershipGuard
{
    public static Guid EnsureTenantMember(ICurrentUserContext currentUserContext)
    {
        if (currentUserContext.TenantId is { } tenantId)
        {
            return tenantId;
        }

        throw new ConflictException(
            "This action is available to tenant members only.", ErrorCodes.TenantMembershipRequired);
    }
}
