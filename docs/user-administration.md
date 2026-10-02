# User Lifecycle and Role Administration

What Batch 4A built: a production flow for inviting, onboarding, updating, deactivating, reactivating,
and role-managing users, on top of what existed before it - `GET /api/users`, `GET /api/users/me`, and users
created only through `DevelopmentSeeder`/`TestDataSeeder`. Assumes
[authentication.md](authentication.md) (token model, refresh rotation) and
[tenant-isolation.md](tenant-isolation.md) (the global query filter) throughout.

## 1. User lifecycle

`User.Status` (`BookSpace.Domain.Enums.UserStatus`, stored as a string column via `HasConversion<string>()`,
same convention as `Tenant.Status`/`Resource.Status`) has three values:

| Status | Can log in | How reached |
|---|---|---|
| `Active` | Yes | Default for every user constructed without an explicit Status (existing seed/test call sites are unaffected); also the terminal state after accepting an invitation or being reactivated. |
| `Invited` | No | `InviteUserCommandHandler` creates a `User` row directly in this state - the row (and its globally-unique `Email`, and any roles staged via `UserRole`) exists before a password does. |
| `Inactive` | No | `DeactivateUserCommandHandler` (`DELETE /api/users/{id}`) - administratively deactivated. Never a physical delete; see §6. |

**`Active` is deliberately the enum's zero/default member, not `Invited`**, even though `Invited` reads
as the more natural "starting" state. `User.Status` is configured with `HasDefaultValue(UserStatus.Active)`
(see `BookSpaceModelConfiguration.cs`) so the `AddUserStatusAndInvitations` migration backfills every
pre-existing row to `Active`. EF Core treats a property's CLR *default* value as the sentinel meaning "not
explicitly set, use the database DEFAULT constraint" for any property configured with `HasDefaultValue`. If
`Invited` were the zero member, `InviteUserCommandHandler` explicitly writing `Status = UserStatus.Invited`
would look identical to "never set" to EF Core, and EF would silently omit the column from the `INSERT`,
letting the database default (`Active`) apply instead - a newly invited user would come back `Active`, with
an empty password hash, having silently bypassed the entire invitation requirement. This was caught live,
before it ever shipped: `dotnet ef migrations add` emits an explicit warning the first time a
`HasDefaultValue`-configured enum property's CLR default doesn't match its intended sentinel, which is
exactly what surfaced this. Confirmed fixed - no warning after reordering the enum.

`AuthenticationService.LoginAsync` rejects any non-`Active` user with the exact same generic
`Auth.InvalidCredentials` result as an unknown email or a wrong password (see §3) - login never reveals
account status to an outside prober. The check is a fast, explicit `user.Status != UserStatus.Active` guard,
short-circuited via `||` before `IPasswordHasher.Verify` is even called - a deactivated user presenting
their genuinely-correct old password still fails without spending a PBKDF2 verification. Belt-and-suspenders:
an `Invited` user's `PasswordHash` is `string.Empty`, which `PasswordHasher.Verify` already rejects for any
input regardless of this check (its `hashedPassword.Split('.', 3)` guard returns `false` for any
non-conforming stored value).

## 2. Administrative authority - the delegation matrix

No existing documentation defined this before Batch 4A; the model below is what was implemented, following
the task's own conservative default where nothing existing said otherwise.

| Actor | Manage users in own tenant | Manage users in another tenant | Grant/revoke `SysAdmin` |
|---|---|---|---|
| `Member` | No (403) | No (403) | No |
| `Approver` | No (403) | No (403) | No |
| `TenantAdmin` | Yes | No (404 - the target is invisible, not forbidden; see [tenant-isolation.md](tenant-isolation.md)) | **No** |
| `SysAdmin` | Yes | No (404, same as `TenantAdmin` - confirms [open-questions.md](open-questions.md)'s "SysAdmin Scope" entry: `SysAdmin` is elevated but tenant-scoped, not a cross-tenant role) | **No** |

Every mutation endpoint below is `[Authorize(Roles = "TenantAdmin,SysAdmin")]`; every target lookup goes
through the ordinary tenant-filtered `IUserRepository.FindByIdAsync`, so a cross-tenant target id is
invisible (`User.NotFound`, 404) rather than a `403` that would confirm the id exists somewhere else.

**`SysAdmin` role delegation is deliberately unsupported** - `AssignUserRoleCommandRequestValidator` and
`RemoveUserRoleCommandRequestValidator` both reject `"SysAdmin"` outright (`400 Bad Request`), for *every*
caller, not just an ordinary `TenantAdmin`. This is not "`TenantAdmin` can't grant it but `SysAdmin` can" -
no documentation anywhere in this codebase defines who, if anyone, should be able to grant or revoke a
platform-escalation-sounding role like `SysAdmin` (see [open-questions.md](open-questions.md)'s own "SysAdmin
Scope" entry, which already flags this as unresolved), and guessing a delegation rule for it here would be
exactly the kind of decision this batch was told to leave open rather than invent. `SysAdmin` accounts exist
today only through `DevelopmentSeeder`/`TestDataSeeder`, same as before this batch. `InviteUserCommandRequest`
applies the identical restriction to its own `Roles` list (`UserAdministrationGuard.DelegableRoles = ["Member",
"Approver", "TenantAdmin"]`) - an invitation can never grant `SysAdmin` either.

**Deliberately not a constraint**: a user can end up holding zero roles (e.g. an admin removes someone's
only role). This is allowed - a roleless user simply can't perform role-gated actions, which is the
least-privilege direction, not a lockout risk. Only the *administrative* invariants below are actively
enforced.

## 3. Lockout protections

Two independent guards, both centralized in `UserAdministrationGuard` (`BookSpace.Application/Users/`) so
they're defined and worded exactly once rather than drifting between copies, mirroring how `ResourceGuard`
centralizes the "Archived is terminal" check across four handlers.

### Self-lockout

`UserAdministrationGuard.EnsureNotActingOnOwnAccount(targetUserId, currentUserContext.UserId, action)` -
checked first, before anything else, in both `DeactivateUserCommandHandler` and
`RemoveUserRoleCommandHandler` (only when the role being removed is `TenantAdmin`/`SysAdmin` - removing your
own `Member`/`Approver` role is never a lockout concern). Throws `ConflictException` /
`User.SelfLockout` if `targetUserId == currentUserContext.UserId`.

### Last-administrator-standing

`UserAdministrationGuard.EnsureTenantRetainsAnActiveAdministratorAsync(targetCountsBeforeChange,
targetCountsAfterChange, ...)` - only queries `IUserRepository.CountActiveAdministratorsAsync` (a
tenant-scoped count of distinct `Status == Active` users holding `TenantAdmin` and/or `SysAdmin`) when the
target is actually *leaving* the active-administrator set as a result of this specific change; a target who
never counted, or who still counts afterward (e.g. holds both `TenantAdmin` and `SysAdmin` and is only
losing one), can never be the one tipping the count to zero, so no query runs for either case. If the count
would be `<= 1` right before the target stops counting, throws `ConflictException` / `User.LastAdminRemaining`.

**A genuine structural finding, verified rather than assumed**: because every caller of `DeactivateUser`/
`RemoveUserRole` must hold a currently-valid `TenantAdmin`/`SysAdmin` JWT claim just to reach the handler at
all (the controller-level `[Authorize]` gate), and the caller can never be the target (self-lockout rejects
that first), any *different*, successfully-authorized caller necessarily counts as an active administrator
themselves in the ordinary case - keeping `CountActiveAdministratorsAsync()` at 2 or more for as long as
they're the one calling it. This makes "the count would drop to zero via two distinct HTTP callers" not
constructible through the normal request path alone - confirmed by actually trying to build that scenario in
`BookSpace.Api.Tests/Users/UserAdministrationEndpointsTests.cs` before concluding it wasn't possible, not
assumed from the start. The guard still exists deliberately as defense-in-depth: a caller whose own account
was deactivated moments before their still-valid (up to 15 more minutes, see §5) access token was used, or
any future code path that reaches these handlers without going through this exact authorization gate, are
both real scenarios it protects against. It is proven directly at the handler level
(`RemoveUserRoleCommandHandlerTests`, `DeactivateUserCommandHandlerTests`), where that scenario can be
constructed by mocking the count directly, without the structural constraint the real HTTP path imposes.
What the integration tests prove instead is the safe case: removing one of two administrators, leaving the
other, must succeed.

### Approver-role removal and `ResourceApprover` assignments

Added alongside Batch 4B's frontend (the gap surfaced while verifying the frontend's role-removal error
handling against the real backend contract, not a change requested by the frontend's own scope): removing a
user's global `Approver` role while they still hold one or more `ResourceApprover` assignments
(`IResourceApproverRepository.GetResourceIdsByUserAsync`) throws `ConflictException` /
`User.ApproverAssignmentsExist`. Without this guard, an admin could remove the role that
`AssignResourceApproverCommandHandler`'s own eligibility check requires (`Approver`, `TenantAdmin`, or
`SysAdmin`) while leaving the now-ineligible user still listed as an approver on every resource they were
assigned to - a silently stranded assignment, invisible until someone tries to route an approval to them.
Scoped to the `Approver` role specifically, not every role removal: `TenantAdmin`/`SysAdmin` are separately
guarded administrative roles, not the role this specific assignment depends on. The assignments themselves
are never removed automatically - the error message directs the admin to each resource's own "Manage
approvers" page, the same as `ResourceApprover.LastRemaining` already does for the reverse direction (see
[recurring-bookings-and-approvals.md](recurring-bookings-and-approvals.md)). Proven at the handler level
(`RemoveUserRoleCommandHandlerTests`) and end-to-end (`UserAdministrationEndpointsTests`, against the seeded
`AcmeApproverUserId`/`AcmeApprovalRequiredResourceId` fixture that already carries a real assignment).

## 4. Invitation-based onboarding

Prefers an invitation flow over an administrator choosing another user's permanent password, per the
task's own stated preference; no equally-secure existing onboarding mechanism existed to reuse instead.

### Creating an invitation - `POST /api/users/invitations`

`InviteUserCommandRequest(Email, FirstName, LastName, Roles)`, `TenantAdmin`/`SysAdmin` only.

1. `Email` is looked up globally (`IUserRepository.FindByEmailAsync`, the same pre-authentication,
   `IgnoreQueryFilters()` exception documented in [tenant-isolation.md](tenant-isolation.md)) - not
   tenant-scoped, because `Email` has a *global* unique index (see `BookSpaceModelConfiguration.cs`'s
   comment on `User.Email`: login looks a user up by email alone, before any tenant is known, so two
   tenants sharing an email would make that lookup ambiguous).
2. **No existing user with that email**: a new `User` row is created directly (`Status = Invited`,
   `PasswordHash = string.Empty`), `Roles` are applied immediately as ordinary `UserRole` rows (so by the
   time the invitation is accepted, `GetRolesAsync` already returns the right roles for the first JWT -
   no separate "convert invitation to real user" step). An unknown role name throws `NotFoundException` /
   `Role.NotFound` (defensive - the delegable set is fixed and always seeded, but never assumed to exist
   without checking, matching `AssignResourceApproverCommandHandler`'s own precedent for its referenced
   rows).
3. **An existing user with that email who is still `Invited`**: a **reissue** - the same `User` row, no
   new one, no roles re-applied (adjusting an already-staged invitation's roles is what
   `AssignUserRole`/`RemoveUserRole` are for, not something a reissue silently redoes with whatever list
   happens to be resubmitted). Any existing active `Invitation` for that user (not yet accepted, not yet
   revoked) is explicitly revoked before the new one is created.
4. **An existing user with that email who is `Active` or `Inactive`**: `ConflictException` /
   `User.EmailConflict`, one generic message regardless of which - and regardless of whether that
   existing account belongs to this tenant or another. Never reveals that distinction.
5. A cryptographically random 64-byte token (`SecureTokenGenerator.GenerateRawToken()` - base64url,
   trimmed) is generated; only its SHA-256 hash (`SecureTokenGenerator.HashToken`) is ever persisted, on
   a new `Invitation` row (`TenantId`, `UserId`, `CreatedByUserId` = the inviting admin,
   `CreatedAtUtc`, `ExpiresAtUtc` = 72 hours out - fixed, not configurable; no existing precedent in this
   codebase for a tenant-tunable expiry comparable to `Tenant.ApprovalExpiryHours`, and inventing one
   without a concrete requirement would be guessing).
6. `SecureTokenGenerator` (`BookSpace.Application/Auth/`) is shared with `AuthenticationService`'s refresh
   tokens - both are high-entropy random values needing the exact same generate-and-hash treatment;
   originally two private methods on `AuthenticationService`, extracted once a second caller needed the
   identical algorithm, so the two never have a chance to independently drift.
7. `InviteUserResponse(UserId, Email, InvitationToken, InvitationExpiresAtUtc)` returns the **raw** token
   **once**. No email-delivery infrastructure exists anywhere in this codebase (see
   [architecture.md](architecture.md)'s "not yet implemented at all" list) - returning it directly to the
   authorized admin, who is responsible for relaying it to the invitee through whatever channel they
   already use, is the interim workflow. The raw token is never logged, never returned from any other
   endpoint (`GetUser`/`GetUsers` never expose it), and never stored anywhere - only its hash is.

### Accepting an invitation - `POST /api/auth/accept-invitation`

Public (`AuthController` is `[AllowAnonymous]` at the class level, same as `login`/`refresh`/`logout`).
`AcceptInvitationCommandRequest(Token, Password)` → `AuthenticationService.AcceptInvitationAsync` (a fourth
method alongside `LoginAsync`/`RefreshAsync`/`LogoutAsync`, for the same reason those three live there: a
pre-tenant-context flow that bypasses the tenant filter via `IgnoreQueryFilters()`).

1. Hashes the presented token, looks up `Invitation` by `TokenHash`
   (`IInvitationRepository.FindByTokenHashForAcceptanceAsync`, unscoped - see
   [tenant-isolation.md](tenant-isolation.md)).
2. Rejects - with the **exact same generic outcome**, `AcceptInvitationResponse(false,
   ErrorCode: "Invitation.Invalid")` - for every one of: unknown token, already accepted
   (`AcceptedAtUtc != null`), revoked (`RevokedAtUtc != null`), expired (`ExpiresAtUtc <= now`), or the
   underlying `User` somehow no longer `Invited` (e.g. an admin deactivated the pending invite
   independently of the invitation record). None of these are distinguished in the response - same
   "don't reveal account state" philosophy `Login`/`Refresh` already apply to their own failures.
3. Enforces the password policy: `MinimumLength(8)`, `MaximumLength(200)`. **No password-strength policy
   existed anywhere in this codebase before this batch** (`LoginCommandRequestValidator` only checks
   `NotEmpty`, since login never needs to judge strength) - this is a new, deliberately conservative
   baseline, not an existing convention being extended.
4. Sets `User.PasswordHash` via the existing `IPasswordHasher` (PBKDF2-HMAC-SHA256, unchanged), flips
   `Status` to `Active`, sets `Invitation.AcceptedAtUtc`. Both entity changes are persisted in one
   `SaveChangesAsync` call (the same DbContext instance backs both repositories within one request) - the
   same atomic-write pattern `IssueTokensAsync` already uses for its own two-entity write.
5. **Concurrent acceptance of the same token is prevented by `Invitation.RowVersion`** (a real SQL Server
   `rowversion` column, same mechanism `RefreshToken.RowVersion` already uses for rotation races) - a
   losing concurrent request's `SaveChangesAsync` throws `ConflictException`, caught and mapped to the
   same generic `Invitation.Invalid` failure, exactly mirroring `RefreshAsync`'s own
   `catch (ConflictException)` handling of a concurrent-rotation loss. Proven against real SQL Server
   LocalDB by `AcceptInvitationConcurrencyTests` (SQLite, used elsewhere in this suite for speed, has no
   `rowversion` support and the column is a no-op there).
6. Returns **no access or refresh token** - `204 No Content` on success, same as `Logout`. The caller logs
   in normally afterward via `POST /api/auth/login`, proven end-to-end by
   `AcceptInvitation_WithAValidToken_ActivatesTheUserWhoCanThenLogIn`.

**No separate "revoke invitation" endpoint exists.** Re-inviting the same still-pending email (§4, point 3)
already covers "resend" by revoking-then-reissuing; an admin who wants to cancel a pending invitation
outright without resending can `DeactivateUser` the still-`Invited` row instead (§1 - `DeactivateUser`
accepts either `Invited` or `Active` as its starting state), which also makes any surviving copy of the old
invitation link permanently unusable (`AcceptInvitationAsync` re-checks the underlying user's `Status`, not
just the invitation's own fields).

## 5. Role changes and JWT staleness - the accepted tradeoff

Assigning or removing a role (`POST`/`DELETE /api/users/{id}/roles/...`) only affects the *next* access token a
session issues - at login, or at its next refresh. Roles are baked into the JWT at issuance
(`JwtTokenGenerator.GenerateAccessToken`) and this codebase has no mechanism to invalidate an already-issued
token early (no token-version claim, no server-side session store). This is the exact, already-documented
"Dynamic Authorization" open question in [open-questions.md](open-questions.md), unchanged and not
re-litigated by this batch - `ResourceApprover` assignment (a *different* kind of authorization, a live
per-resource database row rather than a role) remains the one thing in this codebase that takes effect
immediately, precisely because it is checked live against the database on every request rather than baked
into a token; see [recurring-bookings-and-approvals.md](recurring-bookings-and-approvals.md). Proven, not
just asserted: `AssigningARole_DoesNotChangeAnAlreadyIssuedAccessTokensClaims` grants `TenantAdmin` to a
user mid-session and confirms their already-issued token still gets `403 Forbidden` on an admin-only route.

## 6. Deactivate / Reactivate

`DELETE /api/users/{id}` (`DeactivateUserCommandHandler`) - same DELETE-verb-means-soft-transition convention
`DeleteResourceCommandHandler` already uses for archiving a `Resource`: never a physical row delete (`Users`
has `RowVersion`-independent FKs from `RefreshToken`/`Invitation`/`ResourceApprover`/`Booking`/etc. that
would make a real delete destructive to history anyway, and the task's own scope explicitly excluded
physically deleting users). Valid from either `Invited` or `Active` (cancelling a pending invitation this
way is intentional - see §4). **Idempotent**, same precedent: deactivating an already-`Inactive` user just
returns their current state, no error.

`POST /api/users/{id}/reactivate` (`ReactivateUserCommandHandler`) is its **own explicit action**, not a side
effect of `UpdateUser` - the identical reasoning
[resource-lifecycle-and-capacity.md](resource-lifecycle-and-capacity.md) already gives for why `Resource`
reactivation, if it's ever built, must be a dedicated action rather than relaxing `UpdateResourceCommandHandler`'s
own Archived check. Unlike Deactivate, **not** idempotent and **not** valid from every non-target status:
only `Inactive -> Active`. Already `Active` is a genuine no-op request (nothing to reactivate) and still
`Invited` is a different transition entirely (accepting the invitation, not an admin action) - both are
rejected with `ConflictException` / `User.StatusConflict` and a status-specific message, rather than
silently doing nothing or guessing which one the caller meant.

## 7. Listing and reading users

`GET /api/users` (`GetUsersQueryRequest`) gained a fourth optional filter, `Status` (alongside the `Search`/
`Role`/`Ids` filters `/api/users` already had from the ResourceApprover-management batch), and its
`UserSummaryResponse` items now carry `Status` alongside the `Roles` field that batch already added.
**Deliberately no implicit status filter when `Status` is omitted** - unlike `IResourceRepository.GetPagedAsync`,
which excludes `Archived` resources by default (a public browsing list, for end users picking something to
book), a user-administration list is for admins managing the *whole* roster, including deactivated accounts
they might reactivate; hiding `Inactive` users by default would make deactivated staff seem to vanish
entirely from the admin's own view of their tenant.

`GET /api/users/{id}` (`GetUserQueryRequest`, new) returns the same fields plus `CreatedAtUtc` and
`PendingInvitationExpiresAtUtc` - the latter set only when `Status == Invited` and an active invitation
currently exists, letting the future admin UI show "invited, expires \<date\>" without a separate
invitations-listing endpoint. Never carries anything about the invitation's token itself - see §4's note on
where the raw token is (and is not) ever returned.

## 8. Updating basic user data

`PUT /api/users/{id}` (`UpdateUserCommandRequest(Id, FirstName, LastName)`) - deliberately narrow. `Email` is
not editable here: it is globally unique and immutable through this endpoint, since changing a user's email
is a distinct, higher-stakes operation with its own verification concerns this batch's scope did not ask
for. `Status` and roles are each their own dedicated action (§6, §2) for the same "own explicit lifecycle
action" reasoning already established for `Resource`.

## 9. What this batch deliberately does not do

Out of scope by explicit instruction, not oversight:

- **Password reset for an already-`Active` user.** Only invitation-based initial password setup exists.
  No "forgot password" flow, no admin-initiated reset-for-an-established-user action.
- **Audit-log infrastructure.** `Invitation.CreatedByUserId` records who invited whom, and
  `RefreshToken`/booking cancellation already record actor ids elsewhere in this codebase, but no general
  audit trail exists for user-administration actions (role grants/revocations, deactivations) beyond what
  the entities themselves already carry.
- **Session-management UI or an admin "log out this user everywhere" action.** Deactivating a user stops
  future logins but does not revoke their currently-valid refresh token family - the same
  session-lifetime tradeoff already tracked as unresolved in
  [open-questions.md](open-questions.md)'s "Session Security" entry.
- **Physically deleting a user row.** Deactivation is the only "removal" action; see §6.
- **`ResourceApprover` assignment changes.** Unaffected by this batch entirely - still managed exactly as
  documented in [recurring-bookings-and-approvals.md](recurring-bookings-and-approvals.md).
- **Cross-tenant `SysAdmin` administration, or Tenant CRUD.** Neither exists; `SysAdmin` remains
  tenant-scoped (§2), matching the already-resolved "SysAdmin Scope" open question.
- **Rate-limiting `POST /api/auth/accept-invitation`.** Considered and deliberately declined: the presented
  token is 512 bits of cryptographically secure randomness (`RandomNumberGenerator.GetBytes(64)`) - brute-
  forcing it is not achievable at any HTTP-reachable rate, so a rate limit here would be pure
  defense-in-depth with no real vulnerability behind it, unlike `POST /api/auth/login`'s rate limit, which
  defends a comparatively low-entropy, human-chosen password.
- **The Angular administration UI.** Backend-only batch; the frontend is Batch 4B.

## 10. Testing strategy and evidence

- **`BookSpace.Application.Tests/Auth/AuthenticationServiceTests.cs`**: `AcceptInvitationAsync` - valid
  token activates the user and marks the invitation accepted; unknown/already-accepted/revoked/expired
  tokens and a no-longer-`Invited` underlying user all return the identical generic failure; a concurrent-
  accept race loss (mocked `ConflictException`) fails cleanly. `LoginAsync` - a non-`Active` user (both
  `Inactive` and `Invited`) fails with the identical result an unknown email/wrong password would, and
  never even calls `IPasswordHasher.Verify`.
- **`BookSpace.Application.Tests/Users/{Create,Update,Delete,Get}/*HandlerTests.cs`**: every handler's own
  logic in isolation - reissue-revokes-the-previous-invitation, cross-tenant-email-never-revealed,
  duplicate-role-assignment, role-not-held-on-removal, self-lockout (both deactivation and role removal,
  and that removing a *non*-administrative role from yourself is unaffected), last-admin-standing (both
  the block and the safe multi-admin case, and that an already-`Inactive` admin's role removal never even
  queries the count), idempotent deactivation from either starting status, `Reactivate`'s three-way status
  branching, `GetUser`'s pending-invitation surfacing.
- **`BookSpace.Api.Tests/Users/UserAdministrationEndpointsTests.cs`**: full HTTP-pipeline coverage - RBAC
  across every new endpoint, tenant isolation (404 on a cross-tenant target), the invite -> accept -> login
  round trip, reissue invalidating the previous token, a deactivated user failing login and a reactivated
  one succeeding again, `SysAdmin` rejected by the validator on both assign and remove, and the
  already-issued-token-keeps-its-stale-claims proof (§5). `GET /api/users`/`GET /api/users/me` themselves stay in
  the pre-existing `UsersEndpointsTests.cs`, extended only with the new `Status` field/filter.
- **`BookSpace.Infrastructure.Tests/Persistence/AcceptInvitationConcurrencyTests.cs`** (real SQL Server
  LocalDB, required - SQLite has no `rowversion` support): two genuinely concurrent acceptances of the
  same token - exactly one succeeds and sets the password, the other fails cleanly, and the final
  `PasswordHash` matches exactly one of the two candidate passwords, never a corrupted mix. A second test
  proves accepting an already-accepted invitation (sequential, not concurrent) leaves the original
  password untouched.
- **`BookSpace.Infrastructure.Tests/Persistence/MigrationApplicationTests.cs`**: pre-existing test, updated
  - it seeds a `User` row at a migration checkpoint *before* `AddUserStatusAndInvitations` runs, so the
  seed now uses a raw SQL `INSERT` (the columns that existed at that checkpoint) instead of the current EF
  model, the same way that test's own `RecurringSeries` row already had to, for the same reason.

## 11. Frontend administration UI (Batch 4B)

Existing-user management is now available in the Angular app, extending the pre-existing Users feature
(`frontend/src/app/features/users/`) rather than creating a second, parallel user list. Both admin routes -
`/users` (list) and `/users/:id` (detail/edit/lifecycle/roles) - are gated by
`roleGuard('TenantAdmin', 'SysAdmin')`, matching every other admin-only route in the app.

**Explicitly deferred, not implemented**: no invite-user form, no invitation-token display, no copyable
invitation link, no accept-invitation page, and no invitation-reissue control exist anywhere in this UI. The
Batch 4A invitation backend (§4) is untouched and fully reachable via its own HTTP endpoints, but nothing in
the frontend calls `POST /api/users/invitations` or exposes an `InvitationToken` value - an `Invited` user's row
shows only their status badge and, if present, `pendingInvitationExpiresAtUtc`, with a neutral note that
"invitation delivery will be handled by the onboarding email workflow" once background jobs and email
sending exist. No background job or email-sending infrastructure was added by this batch either, backend or
frontend - both remain future work.

**List** (`user-list/`): server-side paginated, with debounced (300ms), `switchMap`-cancelled search plus
role and status filters, all reflected in the URL's own query params. That's a deliberate choice, not
incidental: it's what lets the detail page's "Back to users" link return to the exact same filtered,
paginated view the admin left, without a shared service between the two routes.

**Detail** (`user-detail/`): one page for viewing, editing `firstName`/`lastName` (the only fields
`UpdateUserCommandRequest` accepts - no email or password field exists here, matching §8), lifecycle
actions, and role management, rather than splitting an already-small editable surface across several routes.

**Deactivation is presented as what it is, not as deletion** - the confirmation dialog explains that the
user can no longer log in, that refresh-token sessions are revoked while an already-issued access token may
remain valid for up to 15 minutes (§5), that bookings/history are preserved, and that `ResourceApprover`
assignments are not automatically removed. Self-deactivation and self-removal of one's own administrative
role are disabled client-side (`isSelf()`/`canRemoveOwnRole()`) for the signed-in administrator's own row,
but every mutation still goes through the real backend call and maps `User.SelfLockout`/
`User.LastAdminRemaining`/`User.ApproverAssignmentsExist` to specific, friendly messages - the client-side
disabling is a convenience, never the actual enforcement, since the frontend's own notion of "who am I" (the
decoded JWT) can always be stale.

**Roles**: only `Member`, `Approver`, and `TenantAdmin` are ever offered as assignable/removable controls
(`DELEGABLE_ROLES`, mirroring `UserAdministrationGuard.DelegableRoles` exactly) - `SysAdmin` delegation
controls are absent entirely, and `ResourceApprover` never appears as a global role anywhere in this UI; it
remains a separate, per-resource, live-checked assignment managed only from each resource's own "Manage
approvers" page (§9's existing distinction, unchanged). Removing `Approver` while the target still holds
`ResourceApprover` assignments surfaces the new `User.ApproverAssignmentsExist` conflict (added this batch -
see §3) with the exact explanation the admin needs and a pointer to where the assignments actually live;
the frontend never deletes those assignments itself. Every successful role change's toast explicitly states
that it applies "once the user's session next refreshes or they log in again" (§5's JWT-staleness tradeoff,
surfaced to the admin rather than only documented here).
