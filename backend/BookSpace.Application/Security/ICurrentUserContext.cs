namespace BookSpace.Application.Security;

// What every tenant-scoped query and every authorization check reads from. TenantId is null in two
// distinct situations, told apart by UserId:
//  - Unauthenticated (UserId also null): no JWT at all, or a request mid-login/refresh before a tenant
//    is known. Those two flows look the user up via UserRepository.FindByEmailAsync/
//    FindByIdForAuthenticationAsync, which call IgnoreQueryFilters() explicitly - they never rely on
//    this context's TenantId being treated as "unrestricted" by BookSpaceDbContext's query filters. It
//    is not: a null TenantId makes every ITenantOwned query filter match NOTHING (SQL NULL = NULL ->
//    UNKNOWN against a non-nullable TenantId column), and User's own, separately-registered filter adds
//    an explicit `TenantId != null` guard for the same reason - see
//    BookSpaceDbContext.ApplyUserTenantFilter's own doc comment for why User needs that extra guard and
//    the others don't. Either way: fail CLOSED, not open.
//  - Authenticated global platform user (UserId set, TenantId null): the global SysAdmin's JWT
//    deliberately carries no tenant_id claim at all (see JwtTokenGenerator) - see IsGlobalPlatformUser.
public interface ICurrentUserContext
{
    Guid? UserId { get; }
    Guid? TenantId { get; }
    IReadOnlyCollection<string> Roles { get; }

    // True only for an authenticated request with no tenant context - the global SysAdmin. False for an
    // anonymous request (UserId also null) and false for any tenant-scoped user. Prefer this over a bare
    // "TenantId is null" check in handler code: it reads as the business concept ("is the caller the
    // global platform admin") rather than the mechanism behind it.
    bool IsGlobalPlatformUser => UserId is not null && TenantId is null;
}
