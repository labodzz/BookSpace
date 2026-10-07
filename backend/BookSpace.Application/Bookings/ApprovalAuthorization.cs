using BookSpace.Application.Common;
using BookSpace.Application.Resources;
using BookSpace.Application.Security;

namespace BookSpace.Application.Bookings;

// Shared by ApproveBookingCommandHandler and RejectBookingCommandHandler. Controller-level
// [Authorize(Roles = "Approver,TenantAdmin")] is coarse - it only proves the caller holds SOME
// approval-capable role, not that they're allowed to decide on THIS booking's resource. TenantAdmin
// bypasses the per-resource check (the same administrative-override role this codebase already gives
// them over resource management generally); a plain Approver must be a ResourceApprover for the
// specific resource. SysAdmin deliberately does NOT get this bypass: a global SysAdmin is not a member
// of any tenant and must not approve/reject/cancel/check-in that tenant's bookings - see
// docs/tenant-onboarding.md, "SysAdmin and tenant business data". In practice a global SysAdmin can
// never even reach a specific booking this way: Booking is ITenantOwned, and the global query filter
// already returns nothing for a null-TenantId caller, so this check is defense in depth, not the only
// thing stopping it.
internal static class ApprovalAuthorization
{
    public static async Task EnsureCallerCanDecideAsync(
        Guid resourceId, IResourceApproverRepository resourceApproverRepository, ICurrentUserContext currentUserContext, CancellationToken cancellationToken)
    {
        if (IsTenantAdmin(currentUserContext))
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

    // Shared with CancelBookingCommandHandler's and CheckInBookingCommandHandler's owner-or-TenantAdmin
    // checks, so the role name can never drift out of sync between the three places a Bookings action
    // bypasses an ownership/assignment check.
    public static bool IsTenantAdmin(ICurrentUserContext currentUserContext) =>
        currentUserContext.Roles.Contains("TenantAdmin");
}
