# Architecture and Current System State

A map of what actually exists in BookSpace today, so the next Work Packet can be evaluated against
reality rather than against an assumed design.

## Layers and dependency direction

```
BookSpace.Api             presentation - controllers, middleware, composition root (Program.cs)
BookSpace.Application      use cases - mediator, commands/queries & handlers, pipeline behaviors
BookSpace.Domain           entities, enums, tenancy contracts - no framework dependencies
BookSpace.Infrastructure   EF Core DbContext, migrations, repository implementations
```

Dependencies point inward: `Api -> Application -> Domain`, `Infrastructure -> Application`/`Domain`.
`Domain` references nothing else in the solution. `Application` defines repository *interfaces*;
`Infrastructure` implements them. Controllers never touch `Infrastructure` or `DbContext` directly -
every action is a thin `mediator.Send(...)` call.

## Request flow

`Api/Controllers/*Controller.cs` (thin, `[Authorize]`-gated) -> `IMediator.Send(request)` ->
`DefaultMediator` resolves the matching `IRequestHandler<TRequest,TResponse>` via DI, running it
through registered `IPipelineBehavior`s first. Today's pipeline is
`LoggingBehavior` -> `ValidationBehavior` (runs every registered `IValidator<TRequest>`, throwing
`FluentValidation.ValidationException` on failure - the handler never runs for an invalid request) ->
the handler itself. Handlers call repository interfaces; repositories query `BookSpaceDbContext`
(EF Core, SQL Server) and never leak EF types back into `Application`.

Every command/query lives as one file named after itself, containing its record type, its validator
(if any), and its handler - see `.claude/skills/add-cqrs-feature/SKILL.md` for the exact convention
(files grouped by CRUD verb, `Request`-suffixed types, one independent response record per request,
no shared per-entity response DTO).

## Authentication flow

See [authentication.md](authentication.md) for the full token/rotation/reuse model. In one line: login
issues an access token (short-lived JWT) + refresh token (long-lived, single-use, family-tracked)
pair; refresh rotates both; reuse of an already-consumed refresh token revokes the whole family.

## Tenant context flow

See [tenant-isolation.md](tenant-isolation.md) for the full model. In one line: `tenant_id` rides in
the access token JWT claim, is read per-request into `ICurrentUserContext.TenantId`, and a global EF
Core query filter uses it to scope every `ITenantOwned` entity - failing closed (seeing nothing, not
everything) when no tenant context is present.

## Persistence and query filtering

`BookSpaceDbContext.OnModelCreating` does two structural things reflectively, for every entity
implementing `ITenantOwned`: applies the tenant query filter (above), and (as of this remediation pass)
conditionally configures `RowVersion` optimistic-concurrency columns only when the active provider is
SQL Server - see [optimistic-concurrency.md](optimistic-concurrency.md). EF Core migrations live under
`BookSpace.Infrastructure/Migrations/`; the integration test suite (`BookSpace.Api.Tests`) runs against
SQLite in-memory for speed, which cannot translate every LINQ pattern SQL Server can (documented
per-case where it matters, e.g. `BookingAvailabilityRepository`'s provider-conditional query in
[availability-and-timezones.md](availability-and-timezones.md)) - a handful of tests specifically
require real SQL Server (LocalDB) and are called out as such in their own file-level comments.

## Mediator, validation, and error handling

- **Mediator**: hand-rolled (`BookSpace.Application/Mediator/`), not MediatR. `IRequest<TResponse>`,
  `IRequestHandler<TRequest,TResponse>`, `IPipelineBehavior<TRequest,TResponse>`, `DefaultMediator`.
  Handlers/validators are auto-registered via assembly scan - only repository implementations need
  explicit DI registration (`Infrastructure/DependencyInjection.cs`).
- **Validation**: FluentValidation, run as a pipeline behavior before the handler. Validators check
  format/presence only (required fields, length limits, numeric ranges, enum membership) - they never
  touch the database. Business rules (does this row exist, does this conflict with that row) live in
  the handler and throw `NotFoundException`/`ConflictException`.
- **Error handling**: `IExceptionHandler`s registered in `Program.cs`, tried in order, first match
  wins - `ValidationExceptionHandler` (400, field errors) -> `NotFoundExceptionHandler` (404) ->
  `ConflictExceptionHandler` (409) -> `GlobalExceptionHandler` (500, generic). Every response is a
  `ProblemDetails` body carrying a correlation id, which is also always present in the
  `X-Correlation-Id` response header (even on framework-level bodyless 401/403 responses).

## Status of major implemented areas

**Implemented and verified** (build clean, full test suite passing - see
[audit-remediation-summary.md](audit-remediation-summary.md) for exact counts):

- Auth: credential login, JWT access tokens, refresh token rotation with reuse detection and
  optimistic-concurrency-safe rotation, RBAC (`TenantAdmin`/`SysAdmin`/`Member`/`Approver` roles).
- Tenant isolation: fail-closed global query filter, structural (not per-query) enforcement.
- Resources: full CRUD, soft-delete/Archived lifecycle, `ResourceType` (tenant-owned taxonomy),
  `AvailabilityRule` (Create/Delete only, no Update), `BlackoutPeriod` (full CRUD, backdating-guarded),
  `ResourceApprover` assignment, optimistic concurrency on `Resource`/`BlackoutPeriod`.
- Live availability query: capacity-aware, DST-correct, SQL-Server-bounded range filtering.
- Bookings: single-user creation (availability/blackout/capacity-checked, `[Start,End)` semantics reused
  from `IntervalMath`), member self-service view/cancel, and - the hard part - concurrency-safe capacity
  enforcement under real racing requests via a transaction-scoped `Resource`-row lock, proven against
  real SQL Server/LocalDB. See [bookings-and-concurrency.md](bookings-and-concurrency.md) for the full
  writeup.
- Recurring bookings: `RecurringSeries` (Daily/Weekly/Monthly, DST-safe per-occurrence generation) with
  every occurrence materialized as a real, independently viewable/cancellable `Booking`; conflicts
  surfaced explicitly per occurrence at creation, never silently dropped.
- Approval workflow: `Resource.RequiresApproval` now produces `Pending` bookings decided via approve/
  reject by a `ResourceApprover` or `TenantAdmin`/`SysAdmin`, with a fresh availability re-check at
  decision time under the same resource lock booking creation uses. See
  [recurring-bookings-and-approvals.md](recurring-bookings-and-approvals.md) for the full writeup,
  including a real EF Core identity-map bug this work surfaced and fixed.

**Intentionally deferred** (see [open-questions.md](open-questions.md) for what each depends on):
database-level composite tenant foreign keys, absolute refresh-token session lifetime, logout/session
revocation, password-change session revocation, dynamic role-change invalidation, Resource
reactivation-from-Archived as an explicit action, JWT signing-key git-history rewrite, TenantAdmin
cancellation of another user's booking, automatic `ApprovalRequest` expiry enforcement, booking
idempotency keys.

**Not yet implemented at all** - do not assume any of this exists: rate limiting, notification/reminder
delivery, any scheduled/background job infrastructure.
