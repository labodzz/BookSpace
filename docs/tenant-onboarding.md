# BookSpace tenant onboarding

Lets a SysAdmin create a brand-new tenant and its first TenantAdmin, who then sets their own password via
an emailed invitation link and logs in - the first genuinely cross-tenant SysAdmin action in this codebase
(see `docs/user-administration.md`, which had previously documented "cross-tenant SysAdmin administration"
as not existing). See `BookSpace.Application/Tenants/`, `BookSpace.Api/Controllers/TenantsController.cs`,
`frontend/src/app/features/tenants/`, and `frontend/src/app/features/auth/accept-invitation/`.

## SysAdmin and tenant business data (platform/tenant separation)

**This section describes the CURRENT model and supersedes any conflicting claim further below** (this
document originally described only the create-tenant/accept-invitation slice; a later task made
`SysAdmin` a true global platform administrator and added the rest of the tenant lifecycle - list,
detail, rename, suspend, reactivate, soft-delete, and replacement-admin recovery actions. The sections
below are kept for the parts that are still accurate - token mechanics, the outbox/transaction boundary,
the email template - but read this one first).

- **`SysAdmin` has no `TenantId`.** `User.TenantId` is `Guid?`; the global SysAdmin's own row has it
  `null`. It is not a member of any tenant, cannot hold `Member`/`Approver`/`TenantAdmin`, cannot create
  bookings, and cannot be a resource approver. See [tenant-isolation.md](tenant-isolation.md) for the
  query-filter mechanics this requires (`BookSpaceDbContext.ApplyUserTenantFilter`, and the EF Core
  null-comparison bug that section documents).
- **`SysAdmin`'s JWT carries no `tenant_id` claim at all** (`JwtTokenGenerator.GenerateAccessToken`
  emits it conditionally now) - not an empty string, not `Guid.Empty`. See
  [authentication.md](authentication.md).
- **`SysAdmin` no longer gets `TenantAdmin`-equivalent access to any tenant's own business functions.**
  Every `[Authorize(Roles = "TenantAdmin,SysAdmin")]` in the codebase (`UsersController`,
  `ResourcesController`, `ResourceTypesController`) is now `[Authorize(Roles = "TenantAdmin")]` only, and
  every in-handler bypass that used to treat them as equivalent (`ApprovalAuthorization`,
  `AssignResourceApproverCommandRequest`'s eligibility check) now excludes `SysAdmin` explicitly. A
  `SysAdmin` calling `POST /api/bookings`, `POST /api/bookings/series`, or `GET /api/bookings` (all plain
  `[Authorize]`, reachable by any tenant member) is rejected by `TenantMembershipGuard` with a clean
  `409 Common.TenantMembershipRequired` (write actions) or gets an always-empty page (`GET /api/bookings`
  - "my bookings" is simply empty for someone who can never have one, not an error).
- **`SysAdmin` instead manages tenant *lifecycle*** through `TenantsController` (expanded by this task -
  see "Tenant lifecycle management" below): list every tenant, see one tenant's platform aggregates,
  rename it, suspend/reactivate it, soft-delete it, and recover a tenant that lost its administrator
  (resend/revoke its pending TenantAdmin invitation, or invite a replacement).
- **Development seed**: `sysadmin@bookspace.test` (`TenantId = null`, `SysAdmin` only) is now a separate
  account from `admin@acme.test` (`TenantId = Acme`, `TenantAdmin` only) - see `DevelopmentSeeder.cs`.
  **Production bootstrap**: `ProductionBootstrapper` now creates only the first global SysAdmin
  (`Bootstrap__SysAdmin*` config keys) - it no longer creates a tenant at all. See
  [container-deployment.md](container-deployment.md).
- **Migration**: `User.TenantId` became nullable (`MakeUserTenantIdNullable`) - see "Migration" below for
  the exact schema change and the documented (not automatically executed) procedure for any
  pre-existing data that combined `SysAdmin` with a non-null `TenantId`.

## Migration (this task)

`User.TenantId` changed from `Guid` to `Guid?` (`User.cs`), and its foreign key is now explicitly
`.IsRequired(false)` (`BookSpaceModelConfiguration.cs`) - a schema-only change, no data transformation.
The EF Core migration (`MakeUserTenantIdNullable`) only relaxes the column's nullability and the FK; it
does **not** touch any existing row's data.

**Resolving a pre-existing `SysAdmin` + non-null `TenantId` combination** (this project has never been
deployed to production, so no such row exists in any real database today - this procedure is
documentation for the scenario, not something this task executed): do **not** run an automatic
"set `TenantId = NULL` for every `SysAdmin` row" migration - a tenant whose *only* `TenantAdmin` was also
(incorrectly) a `SysAdmin` would silently lose its one administrator with no record of what happened.
Instead:

1. Query for the invalid combination directly: users holding `SysAdmin` with a non-null `TenantId`.
2. For each one, check whether their tenant has another `Active` `TenantAdmin` (`GetTenantUserAggregatesAsync`'s
   `ActiveTenantAdminEmails`, or a direct query). If yes, the transformation is unambiguous: remove the
   `TenantId` (making them a true global SysAdmin) and remove any tenant roles they also held - report
   this to a human operator, don't apply it silently.
3. If their tenant has **no other** active TenantAdmin, do **not** auto-fix. Use
   `InviteReplacementTenantAdminCommandRequest` (or the `/tenants/:id` recovery panel) to invite a proper
   replacement TenantAdmin for that tenant **first**, confirm they accept, and only then separate the
   original user into a tenant-less global SysAdmin.
4. A documented local dev-database reset (drop and re-`dotnet ef database update` from scratch) remains
   the simplest option for a local development database carrying test data from before this task - this
   task did **not** execute this, and never deletes a local database automatically; it is offered here
   only as a documented option for the operator to choose.

## What already existed vs. what this feature adds

This feature deliberately reuses almost everything - no parallel invitation/token/email system was built:

| Reused as-is | New |
| --- | --- |
| `SecureTokenGenerator` (512-bit token + SHA-256 hash) | `CreateTenantCommandHandler` / `ResendTenantAdminInvitationCommandHandler` |
| `Invitation` entity, its filtered unique index, and `IInvitationRepository` | Two narrowly-scoped, `IgnoreQueryFilters()`-based repository methods for cross-tenant SysAdmin lookups (see "Cross-tenant authorization" below) |
| `AuthenticationService.AcceptInvitationAsync` (unmodified) and `POST /api/auth/accept-invitation` | `BookSpace.Application.Tenants.ITenantRepository` (broader than the existing read-only `Bookings.ITenantRepository`) |
| `INotificationOutboxWriter`, the outbox table, `NotificationOutboxProcessor`/`SmtpNotificationSender` | `TenantNotificationFactory` / `Tenant.AdminInvitation` notification type + its email template branch |
| `guestGuard`/`roleGuard`, the Angular auth/routing conventions | `AppOptions.PublicBaseUrl`, `TenantsController`, `TenantFormComponent`, `AcceptInvitationComponent` |
| `TimezoneSelectComponent`, `ResourceFormComponent`'s form conventions | - |

No new entity and no new migration - `has-pending-model-changes` confirms the model is unchanged.

## Endpoints

| Method | Route | Auth | Purpose |
| --- | --- | --- | --- |
| `POST` | `/api/tenants` | JWT, `SysAdmin` only | Creates a tenant and its first TenantAdmin (Invited), queues an invitation email. `201`, body `{ tenantId, tenantName, adminUserId, adminEmail }` - never a token or URL. |
| `GET` | `/api/tenants` | JWT, `SysAdmin` only | Paginated, searchable, status-filterable tenant list with user-count/active-admin aggregates. *(this task)* |
| `GET` | `/api/tenants/{id}` | JWT, `SysAdmin` only | One tenant's platform detail: the same aggregates, plus `DefaultTimeZoneId` and any pending TenantAdmin invitation contact(s). *(this task)* |
| `PUT` | `/api/tenants/{id}` | JWT, `SysAdmin` only | Renames the tenant - the only editable field; `409 Tenant.NameConflict` on a collision. *(this task)* |
| `POST` | `/api/tenants/{id}/suspend` | JWT, `SysAdmin` only | `Active -> Suspended`. `409 Tenant.StatusConflict` if not currently Active. *(this task)* |
| `POST` | `/api/tenants/{id}/reactivate` | JWT, `SysAdmin` only | `Suspended -> Active`. `409 Tenant.StatusConflict` otherwise (including from `Deleted` - not reversible). *(this task)* |
| `POST` | `/api/tenants/{id}/delete` | JWT, `SysAdmin` only | Soft-delete (`-> Deleted`). Body `{ confirmName }` must match the tenant's **current** name exactly (re-verified server-side); `409 Tenant.DeleteConfirmationMismatch` otherwise, `409 Tenant.StatusConflict` if already Deleted. Never a physical delete. *(this task)* |
| `POST` | `/api/tenants/{tenantId}/users/{userId}/resend-invitation` | JWT, `SysAdmin` only | Revokes the admin's current active invitation (if any) and issues a new one. `200`, body `{ tenantId, adminUserId, adminEmail, invitationExpiresAtUtc }`. |
| `DELETE` | `/api/tenants/{tenantId}/users/{userId}/invitation` | JWT, `SysAdmin` only | Revokes a pending TenantAdmin invitation without replacing it. `204`. *(this task)* |
| `POST` | `/api/tenants/{tenantId}/admin-invitations` | JWT, `SysAdmin` only | Platform recovery: invites a brand-new person as an additional/replacement TenantAdmin for an *existing* tenant - same outbox/email path as the first-admin invitation. `201`, body `{ tenantId, adminUserId, adminEmail }`. *(this task)* |
| `POST` | `/api/auth/accept-invitation` | **None** (`[AllowAnonymous]`) | Unmodified, existing endpoint - sets the password, activates the account. Now also rejects if the invitation's tenant is Suspended/Deleted. `204` on success, generic `401` otherwise. |

`TenantsController` is `[Authorize(Roles = "SysAdmin")]` at the class level - unlike every
`[Authorize(Roles = "TenantAdmin")]` controller elsewhere in this codebase, `TenantAdmin` is never also
permitted here, not even for its own tenant: every action on this controller is platform administration,
not a tenant's own business function (see "SysAdmin and tenant business data" above).

## Cross-tenant authorization

A SysAdmin's `ICurrentUserContext.TenantId` is always their **own** tenant, never the one being onboarded,
so the ordinary tenant-filtered repository methods can never see the new tenant's rows. Two new,
narrowly-scoped methods close this gap, both documented at the call site as to why they exist and who may
call them:

- `IUserRepository.FindByIdForTenantAdministrationAsync(tenantId, userId, ct)` - `IgnoreQueryFilters()`,
  then re-applies **both** `TenantId` and `Id` explicitly in the same `Where`.
- `IInvitationRepository.FindActiveByUserIdForTenantAdministrationAsync(tenantId, userId, ct)` - same shape,
  for the active-invitation lookup.

This is the same sanctioned "bypass the ambient filter, then re-apply the real scoping condition yourself"
exception already used by `UserRepository.FindByEmailAsync`/`FindByIdForAuthenticationAsync` (see
`docs/tenant-isolation.md`) - never a blanket `IgnoreQueryFilters()` with no replacement condition.

## Tenant + first admin creation

`CreateTenantCommandHandler` (`BookSpace.Application/Tenants/Create/CreateTenantCommandRequest.cs`):

1. Validates the request (`FluentValidation`): tenant `Name` (required, ≤200 chars), `DefaultTimeZoneId`
   (required, must be a valid IANA zone - reuses `TimeZoneValidation.BeAValidTimeZoneId`, the exact same
   check `CreateResourceCommandRequestValidator` uses), and the admin's first/last name/email.
2. Friendly pre-checks: `Tenant.Name` and the admin's email must not already exist (`409
   Tenant.NameConflict` / `409 User.EmailConflict`) - never silently moves an existing account, changes its
   tenant, or assigns it a new role.
3. Stages (via `AddAsync` only - no intermediate `SaveChangesAsync`): a new `Tenant`, a new `User` (`Status
   = Invited`, `PasswordHash = string.Empty` - the exact same "exists before a password does" convention
   `InviteUserCommandHandler` already established), the `TenantAdmin` role assignment, and a new
   `Invitation` (72-hour expiry, `CreatedByUserId` = the calling SysAdmin).
4. Builds the invitation URL from `AppOptions.PublicBaseUrl` (see below) and calls
   `INotificationOutboxWriter.EnqueueAsync` **last** - the single commit point for all five rows (see
   "Transaction boundary").

Only the fields actually required to start a tenant are collected - every other `Tenant` column
(`ReminderLeadMinutes`, `NoShowGraceMinutes`, `ApprovalExpiryHours`, `CheckInLeadMinutes`) keeps its
existing C# default; nothing stops a later feature from letting the new TenantAdmin tune them.

## Transaction boundary

`NotificationOutboxWriter.EnqueueAsync`/`EnqueueManyAsync` are the **only** methods in this chain that call
`SaveChangesAsync` - every other repository call in `CreateTenantCommandHandler`/
`ResendTenantAdminInvitationCommandHandler` only ever stages (`AddAsync`) on the same scoped `DbContext`.
This was verified by reading every repository method actually called, not assumed from the method names -
`ITenantRepository.AddAsync`, `IUserRepository.AddAsync`/`AddRoleAsync`, `IInvitationRepository.AddAsync`
none of them call `SaveChangesAsync` themselves. The result: Tenant + User + UserRole + Invitation +
NotificationOutboxItem either all land in one SQL transaction, or none of them do - there is no code path
that leaves a partially-created tenant or account. Proved under real concurrency, not just by code reading,
in `TenantOnboardingEndToEndTests` (`BookSpace.Infrastructure.Tests`, real LocalDB) - see "Testing" below.

### Concurrent create

The friendly pre-checks in step 2 above narrow a race between two concurrent requests but cannot close it.
The real backstop is the database's own unique constraints (`Tenant.Name`, `User.Email`), surfacing through
`SaveChangesAsync` as a SQL 2601/2627 error. Translating that into a client-facing `409` required a genuine
architectural fix: `BookSpace.Application` does not (and must not) reference `Microsoft.EntityFrameworkCore`
or `Microsoft.Data.SqlClient`, so the two command handlers cannot catch `DbUpdateException`/`SqlException`
themselves. Instead, `NotificationOutboxWriter` (in `BookSpace.Infrastructure`, which already references
both packages to detect its own duplicate-key idempotency case) now also recognizes any **other** 2601/2627
violation staged on the same `DbContext` and throws `ConflictException("The request conflicts with
existing data.")` directly - mirroring `DbContextConcurrencyExtensions.SaveChangesHandlingConflictsAsync`'s
existing behavior for ordinary repositories, but without widening what gets silently treated as "already
enqueued" (the outbox's own duplicate-key check still matches on the specific index name, confirmed against
the actual migration-generated name `IX_NotificationOutboxItems_TenantId_IdempotencyKey`). The same writer
also now translates a `DbUpdateConcurrencyException` (a RowVersion mismatch on some *other* entity staged on
the same `DbContext`, e.g. a concurrent resend's revoke - see below) into the equivalent conflict. Neither
command handler needs to know any of this; the `ConflictException` simply propagates to
`ConflictExceptionHandler` and becomes an ordinary `409`.

## Invitation email

`TenantNotificationFactory.AdminInvitation` builds the `NotificationOutboxRequest`:

- **Notification type**: `Tenant.AdminInvitation` (`TenantNotificationTypes.AdminInvitation`).
- **Payload** (`TenantAdminInvitationNotificationPayload`): `TenantId`, `TenantName`, `RecipientFirstName`,
  `InvitationUrl`, `ExpiresAtUtc`. Never a password, never anything beyond what the email itself needs to
  say. `NotificationOutboxItem.PayloadJson` has no `HasMaxLength()` (unbounded `nvarchar(max)`), so the full
  URL fits safely.
- **Idempotency key**: `tenant-admin-invitation:{invitation.Id}` - keyed on the **invitation's own id**, not
  the user's or tenant's. This matters specifically for reissue: a resend creates a brand-new `Invitation`
  row with its own fresh id, so its notification gets its own key and is never silently swallowed as
  "already exists" by the outbox's idempotency check. An initial draft of this factory mistakenly keyed on
  `(adminUser.Id, expiry)` instead - caught and corrected before this ever shipped, since it would have made
  a resend's notification collide with the original's key forever.
- **Rendering**: `BookingEmailTemplateRenderer` gained a third, independent render branch (alongside the
  booking-lifecycle switch and `ApprovalExpired`) - subject `"You've been invited to administer {TenantName}
  on BookSpace"`, a plain-text + HTML body naming the tenant and containing the link, every dynamic value
  HTML-encoded. Explicitly never mentions or includes a password - the email's entire call to action is
  "click this link to set your password."

### Reissue and the outbox idempotency key

`ResendTenantAdminInvitationCommandHandler` mirrors `InviteUserCommandHandler`'s existing reissue pattern
exactly: revoke the previous active invitation (if any), insert a new one, enqueue its own notification.
Preconditions: the target user must still be `Invited` (`409 Tenant.AdminInvitationNotPending` otherwise -
this must never become a de facto password-reset mechanism for an already-active administrator; there is no
such mechanism anywhere else in this codebase either, see `docs/user-administration.md`). Old link
invalidation is immediate and real: `AcceptInvitationAsync` rejects any token whose `Invitation` row has
`RevokedAtUtc` set, with the same generic `Invitation.Invalid` outcome as every other failure case.

**If the old invitation's email is still sitting unsent in the outbox at the moment of reissue**: it is left
exactly where it is - this feature does not (and the existing outbox has no built-in mechanism to)
retroactively cancel an already-queued notification. The old email can still be delivered later, containing
a link that will fail with the generic invalid-invitation message if used - a stale, dead link, never a
security problem (the token itself was already revoked), and the exact same pre-existing limitation
`InviteUserCommandHandler`'s own reissue path has always had for ordinary user invitations.

### Concurrent resend

Two genuinely concurrent resend requests for the same user both read the same `previousActiveInvitation`
and both try to revoke it and insert a new active row. Proved in `TenantOnboardingEndToEndTests`
(`TwoConcurrentResendRequestsForTheSameAdmin_ExactlyOneActiveInvitationSurvives`): exactly one wins. The
losing request's failure mode turned out, empirically, to be a `DbUpdateConcurrencyException` (a RowVersion
mismatch on the shared `previousActiveInvitation` row both requests tried to update) far more often than a
unique-index violation on the new row's own insert - an assumption worth calling out explicitly, since an
earlier draft of this handler's own doc comment incorrectly assumed only the unique-index shape was
possible. `NotificationOutboxWriter` now translates both exception shapes into the identical
`ConflictException`, so the handler itself needs no special-casing either way.

## Where the raw token lives, and for how long

Only `Invitation.TokenHash` (a SHA-256 hash) is ever persisted. The raw token exists in exactly one other
place: `NotificationOutboxItem.PayloadJson`, from the moment `EnqueueAsync` is called until that row is
successfully dispatched by `NotificationOutboxProcessor` (see `docs/background-jobs.md`) - after which the
row's `Status` becomes `Sent` but the row itself (and its `PayloadJson`) is **not** deleted, so the raw
token technically remains at rest in that column indefinitely, identical to every other outbox notification
type already in this codebase (booking confirmations, reminders, etc. - this is a pre-existing, accepted
shape, not something this feature introduces or could narrow on its own). The token is never logged
anywhere in this chain, never returned by `POST /api/tenants`'s response body, and never exposed through any
admin list, error message, or log line.

## Public URL configuration (`App:PublicBaseUrl`)

A **new, deliberately separate** setting from the existing `CalendarFeed:PublicBaseUrl`
(`BookSpace.Application/Common/AppOptions.cs`) - not shared, and not always the same value:

- **Packaged/container deployment** (Production, or local Podman/Compose): Angular and the API are served
  from the one and only origin (`docs/architecture.md`) - both settings should be configured identically.
  `compose.local.yaml`: `App__PublicBaseUrl: "http://localhost:8080"`, matching `CalendarFeed__PublicBaseUrl`.
- **Local Development** (`dotnet run` + `ng serve`): genuinely different origins - `ng serve` serves the
  Angular SPA on `http://localhost:4200`, the API runs on a different port. `App:PublicBaseUrl` must point
  at the **frontend** origin (where the browser-rendered `/accept-invitation` page lives), unlike
  `CalendarFeed:PublicBaseUrl`, which points at the API origin (an endpoint an external calendar app polls
  directly, never a browser-rendered page). `appsettings.Development.json`:
  `"App": { "PublicBaseUrl": "http://localhost:4200" }`.
- Validated at startup (`AppOptionsValidator`, `IValidateOptions<AppOptions>`, the same `ValidateOnStart()`
  convention as `CalendarFeedOptions`/`BackgroundJobsOptions`/`EmailOptions`) - fails fast if missing or not
  an absolute `http`/`https` URL.
- Never built from the incoming request's `Host` header - the same Host-header-injection concern
  `CalendarFeedOptions.PublicBaseUrl`'s own doc comment already documents.
- `docs/container-deployment.md`'s Azure environment variable list now also documents
  `App__PublicBaseUrl` (identical to `CalendarFeed__PublicBaseUrl` in that single-origin deployment).

## Accepting the invitation

`AcceptInvitationComponent` (`frontend/src/app/features/auth/accept-invitation/`), route `/accept-invitation`
- public, reachable from an emailed link with no prior session:

- `canActivate: [guestGuard]` (the same guard already protecting `/login`) - an already-authenticated
  browser is redirected to `/` **before** this page ever reads the token, so accepting an invitation can
  never silently swap an existing session's identity or mix tenant context.
- No token in the URL -> an "invalid link" message, no form rendered, no API call attempted.
- Password + confirm-password fields, a form-level cross-field validator for the match, `minlength(8)` for
  immediate UX feedback (the real enforcement is still server-side, in the existing
  `AcceptInvitationCommandRequestValidator`).
- Calls the existing `AuthService.acceptInvitation(token, password)` (new method - thin wrapper over `POST
  /api/auth/accept-invitation`, unlike `login()` it never touches `tokens`/localStorage, matching the
  backend's 204-no-tokens response). On success: a confirmation message and a link to `/login` - **no new
  auth pattern**, no auto-login, the exact same "accept, then log in separately" behavior
  `docs/user-administration.md` already documents for every other invitation.
- Every failure (unknown/expired/revoked/already-accepted token) surfaces the backend's own single, generic
  message ("This invitation link is no longer valid.") verbatim - the page never tries to distinguish the
  cases itself.

### Already logged in

If the browser already holds a valid session when the invitation link is opened, `guestGuard` redirects to
`/` before `AcceptInvitationComponent` ever constructs or reads the `token` query parameter - the component
never runs, the backend is never called, and nothing about the existing session (its tokens, its tenant
context) is touched. This is the same guard, with the same behavior, `/login` already relies on; no new
mechanism was introduced specifically for this page.

## Tenant lifecycle management (this task)

Backend: `GetTenantsQueryRequest`/`GetTenantQueryRequest` (`BookSpace.Application/Tenants/Get/`),
`UpdateTenantCommandRequest`/`SuspendTenantCommandRequest`/`ReactivateTenantCommandRequest`
(`.../Update/`), `DeleteTenantCommandRequest`/`RevokeTenantAdminInvitationCommandRequest`
(`.../Delete/`), `InviteReplacementTenantAdminCommandRequest` (`.../Create/`).

- **Aggregates, never business data**: `IUserRepository.GetTenantUserAggregatesAsync` (a new,
  `IgnoreQueryFilters()`-based batched query - see [tenant-isolation.md](tenant-isolation.md)) returns
  only counts (total/active/invited/inactive), the active TenantAdmin's email(s), and - detail view only
  - any still-`Invited` TenantAdmin's `(UserId, Email)` so a SysAdmin can resend/revoke it. Never a
  booking, a resource, or an ordinary member's identity.
- **List** (`GET /api/tenants`): paginated (`Page`/`PageSize`), `Search` (name substring), `Status`
  filter, ordered by `CreatedAtUtc` descending then `Id` (a stable tiebreaker) so a page's contents don't
  shift under a caller paging through while tenants are created concurrently.
- **Rename** (`PUT /api/tenants/{id}`): `Name` only, by design - status changes go through their own
  dedicated actions below, not a side effect of this call, the same "own explicit lifecycle action"
  convention [resource-lifecycle-and-capacity.md](resource-lifecycle-and-capacity.md) already documents.
- **Suspend/Reactivate**: strict, non-idempotent transitions (`Active -> Suspended`,
  `Suspended -> Active` only - mirroring `ReactivateUserCommandHandler`'s own precedent), not a generic
  "set status to X". `AuthenticationService.IsUsableAsync` (see [authentication.md](authentication.md))
  is what actually enforces a suspended/deleted tenant's users can't log in or refresh - suspending does
  **not** proactively revoke already-issued refresh tokens (same accepted, bounded-window tradeoff this
  codebase already lives with for a role change, see
  [user-administration.md](user-administration.md) §5); the next refresh/login is where it bites.
- **Soft delete** (`POST /api/tenants/{id}/delete`): `TenantStatus` gained a `Deleted` member (`Active`,
  `Suspended`, `Deleted` - the smallest equivalent of a four-state `DeletionPending`/`Deleted` lifecycle;
  a separate staging state was judged unnecessary since the confirm-by-typing-the-exact-current-name
  requirement already is that gate - see `TenantStatus`'s own doc comment). Not reversible through this
  API. The confirmation name is re-verified server-side against the tenant's **current** name (never
  trusted from whatever the frontend dialog captured) - the frontend is not a security boundary.
  - **Name/email release for reuse** (later task): deleting a tenant also releases its `Name` and every
    one of its users' `Email` for a brand-new tenant/account to use, while keeping every historical row
    (`Booking`, `ApprovalRequest`, ...) intact under its original id forever. `Tenant.Name`'s unique index
    is now filtered to `[Status] <> 'Deleted'`; a NEW `UserStatus.Withdrawn` member (distinct from the
    reversible, administrator-triggered `Inactive` - see `UserStatus`'s own doc comment) backs a matching
    filtered unique index on `User.Email` (`[Status] <> 'Withdrawn'`). `DeleteTenantCommandHandler` now
    atomically, in one commit: marks the tenant `Deleted`, withdraws every one of its users
    (`IUserRepository.WithdrawAllForTenantAsync`), revokes every still-active invitation and calendar
    subscription for the tenant, and revokes every refresh token belonging to one of those now-withdrawn
    users. `IUserRepository.FindByEmailAsync` (login, and every "does this email exist" conflict check)
    and `ITenantRepository.ExistsByNameAsync` both exclude Withdrawn/Deleted rows, so a historical and a
    brand-new row can share an email/name without ever being confused - the live one is always what
    resolves. History is never looked up by email/name; only by the original, unchanged ids.
  - **Already-unexpired access tokens** (later task): revoking refresh tokens alone only stops a FUTURE
    refresh - it does nothing about an access token that is still, technically, inside its own
    `AccessTokenMinutes` window. `IDeletedTenantAccessGuard`/`DeletedTenantAccessMiddleware`
    (`BookSpace.Api/Security/`) close that gap specifically for a Withdrawn user or a Deleted tenant by
    checking live state once per authenticated request - deliberately narrower than "any non-Active
    status": a merely-`Suspended` tenant (and an ordinarily-`Inactive` user) keep the existing, accepted
    bounded-access-token-window tradeoff described two paragraphs up; only the two states a deletion
    itself writes are an immediate hard cutoff.
  - **Migration**: `ReleaseTenantNameAndUserEmailOnSoftDelete` backfills any tenant that was ALREADY
    `Deleted` before this feature existed - its users become `Withdrawn` and its still-active
    invitations/subscriptions/refresh tokens are revoked, retroactively, in the same migration, before the
    new filtered indexes start enforcing anything.
  - **Known limitation**: an email already accepted by the outbound SMTP server before a tenant was
    deleted cannot be recalled - see [background-jobs.md](background-jobs.md). A still-`Pending` outbox
    item for a since-withdrawn recipient is never sent at all (`EmailRecipientResolver` excludes Withdrawn
    users, so the send attempt reports "recipient not found" and the item dead-letters, exactly as it
    would for any other vanished recipient).
- **Platform recovery**: `InviteReplacementTenantAdminCommandRequest` is a *separate* handler from both
  `CreateTenantCommandHandler` (which also creates the `Tenant` itself) and `InviteUserCommandHandler`
  (`UsersController`'s own invite, which is `TenantAdmin`-only, scoped to the caller's own tenant, and
  returns the raw token directly instead of emailing it) - it creates a new `Invited` `TenantAdmin` user
  for an *existing* tenant and reuses `TenantNotificationFactory.AdminInvitation` verbatim, so the email
  reads identically whether the recipient is the first or a replacement administrator.
  `RevokeTenantAdminInvitationCommandRequest` is the opposite number of resend: revokes without
  replacing.

## Minimal frontend

- `TenantFormComponent` (`frontend/src/app/features/tenants/tenant-form/`), route `tenants/new` inside the
  authenticated shell, `canActivate: [roleGuard('SysAdmin')]` (never also `TenantAdmin` - matching the
  backend's own authorization). A "Tenants" and "New tenant" link appear in the sidebar only for
  `SysAdmin` (`shell.html`) - every tenant-business link (Resources/My bookings/Calendar/Approvals/
  Users/Resource types) is hidden from it instead (this task).
- Form: tenant name, time zone (`TimezoneSelectComponent`, reused as-is - its backing endpoint,
  `GET /resources/supported-timezones`, already serves the exact IANA list `TimeZoneValidation` accepts),
  and the first admin's first/last name/email. Client-side required-field checks before any HTTP call;
  server-side field errors and the two named conflicts (`Tenant.NameConflict`/`User.EmailConflict`) render
  friendly inline messages, mirroring `ResourceFormComponent`'s own error-handling convention.
- On success: a confirmation panel, not a navigation away - this stays a one-shot "create, then show what
  happened" form even though a tenant list/detail view exists now (this task): the panel remains the
  quickest way to resend the just-created admin's invitation without a detour through the list. It states
  plainly that the tenant/account were created and an invitation was **queued for sending** - it never
  claims the email was actually delivered just because the outbox write succeeded.
- `TenantListComponent`/`TenantDetailComponent` (`frontend/src/app/features/tenants/tenant-list/`,
  `.../tenant-detail/`, this task), routes `/tenants` and `/tenants/:id`, both `roleGuard('SysAdmin')`.
  List: search/status filters and pagination in the URL's own query params (the same convention
  `UserListComponent` established, so "Back to tenants" restores the exact filtered view). Detail:
  aggregates, inline rename, suspend/reactivate buttons, a soft-delete dialog requiring the exact current
  name typed before the confirm button enables, and the platform-recovery panel (resend/revoke a pending
  invitation, invite a replacement administrator) - never a booking/resource/ordinary-member view. The
  delete dialog's summary text (later task) states plainly that history is kept, every user loses access
  immediately, and the tenant's name/its users' emails are released for reuse - never that anything was
  physically deleted.

## Testing

- **`BookSpace.Application.Tests/Tenants/`**: `CreateTenantCommandHandlerTests` (success shape, role
  assignment, invitation URL/token-hash correctness, both named pre-check conflicts, missing-role
  `NotFoundException`, a race-lost `ConflictException` propagating unmodified),
  `CreateTenantCommandRequestValidatorTests`, `ResendTenantAdminInvitationCommandHandlerTests` (success,
  no-previous-invitation edge case, never creates a duplicate user/tenant, tenant/user-not-found, non-Invited
  conflict, concurrent-loss propagation). `Notifications/TenantNotificationFactoryTests` (idempotency-key
  shape, two different invitations never collide, payload never carries a password).
  `Email/BookingEmailTemplateRendererTests` gained a `TenantAdminInvitation` section (subject, link/expiry
  content, HTML-escaping, determinism, malformed-payload failure).
- **`BookSpace.Infrastructure.Tests/Persistence/`**: `TenantOnboardingEndToEndTests` (real LocalDB) - the
  full chain through the real handlers, real repositories, the real `NotificationOutboxProcessor`/
  `SmtpNotificationSender` chain (fake-only SMTP transport, the same harness
  `BookingConfirmationEndToEndDispatchTests` already established), real `AuthenticationService` for both
  accept and login: create -> cannot log in yet -> outbox row -> dispatch -> extract the real link from the
  real rendered email -> accept -> same token rejected a second time -> login succeeds -> tenant isolation
  (`GET`-equivalent query scoped to the new tenant sees only its own data). Also: resend invalidates the old
  token and the new one works; two concurrent creates with the same tenant name/same admin email leave
  exactly one winner and **no partial rows** for the loser; two concurrent resends leave exactly one active
  invitation. `NotificationOutboxWriterTests` gained a test proving a *different* entity's unique-constraint
  violation (not the outbox's own index) is translated into `ConflictException`, not silently misreported.
- **`BookSpace.Api.Tests/Tenants/`**: `TenantsEndpointsTests` - full HTTP: SysAdmin success (never exposes a
  token/URL in the response body), every other role and anonymous both rejected, duplicate tenant
  name/email `409`, missing-fields `400`, the full create -> accept -> login -> tenant-isolation chain over
  real HTTP (the invitation link is read directly from the queued outbox payload via the test host's own DI
  container, there being no running background worker or real inbox in this test environment), resend
  invalidates the old token, resend against an already-accepted admin `409`, resend authorization.
- Frontend: `tenant.service.spec.ts`, `tenant-form.spec.ts` (empty form, client-side blocking, success
  confirmation wording - including an explicit assertion it never claims the email was *sent* - conflict/
  field-error rendering, resend success/failure), `accept-invitation.spec.ts` (no-token state, client-side
  password-match/length validation, in-flight/disabled state, success confirmation with a login link, the
  backend's generic failure message, no-double-submit), and a new `AuthService.acceptInvitation()` case in
  `auth.service.spec.ts` (posts the right body, never touches session state).

### A real bug found and fixed by this test suite

`TenantFormComponent`'s `@Component` declaration omitted `FormsModule` from its `imports` array. Angular's
template compiler did not reject the resulting `(ngSubmit)="submit()"` binding as an error (it has no
directive named `ngSubmit` to attach without `FormsModule`'s `NgForm`, so the binding was silently treated
as a dead native-event listener for an event that never fires) - the Create Tenant button would have done
**nothing** at all in the real app. Caught by `tenant-form.spec.ts`'s very first submission test, root-caused
(not worked around), and fixed by adding the missing import - the same real-bug-via-test-failure shape as
WP-8's own leaked-`setTimeout` finding in `CalendarSubscriptionPanelComponent`.

### Results (this task)

- `BookSpace.Application.Tests`: 749/749 passed (+40 new).
- `BookSpace.Infrastructure.Tests`: 339/339 passed (real LocalDB; +6 new).
- `BookSpace.Api.Tests`: 313/313 passed (+13 new).
- Backend `dotnet build`/`dotnet publish` (Release): succeeded, 0 warnings, 0 errors.
- Frontend `ng test` (full suite): 531/531 passed, 45/45 files - run twice to specifically watch for the
  prior audit's known intermittent worker-crash flake; both runs were clean.
- Frontend `ng build` (production): succeeded (same pre-existing, unrelated `dashboard.scss` budget warning
  as every prior task).

## What email-disabled or failed sending looks like

Unchanged from the existing outbox/email design (`docs/background-jobs.md`): if `Email:Enabled` is `false`,
`NotificationOutboxProcessor` still marks the item `Sent` without actually attempting delivery (the existing
"don't block the pipeline on email being configured" behavior) - a tenant can still be created and its admin
can still, in principle, obtain the invitation URL through some other out-of-band means (there is no
in-product way to view it once queued - see "Where the raw token lives" above - so in practice a deployment
with email disabled has no way to deliver this specific invitation at all, same as it would for any other
notification type). If sending is enabled but transiently fails (SMTP error), the existing retry/backoff
behavior applies identically to this notification type - nothing about tenant onboarding special-cases it.

## Manual verification performed vs. not performed

Performed: every item in "Testing" above, against a real SQL Server (LocalDB) and a real in-process HTTP
pipeline, including the full create -> dispatch -> accept -> login -> tenant-isolation chain with a real
(fake-transport) SMTP send.

**Not performed** (left for a real deployment, same disclosure shape as `docs/calendar-subscription-feed.md`'s
own "Manual end-to-end verification" section): sending a real email through a real Gmail SMTP account to a
real inbox, confirming the link in that actual received email opens the actual deployed frontend, and
completing the flow through a browser by hand end to end. This task never sent email to a real address and
never modified the user's own development database, per the governing instructions.

## Starting/ending branch and inherited changes

Started on `feature/tenant-admin-onboarding`, branched from `dev` after confirming the WP-8 final audit
(PR #70) was already merged and `dev` was up to date with `origin/dev`. No other in-progress work existed on
`dev` at that point. Everything in this document was produced on this branch; nothing has been committed,
pushed, or opened as a PR, per the governing instructions - work is left entirely uncommitted for review.
`wp8notify/` was never touched (confirmed via `git status` both before and after this task).

## Remaining limitations

- The stale-outbox-email-after-reissue behavior (see "Reissue and the outbox idempotency key" above) is
  disclosed, not fixed - fixing it would mean adding a cancel/expire mechanism to the shared outbox that
  `InviteUserCommandHandler`'s own reissue path doesn't have either, which is out of this task's minimal-
  change scope.
- A deployment with `Email:Enabled=false` has no in-product way to retrieve a queued invitation's link - see
  "What email-disabled or failed sending looks like" above.
- A tenant list/detail page now exists (this task) - "Resend invitation" (and revoke, and inviting a
  replacement administrator) are reachable from `/tenants/:id` at any time afterward, not only from the
  just-created confirmation panel.
- No real email was sent to a real inbox in this task (see "Manual verification performed vs. not
  performed").
- **Not implemented, by explicit instruction**: support impersonation, a global view of all tenants'
  bookings/resources, global editing of all ordinary users' roles, physical (hard) deletion of a tenant's
  data, billing/subscription plans. See the governing instructions' own "not part of this task" list.
- **Suspending** a tenant does not proactively revoke its users' already-issued refresh tokens or access
  tokens - see "Tenant lifecycle management" above. An already-valid access token remains usable for up to
  `AccessTokenMinutes` (15) after the action; this is an accepted, documented tradeoff, unchanged by the
  later soft-delete work below.
- **Soft-deleting** a tenant (later task) is stricter: it DOES revoke every refresh token belonging to one
  of its (now-withdrawn) users, AND an already-unexpired access token is separately, immediately rejected
  by `DeletedTenantAccessMiddleware` on the very next API call - see "Tenant lifecycle management" above.
  The one gap that remains: an SMTP send already accepted by the mail server before deletion cannot be
  recalled (see "Tenant lifecycle management" above, "Known limitation").
- No composite database-level constraint prevents a future bug from re-introducing `SysAdmin` + non-null
  `TenantId` on a `User` row - this is enforced in application code only (the role-assignment validators,
  the seed/bootstrap code paths), not a `CHECK` constraint. See "Migration" above for why an automatic
  full-table scan on every request was rejected as the alternative.
