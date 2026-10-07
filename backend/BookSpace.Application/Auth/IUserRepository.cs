using BookSpace.Application.Common;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Auth;

public interface IUserRepository
{
    // Excludes Withdrawn users (a tenant soft-delete releases their email for reuse - see UserStatus and
    // BookSpaceModelConfiguration) - always resolves to the live account when a withdrawn and a live row
    // happen to share an email.
    Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken);
    Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken);

    // Bypasses the tenant query filter, same reasoning as FindByEmailAsync: refresh (like login)
    // happens with no tenant context yet - the incoming request carries a refresh token, not a valid
    // access token, so ICurrentUserContext.TenantId is null. Only AuthenticationService.RefreshAsync
    // should call this; every other lookup-by-id (e.g. validating an approver assignment target) must
    // keep using the tenant-filtered FindByIdAsync above.
    Task<User?> FindByIdForAuthenticationAsync(Guid id, CancellationToken cancellationToken);

    // Explicitly bypasses the tenant query filter and re-applies BOTH tenantId and id in the same Where
    // clause - a different, narrower exception than FindByIdForAuthenticationAsync's (that one has NO
    // tenant to filter by at all; this one has one, just never the CALLER's own). A SysAdmin administering
    // a tenant they do not themselves belong to (tenant onboarding - see docs/tenant-onboarding.md) is the
    // one sanctioned caller: ICurrentUserContext.TenantId is always the SysAdmin's OWN tenant, never the
    // target tenant being onboarded, so the ordinary tenant-filtered FindByIdAsync can never find that
    // user. Only ResendTenantAdminInvitationCommandHandler should call this.
    Task<User?> FindByIdForTenantAdministrationAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetRolesAsync(Guid userId, CancellationToken cancellationToken);

    // Relies on the DbContext's global tenant query filter to scope results - callers must not
    // add their own TenantId filter on top of this. Paged (unlike ResourceType's GetAllAsync): a
    // tenant's user roster grows roughly linearly with headcount and is never pruned, unlike small,
    // human-curated lists such as resource types. search matches FirstName/LastName/Email
    // (case-insensitivity depends on the database collation, same as every other text lookup in this
    // codebase); role restricts to users holding that exact global role name; ids restricts to that
    // specific set of user ids (see GetUsersQueryRequest for why this is distinct from role); status
    // restricts to that exact UserStatus, with no implicit default filter when omitted (see
    // GetUsersQueryRequest's own comment on why this differs from IResourceRepository.GetPagedAsync's
    // status). All four are optional and applied on top of - never instead of - the tenant filter.
    Task<PagedResult<User>> GetPagedAsync(
        int page, int pageSize, string? search, string? role, IReadOnlyList<Guid>? ids, UserStatus? status,
        CancellationToken cancellationToken);

    // Batched to resolve roles for a whole page of users in one query, avoiding an N+1 per-user
    // GetRolesAsync call when enriching a GetUsersQueryRequest page. A user with no roles simply has no
    // entry in the result rather than an empty list, so callers should default missing keys to empty.
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> GetRolesByUserIdsAsync(
        IReadOnlyList<Guid> userIds, CancellationToken cancellationToken);

    Task AddAsync(User user, CancellationToken cancellationToken);

    // Looked up by name (not id) because every caller (InviteUser/AssignUserRole/RemoveUserRole) only
    // ever has the role's name to work with - the Roles table is a small, fixed, seeded catalog, not
    // something callers are expected to know ids for. Lives here rather than on a dedicated
    // IRoleRepository for the same reason GetRolesAsync already does: role reads/writes are always in
    // service of a specific user's membership, never a standalone "manage the Roles catalog" concern
    // (there is no Role CRUD in this codebase, by design - see docs/user-administration.md).
    Task<Role?> FindRoleByNameAsync(string name, CancellationToken cancellationToken);

    Task AddRoleAsync(Guid userId, Guid roleId, CancellationToken cancellationToken);

    // A no-op (not an error) if the user doesn't currently hold this role - callers that need "was this
    // role actually held" to behave differently (e.g. RemoveUserRoleCommandHandler's 404) check
    // GetRolesAsync themselves first, the same way RemoveResourceApproverCommandHandler checks
    // FindByResourceAndUserAsync before calling RemoveAsync rather than relying on the removal itself
    // to report whether anything existed.
    Task RemoveRoleAsync(Guid userId, Guid roleId, CancellationToken cancellationToken);

    // Tenant-scoped (via the global query filter) count of distinct users who are both Status.Active
    // and hold TenantAdmin and/or SysAdmin - the number DeactivateUser/RemoveUserRole check against to
    // enforce "a tenant must always retain at least one active administrator". Invited/Inactive holders
    // of an admin role deliberately don't count - they cannot log in, so they provide no actual
    // administrative capability right now.
    Task<int> CountActiveAdministratorsAsync(CancellationToken cancellationToken);

    // Bypasses the tenant query filter by design - this is the one place a SysAdmin (ICurrentUserContext.
    // TenantId == null) legitimately needs user-derived facts about tenants that are never its own. Used
    // by the tenant list/detail screens (see GetTenantsQueryRequest/GetTenantQueryRequest). Projects ONLY
    // counts and the active TenantAdmin(s)' email - never full user rows, never Invited/Inactive users'
    // identities, matching docs/tenant-onboarding.md's "aggregates only, never business/personal data
    // beyond what platform recovery needs" rule. A tenantId with no users at all is simply absent from
    // the result rather than present with zeros - callers default a missing key to
    // TenantUserAggregate.Empty.
    Task<IReadOnlyDictionary<Guid, TenantUserAggregate>> GetTenantUserAggregatesAsync(
        IReadOnlyList<Guid> tenantIds, CancellationToken cancellationToken);

    // Bypasses the tenant query filter by design - see the implementation's own doc comment. The one
    // sanctioned caller is DeleteTenantCommandHandler. Idempotent: a user already Withdrawn is left alone
    // and excluded from the returned id list, so calling this again (a repeated delete attempt, or the
    // ReleaseTenantNameAndUserEmailOnSoftDelete migration re-processing an already-handled tenant) revokes
    // nothing a second time.
    Task<IReadOnlyList<Guid>> WithdrawAllForTenantAsync(Guid tenantId, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
