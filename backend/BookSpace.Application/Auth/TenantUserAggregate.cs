namespace BookSpace.Application.Auth;

// A still-Invited TenantAdmin - exactly enough to let a SysAdmin resend or revoke their invitation on
// the tenant detail screen (see TenantDetailResponse), never anything more (no name, no invitation
// token/expiry - those stay behind the existing Resend/Revoke actions' own responses).
public sealed record PendingTenantAdminContact(Guid UserId, string Email);

// What a SysAdmin's tenant list/detail screen needs about a tenant's users - counts, the active
// TenantAdmin contact(s), and any still-Invited TenantAdmin(s) platform recovery might need to act on -
// never full user rows (see IUserRepository.GetTenantUserAggregatesAsync). A tenant with zero users
// (should not normally happen - CreateTenantCommandHandler always creates the first TenantAdmin
// atomically with the Tenant - but not impossible to imagine after a future admin-removal feature) is
// simply all zeros / empty lists, not a missing/null aggregate.
public sealed record TenantUserAggregate(
    int TotalUserCount,
    int ActiveUserCount,
    int InvitedUserCount,
    int InactiveUserCount,
    IReadOnlyList<string> ActiveTenantAdminEmails,
    IReadOnlyList<PendingTenantAdminContact> PendingTenantAdminInvitations)
{
    public static readonly TenantUserAggregate Empty = new(0, 0, 0, 0, [], []);
}
