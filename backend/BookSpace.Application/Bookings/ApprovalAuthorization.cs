using BookSpace.Application.Common;
using BookSpace.Application.Resources;
using BookSpace.Application.Security;

namespace BookSpace.Application.Bookings;

// Shared by ApproveBookingCommandHandler and RejectBookingCommandHandler. Controller-level
// [Authorize(Roles = "Approver,TenantAdmin,SysAdmin")] is coarse - it only proves the caller holds SOME
// approval-capable role, not that they're allowed to decide on THIS booking's resource. TenantAdmin/
// SysAdmin bypass the per-resource check (the same administrative-override role this codebase already
// gives them over resource management generally); a plain Approver must be a ResourceApprover for the
// specific resource.
internal static class ApprovalAuthorization
{
    public static async Task EnsureCallerCanDecideAsync(
        Guid resourceId, IResourceApproverRepository resourceApproverRepository, ICurrentUserContext currentUserContext, CancellationToken cancellationToken)
    {
        if (IsTenantAdminOrSysAdmin(currentUserContext))
        {
            return;
        }

        var isResourceApprover = await resourceApproverRepository.FindByResourceAndUserAsync(
            resourceId, currentUserContext.UserId!.Value, cancellationToken) is not null;

        if (!isResourceApprover)
        {
            // The caller already knows this booking exists (RBAC already let them reach this handler,
            // and they hold the Approver role) - this isn't a cross-tenant/cross-user existence leak the
            // way a plain NotFoundException protects elsewhere, so a clear, distinguishable conflict is
            // more honest than a fake 404.
            throw new ConflictException(
                $"You are not an approver for resource {resourceId}.", ErrorCodes.BookingApprovalForbidden);
        }
    }

    // Shared with CancelBookingCommandHandler's owner-or-TenantAdmin check, so the two role names can
    // never drift out of sync between the two places a Bookings action bypasses an ownership/assignment
    // check.
    public static bool IsTenantAdminOrSysAdmin(ICurrentUserContext currentUserContext) =>
        currentUserContext.Roles.Contains("TenantAdmin") || currentUserContext.Roles.Contains("SysAdmin");
}
