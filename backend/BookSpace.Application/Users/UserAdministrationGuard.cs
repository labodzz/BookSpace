using BookSpace.Application.Auth;
using BookSpace.Application.Common;

namespace BookSpace.Application.Users;

// Shared by DeactivateUserCommandHandler and RemoveUserRoleCommandHandler - the two actions that can
// take a user out of the "active administrator" set (deactivation directly; role removal only when the
// role removed was their last administrative one). Extracted so the two lockout invariants ("an admin
// cannot act on their own admin capacity" and "the tenant always keeps at least one active admin") are
// defined and worded exactly once, rather than drifting between two independently-written copies - see
// docs/user-administration.md for the full rationale.
internal static class UserAdministrationGuard
{
    private static readonly string[] AdministratorRoles = ["TenantAdmin", "SysAdmin"];

    // SysAdmin delegation is deliberately unsupported by InviteUser/AssignUserRole/RemoveUserRole, for
    // every caller (not just an ordinary TenantAdmin) - no existing documentation defines who, if
    // anyone, may grant or revoke SysAdmin, and guessing a rule for a platform-level escalation role is
    // exactly the kind of decision this batch was told to leave open rather than invent. See
    // docs/user-administration.md's role-delegation matrix and docs/open-questions.md.
    public static readonly IReadOnlyList<string> DelegableRoles = ["Member", "Approver", "TenantAdmin"];

    public static bool IsAdministrator(IReadOnlyCollection<string> roles) => roles.Any(AdministratorRoles.Contains);

    // "Self" here always means "the caller acting on their own account" - both self-deactivation and
    // self-removal-of-an-admin-role are forms of the same mistake (an admin accidentally locking
    // themselves out), so both go through this one check with an action-specific message.
    public static void EnsureNotActingOnOwnAccount(Guid targetUserId, Guid? currentUserId, string action)
    {
        if (currentUserId is { } callerId && callerId == targetUserId)
        {
            throw new ConflictException($"You cannot {action} your own account.", ErrorCodes.UserSelfLockout);
        }
    }

    // Only ever needs to look anything up when the target is actually LEAVING the active-administrator
    // set as a result of this specific change (targetCountsBeforeChange && !targetCountsAfterChange) -
    // a target who never counted, or who still counts afterward, can never be the one tipping the
    // tenant to zero, so no query is needed for either of those cases.
    public static async Task EnsureTenantRetainsAnActiveAdministratorAsync(
        bool targetCountsBeforeChange, bool targetCountsAfterChange, IUserRepository userRepository, CancellationToken cancellationToken)
    {
        if (!targetCountsBeforeChange || targetCountsAfterChange)
        {
            return;
        }

        var activeAdministratorCount = await userRepository.CountActiveAdministratorsAsync(cancellationToken);
        if (activeAdministratorCount <= 1)
        {
            throw new ConflictException(
                "This tenant must always retain at least one active TenantAdmin or SysAdmin.", ErrorCodes.UserLastAdminRemaining);
        }
    }
}
