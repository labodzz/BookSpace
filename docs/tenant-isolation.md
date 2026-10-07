# Tenant Isolation

The invariant this document exists to protect:

> A missing tenant context must not result in unscoped access to tenant-owned data.

## How tenant context is derived

An access token JWT carries a `tenant_id` claim **only for a tenant-scoped user**
(`JwtTokenGenerator.GenerateAccessToken` emits it conditionally from `user.TenantId` at issuance). The
global `SysAdmin` - a platform administrator who is not a member of any tenant, see
[tenant-onboarding.md](tenant-onboarding.md) - has `TenantId == null` on its own `User` row and
deliberately gets **no** `tenant_id` claim at all, not a placeholder/empty one.
`ICurrentUserContext.TenantId` reads that claim per-request (`Guid.TryParse` against a missing claim
yields `null`, never a thrown exception). Three situations resolve to a null `TenantId`: login (no token
presented yet), refresh (a refresh token, not an access token, is presented), and an authenticated
request from the global SysAdmin.

## Normal tenant-scoped data access

`BookSpaceDbContext.OnModelCreating` reflectively finds every entity implementing `ITenantOwned` and
applies a global EF Core query filter to it:

```csharp
builder.Entity<TEntity>().HasQueryFilter(entity => entity.TenantId == currentUserContext.TenantId);
```

This runs for every `IQueryable` against that entity - a developer cannot forget it, because there is
no `.Where()` to forget. **The filter fails closed**: if `currentUserContext.TenantId` is `null`
(no tenant context resolved), the predicate is `entity.TenantId == null`, which never matches a real
row - the query returns nothing, not everything. This was a deliberate change from the previous
behavior, which treated a null context as "see every tenant's rows." This reasoning relies on
`TenantId` being a **non-nullable** `Guid` column: a real value can never be null-equal to the ambient
`null`, in either C# or SQL comparison semantics, so there is no ambiguity to resolve.

Writes are equally disciplined: every command handler that creates a tenant-owned row sets
`TenantId = currentUserContext.TenantId!.Value` explicitly, or (where the caller might legitimately be
the global SysAdmin with no tenant at all - any handler reachable through a plain `[Authorize]` route
with no role restriction, such as creating a booking) goes through `TenantMembershipGuard.
EnsureTenantMember(currentUserContext)` first, which throws a clean `ConflictException`
(`Common.TenantMembershipRequired`) instead of crashing on a bare `!.Value`. No request DTO in the
codebase has a client-suppliable `TenantId` field.

### `User` is the one exception to "`TenantId` is non-nullable"

`User.TenantId` is `Guid?`, not `Guid` - the global SysAdmin's own row has `TenantId == null`. `User`
therefore does **not** implement `ITenantOwned` (that interface's contract is a required, non-nullable
`TenantId`); it gets its own, separately-registered filter in
`BookSpaceDbContext.ApplyUserTenantFilter`:

```csharp
builder.Entity<User>().HasQueryFilter(user => currentUserContext.TenantId != null && user.TenantId == currentUserContext.TenantId);
```

The explicit `currentUserContext.TenantId != null` guard is load-bearing, not redundant: EF Core's
default translation of a `Guid? == Guid?` comparison rewrites it using **C# null-comparison semantics**
(`(a == b) OR (a IS NULL AND b IS NULL)`), not raw SQL `NULL = NULL -> UNKNOWN`. Without the guard, a
global SysAdmin querying with no ambient tenant (`currentUserContext.TenantId == null`) would match
every *other* global SysAdmin's row too (anyone else with `TenantId == null`), not nothing - the
opposite of fail-closed. This was a real bug caught during development by
`TenantIsolationFilterTests.SysAdminFilter_WithNoTenantContext_SeesNoGlobalSysAdminEitherIncludingItsOwn`
and the equivalent step in `TenantOnboardingEndToEndTests`, not merely reasoned about. **Never copy the
generic `entity.TenantId == currentUserContext.TenantId` filter shape onto a nullable `TenantId`
column** - it needs this explicit left-hand guard instead.

## Explicit authentication exceptions

Login and refresh need to read a `User` row (itself `ITenantOwned`) before any tenant is known - that
is the whole point of those two operations. Rather than let the global filter fail open for everyone
just to accommodate these two callers, `IUserRepository` exposes two narrow, explicitly-unscoped
methods, both implemented with `.IgnoreQueryFilters()`:

| Method | Caller | Why it's safe to bypass the filter |
|---|---|---|
| `FindByEmailAsync` | `AuthenticationService.LoginAsync` | Email has a global unique index (not per-tenant) - there is no ambiguity to resolve, since the lookup key already uniquely identifies one user across every tenant. |
| `FindByIdForAuthenticationAsync` | `AuthenticationService.RefreshAsync`, `AuthenticationService.AcceptInvitationAsync` | The refresh token (or invitation token) itself - a high-entropy, unguessable value, looked up by its own hash - is already the real authorization check; the user id it points to needs no additional tenant scoping on top. |
| `IInvitationRepository.FindByTokenHashForAcceptanceAsync` | `AuthenticationService.AcceptInvitationAsync` | Same reasoning as the row above, for the `Invitation` itself: accepting an invitation happens with no tenant context yet (the caller presents only a raw token), and the token's own unguessability is the authorization boundary. |
| `FindByIdForTenantAdministrationAsync` | `ResendTenantAdminInvitationCommandHandler`, `RevokeTenantAdminInvitationCommandHandler` | The global SysAdmin's own ambient `TenantId` is always `null`, never the tenant it's administering - re-applies `tenantId` explicitly in the same `Where` instead of relying on the ambient filter. |
| `GetTenantUserAggregatesAsync` | `GetTenantsQueryRequest`, `GetTenantQueryRequest` | The SysAdmin tenant list/detail screens legitimately need counts and the active-TenantAdmin contact across tenants that are never the caller's own (it has none) - projects only counts and an email, never full user rows. |

A tenant-scoped (role-gated) handler never needs a bypass of its own: the global SysAdmin cannot reach a
`[Authorize(Roles = "TenantAdmin")]` endpoint at all (see the authorization audit in
[user-administration.md](user-administration.md)). The bypasses above exist only for the handful of
genuinely pre-authentication or platform-administration call sites.

Every other by-id lookup (e.g. `AssignResourceApproverCommandHandler` checking that a target `UserId`
exists before making them an approver) uses the ordinary, filtered `FindByIdAsync` - a cross-tenant
`UserId` is invisible there by design, which doubles as a tenant-ownership check "for free." **Do not
add a third `IgnoreQueryFilters()` caller without the same justification these two have**: a
pre-authentication lookup on a globally-unique key. Anything else bypassing the filter is very likely
an accidental cross-tenant leak waiting to be found.

## Current application-level protection against cross-tenant relationships

Every write path that references a parent entity (e.g. `CreateAvailabilityRuleCommandRequest`
referencing `Resource`, `AssignResourceApproverCommandRequest` referencing both `Resource` and `User`)
loads that parent through a tenant-filtered repository call first (`FindByIdAsync`, `ResourceTypeExistsAsync`,
etc.) and throws `NotFoundException` if it isn't visible. Since a cross-tenant parent is invisible to
that lookup, this correctly rejects any attempt to attach a child to another tenant's parent - as a
consequence of the read-side filter, not because of an explicit ownership check.

### The remaining defense-in-depth gap

**Database-level composite tenant foreign keys are NOT currently implemented.** Every foreign key in
`BookSpaceModelConfiguration.cs` references the parent's plain `Id` (e.g.
`HasForeignKey(rule => rule.ResourceId)`), not a composite `(TenantId, ResourceId)` key. Nothing at the
schema level stops a row from being inserted with `TenantId = A` but a `ResourceId` belonging to tenant
B - the schema only guarantees the referenced row exists *somewhere*, not that it belongs to the same
tenant as the child.

This is not exploitable today only because every current write handler faithfully re-validates the
parent through the tenant-filtered read path described above. **This is an architectural risk to
revisit if a new relationship write path is ever added that skips that pattern** - e.g. a bulk-import
handler, a background job that constructs entities directly, or any future code that resolves a parent
id through something other than the standard repository lookup. A full composite-FK migration across
every tenant-owned relationship was evaluated and deliberately deferred (see
[audit-remediation-summary.md](audit-remediation-summary.md)) because no live exploit path exists and
the migration's blast radius is large; it remains open as a question in
[open-questions.md](open-questions.md).

## Checklist for adding a new tenant-owned relationship

1. The child entity implements `ITenantOwned` and has its own `TenantId` column.
2. The create handler sets `TenantId = currentUserContext.TenantId!.Value` - never from the request DTO.
   If the endpoint is reachable via a plain `[Authorize]` route with no role restriction (so the global
   SysAdmin, with no tenant, could call it), guard with `TenantMembershipGuard.EnsureTenantMember(...)`
   first instead of a bare `!.Value` - see `CreateBookingCommandHandler` for the pattern.
3. Before referencing a parent (or any other tenant-owned entity) by id, load it through its
   tenant-filtered repository method and throw `NotFoundException` if it's missing. Do not assume an id
   passed in a request is valid just because it parses as a `Guid`.
4. If the relationship is exposed as `{parentId}/{childId}` in a route, verify the child actually
   belongs to that parent (not just that both ids exist) - a mismatched pairing should also 404. See
   `DeleteAvailabilityRuleCommandRequest`/`UpdateBlackoutPeriodCommandRequest` for the pattern.
5. Add a cross-tenant test: create the entity as Tenant A, attempt to read/modify it as Tenant B,
   assert `404`, not `403` or a data leak.
6. Do not add a new `IgnoreQueryFilters()` call site without the same justification as the two existing
   ones (pre-authentication, globally-unique-key lookup) - see above.
