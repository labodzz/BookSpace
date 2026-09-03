We are at the FINAL CODE/TEST AUDIT of WP-3 (Resources & Availability API) for BookSpace.

Act as a senior .NET backend engineer doing a release-readiness review.

IMPORTANT:
- This is a single-branch audit, not a multi-PR audit. All of WP-3 lives on one feature branch
  (`feature/wp3-resources-availability`), built as 7 sequential commits, not yet merged to `dev`/`master`:
    1f09c50 WP-3: resource CRUD, NotFound/Conflict error handling
    f9ef4ed WP-3: availability rule management, race-condition-safe conflict handling
    b202dec WP-3: blackout period management, stable ErrorCode on business exceptions
    f311e7d WP-3: per-command response DTOs, Request-suffixed command/query types
    758b11b WP-3: group Resources CQRS files by CRUD verb (Create/Update/Delete/Get)
    29e8b2a WP-3: resource approver assignment
    7cb5353 WP-3: capacity-aware resource availability query
    3384806 WP-3: ResourcesController wiring all Resources commands/queries to HTTP
    6fe06ef WP-3: Resources & Availability API integration tests, finalize work package
  Audit all of them together plus the current working tree - do not treat any of them as separately reviewable PRs.
- Controller naming / route grouping / OpenAPI cosmetics are explicitly OUT OF SCOPE.
- Do not commit, push, create a PR, rename anything, or do cosmetic refactors.
- There is no docs/wp3/*.md set and no backend/CLAUDE.md in this repo. Do not invent or look for them.
- Do not print/list/echo any User Secrets or passwords. In particular do not run `dotnet user-secrets list`,
  and do not print the DevelopmentSeeder's seed password even though it is a hardcoded dev-only constant.
- Do not reset/reseed the development LocalDB database unless a test absolutely requires it; integration
  tests must use their own isolated setup (see Test Landscape below), not the dev DB.
- Preserve all current uncommitted work. Never reset/checkout/restore user changes.

SOURCE OF TRUTH / PRIORITY

There is no separate requirements doc set checked into this repo for WP-3. Use this order:

1. The original external WP-3 task description (Resources & Availability API), if you have access to it
   from the session/user - acceptance criteria below are a good-faith summary of it.
2. Current actual repository + current working tree (this is the primary source of truth - the code is
   more authoritative than any summary below).
3. `AI-USAGE.md`'s WP-3 entry (dated 2026-09-02) - a first-person log of what was actually built, what
   was corrected mid-session, and what bugs were found and fixed. Treat it as a reliable account of
   intent, not as a spec.
4. `.claude/skills/add-cqrs-feature/SKILL.md`, `write-handler-tests/SKILL.md`, `tenant-isolation-review/SKILL.md`
   - these encode the actual conventions this codebase settled on (verb-based folders, `Request`-suffixed
   command/query types, one independent response record per command/query, tenant isolation patterns).

IMPORTANT source conflict rule:
- There is no known superseded design to avoid resurrecting - WP-3 was built once, iteratively, on one
  branch. If you find dead code or an abandoned approach, treat it as a genuine defect/cleanup candidate,
  not evidence of a documented pivot.
- Do not invent stricter or richer business rules than what the code and AI-USAGE.md actually describe.
  In particular: there is no NodaTime/IANA-TZDB library, no per-category approver-eligibility invariant,
  no full-schedule-replacement endpoint, and no discretized 15-minute slot grid anywhere in this feature.
  Do not treat their absence as a defect unless you can point to an actual acceptance criterion that
  requires them.

ACTUAL WP-3 SCOPE TO PROVE

WP-3 provides:
- TenantAdmin Resource CRUD/lifecycle: `ResourceTypeId` (FK to a flat, non-tenant-scoped `ResourceType`
  lookup table with no CRUD/lifecycle of its own), `Name`, `Description`, `Capacity` (int > 0),
  `RequiresApproval` (bool), `TimeZoneId` (validated IANA/Windows id via `TimeZoneInfo.FindSystemTimeZoneById`),
  `Status` (`Active`/`Inactive`/`Maintenance`/`Archived`). Delete is soft: it sets `Status = Archived` and
  is idempotent; there is no hard delete anywhere in the API.
- `AvailabilityRule` management per Resource: one row per `(ResourceId, DayOfWeek, StartTime, EndTime)`
  with `TimeOnly` start/end **within a single calendar day only** - there is no overnight/cross-midnight
  window concept, no "EndsNextDay" flag, and no week-wrap handling anywhere in the schema or algorithm.
  Mutation is one rule at a time: `POST .../availability-rules` (create) and
  `DELETE .../availability-rules/{ruleId}` (delete). There is no PUT/full-replacement endpoint for the
  schedule - if you find references to one, that is a hallucination to ignore, not a gap to fill.
- `BlackoutPeriod` management per Resource: absolute `StartUtc`/`EndUtc` (`DateTimeOffset`) + `Reason`.
  Full CRUD (Create/Get list/Update/Delete) is allowed unconditionally, regardless of whether the period
  is in the past, present, or future - **there is no lifecycle state (Future/Active/Historical) tracked
  anywhere**, and no restriction like "Active blackout can only be shortened." Overlapping blackouts on
  the same resource are deliberately allowed (see the code comment in `CreateBlackoutPeriodCommandRequest.cs`)
  because the availability algorithm merges/handles overlaps naturally - do not treat overlap-allowed as a defect.
- `RequiresApproval` + `ResourceApprover` (a plain `(ResourceId, UserId)` join, unique per pair):
  `POST/GET/DELETE .../approvers`. There is **no invariant enforced** tying `RequiresApproval=true` to
  having at least one active approver - `RemoveResourceApproverCommandHandler` will happily remove the
  last approver from a resource that still requires approval. Determine whether this is an intentional
  simplification (acceptable for this WP) or a real gap worth flagging - do not assume either way without
  checking whether AI-USAGE.md or the task description actually requires the invariant.
- Live availability query for a Resource/date range (`GET .../availability?from=&to=`, plain `yyyy-MM-dd`
  `DateOnly` params, range capped at **under 92 days** via `GetResourceAvailabilityQueryRequestValidator`)
  returning a single `GetResourceAvailabilityResponse` object (not an array) with `OpenPeriods`,
  `Blackouts`, `BusyPeriods`, `BookableSlots`. The algorithm (`IntervalMath.ComputeAvailableCapacity`,
  `BookSpace.Application/Resources/IntervalMath.cs`) is a sweep-line that splits each open window at every
  occupancy boundary and reports remaining capacity per sub-interval - it does **not** discretize output
  into a fixed slot grid (no 15-minute quantization of `BookableSlots`), and there is **no `attendeeCount`/
  requested-quantity request parameter at all** - the endpoint only reports remaining capacity, it does
  not validate a caller's desired quantity against it.
- Clean DTOs (one independent response record per command/query, `Request`-suffixed request types),
  structured `ProblemDetails` error contracts (`NotFoundException`/`ConflictException` with an optional
  `ErrorCode`, correlation ID in both body and header), and pagination (`PagedResult<T>` on
  `GetResourcesQueryRequest` only - there is no paginated endpoint for anything else in this feature).

Acceptance (as actually implemented, verify each is really true):
- A TenantAdmin can publish/manage a Resource with availability rules and blackout periods.
- The availability query excludes blackout periods entirely (full capacity loss for their duration) and
  reduces (not necessarily zeroes) capacity for overlapping bookings.
- Non-admin (Member) users cannot create/edit admin-managed resource state (`[Authorize(Roles =
  "TenantAdmin,SysAdmin")]` on every write action in `ResourcesController`).
- Expected API failures are structured `ProblemDetails` with a correlation ID.
- Tenant isolation: a cross-tenant entity id behaves as 404 (via the reflective `HasQueryFilter` on every
  `ITenantOwned` entity making the row invisible, not via an explicit ownership check), never 403 and
  never a distinguishable "exists but not yours" response.

BOOKING BOUNDARY

WP-3 does NOT implement Booking lifecycle/API. This holds in the actual code:
- `IBookingAvailabilityRepository`/`BookingAvailabilityRepository` are explicitly read-only and
  deliberately not named `IBookingRepository` (see the comment on the interface) - full Booking CRUD is
  future work.
- Test/dev fixtures create `Booking` rows directly (`BookSpace.Api.Tests/TestDataSeeder.cs`,
  `BookSpace.Infrastructure/Persistence/DevelopmentSeeder.cs`), never through an API.

Required semantics - IMPORTANT, this differs from a naive assumption:
- **Both `Pending` and `Confirmed` bookings block availability and reduce capacity.** This is a deliberate
  decision made this session (`BookingAvailabilityRepository.GetActiveBookingsAsync`'s
  `ActiveStatuses = [BookingStatus.Pending, BookingStatus.Confirmed]`), reasoned as: you should not be able
  to double-book a slot while someone else's approval is pending. Do not flag Pending-blocks-availability
  as a defect - verify instead that `Cancelled`/`Rejected`/`Completed`/`NoShow` bookings correctly do NOT
  block (only Pending/Confirmed should).
- Both Pending and Confirmed bookings appear only as anonymous `BusyPeriodResponse(StartUtc, EndUtc,
  Quantity)` entries - verify no `BookingId`/`UserId`/owner/attendee/status/cancellation metadata ever
  leaks into the availability response.
- Do not add CreateBooking/CancelBooking/booking controller/approval-queue/recurrence/notification behavior.

==================================================
PHASE A - REPOSITORY REALITY + DIFF INVENTORY
==================================================

Before judging anything:

1. Show:
   - current branch (expect `feature/wp3-resources-availability`);
   - `git status`;
   - changed/untracked files;
   - the 9 WP-3 commits listed above (`git log --oneline` against `dev`/`master`, whichever this branch
     was cut from), and confirm nothing else has landed on this branch outside WP-3 scope.
2. Audit the CURRENT WORKING TREE, not only committed HEAD.
3. Build a complete WP-3 implementation inventory under these actual paths:
   - `backend/BookSpace.Api/Controllers/ResourcesController.cs` (controller + all inline request DTOs);
   - `backend/BookSpace.Application/Resources/{Create,Update,Delete,Get}/*.cs` (commands/queries/
     validators/handlers - note the verb-folder convention: every entity's Create/Update/Delete/Get
     lives together by verb, not grouped by entity);
   - `backend/BookSpace.Application/Resources/{IResourceRepository.cs, IAvailabilityRuleRepository.cs,
     IBlackoutPeriodRepository.cs, IResourceApproverRepository.cs, IBookingAvailabilityRepository.cs,
     IntervalMath.cs}` (repository interfaces + the pure sweep-line calculator, all at the feature root);
   - `backend/BookSpace.Infrastructure/Persistence/{ResourceRepository.cs, AvailabilityRuleRepository.cs,
     BlackoutPeriodRepository.cs, ResourceApproverRepository.cs, BookingAvailabilityRepository.cs}`
     (EF implementations);
   - `backend/BookSpace.Application/Common/{NotFoundException.cs, ConflictException.cs, ErrorCodes.cs}`;
   - `backend/BookSpace.Infrastructure/Persistence/BookSpaceModelConfiguration.cs` (EF config/indexes/
     check constraints for Resource/AvailabilityRule/BlackoutPeriod/ResourceApprover/Booking);
   - EF migrations under `backend/BookSpace.Infrastructure/Migrations/` relevant to these tables (they
     predate WP-3 - WP-3 added no new migration, since the schema was already fully built in WP-1;
     confirm this is actually true and no migration was silently skipped);
   - tenant filter: `BookSpaceDbContext.OnModelCreating`'s reflective `HasQueryFilter` over `ITenantOwned`;
   - concurrency/locking infrastructure: **there is none beyond SQL Server unique-index-backed conflict
     detection** (`BookSpaceDbContext.SaveChangesHandlingConflictsAsync`, catching SQL error 2601/2627 and
     mapping to `ConflictException`). There is no `ITransactionalCommand`, no explicit `UPDLOCK`/`HOLDLOCK`,
     no deterministic multi-row lock ordering anywhere in this feature. Confirm this is really the extent
     of it (don't assume something more exists) and flag any concurrency-sensitive path that ISN'T backed
     by a unique index as a genuine unguarded race.
   - `backend/BookSpace.Infrastructure/Persistence/DevelopmentSeeder.cs` (dev-only seed);
   - test projects: `backend/BookSpace.Application.Tests/Resources/**` (xUnit + Moq, handler/validator
     unit tests, mocked repos - no real DB), `backend/BookSpace.Application.Tests/Resources/IntervalMathTests.cs`
     (pure calculator unit tests), `backend/BookSpace.Api.Tests/Resources/ResourcesEndpointsTests.cs`
     (real HTTP integration tests) + `backend/BookSpace.Api.Tests/TestDataSeeder.cs` (its fixtures).

Do not modify code in Phase A.

TEST LANDSCAPE - IMPORTANT, this differs from a naive "integration tests use SQL Server" assumption:
- `BookSpace.Application.Tests` (121 tests as of the last full run): pure unit tests, `Mock<T>` repos, no
  database at all.
- `BookSpace.Infrastructure.Tests` (10 tests): check what provider these actually run against before
  assuming SQL Server.
- `BookSpace.Api.Tests` (47 tests, including `ResourcesEndpointsTests.cs`'s 17): real HTTP against a real
  `BookSpaceDbContext`, but wired to **SQLite in-memory** via `CustomWebApplicationFactory`
  (`options.UseSqlite(_connection)`), NOT SQL Server. This matters a lot for this audit: earlier this
  session, exactly two EF Core query-translation bugs were found and fixed
  (`BlackoutPeriodRepository.GetByResourceIdAsync`'s `ORDER BY` on a `DateTimeOffset` column, and
  `BookingAvailabilityRepository.GetActiveBookingsAsync`'s `<`/`>` comparisons on `DateTimeOffset`) that
  are SQLite-provider-specific limitations invisible to SQL Server. Any "proof by integration test"
  claim in this audit that hinges on real database persistence/constraint/translation behavior (CHECK
  constraints, DATETIMEOFFSET comparisons/ordering, unique index race behavior) should be verified against
  **real SQL Server** (LocalDB is configured in `appsettings.Development.json`,
  `Server=(localdb)\MSSQLLocalDB;Database=BookSpace`), not just the SQLite-backed API test suite, and
  should also re-scan `BookingAvailabilityRepository.cs`/`BlackoutPeriodRepository.cs`/every other
  repository for any OTHER un-translatable LINQ pattern that hasn't yet been exercised by a real HTTP test.

==================================================
PHASE B - WORK-PACKAGE PROOF MATRIX
==================================================

Create a proof matrix:

Requirement | Implementation | Tests | Status | Evidence

Status must be exactly:
PASS
GAP
DEFECT
SOURCE-CONFLICT
NOT-APPLICABLE

Every PASS needs actual file/test evidence. Do not infer PASS from AI-USAGE.md's prose.

Cover at least:

1. `ResourceType` FK validation (exists-check only - there is no ResourceType CRUD/lifecycle to audit;
   mark any "ResourceType lifecycle" line item NOT-APPLICABLE with a one-line reason, don't skip it silently).
2. Pagination (`GetResourcesQueryRequest` only).
3. Resource tenant-facing/admin queries (`GetResourceQueryRequest`, `GetResourcesQueryRequest` with
   `ResourceTypeId`/`Status` filters).
4. Approver assignment/removal/list (`AssignResourceApproverCommandRequest`,
   `RemoveResourceApproverCommandRequest`, `GetResourceApproversQueryRequest`) - there is no
   "candidates" query (listing eligible-but-unassigned users) anywhere; mark NOT-APPLICABLE if the
   original task description didn't require one, DEFECT/GAP if it did.
5. Resource CRUD/lifecycle (Create/Update/Delete-as-archive/Get).
6. `RequiresApproval` field behavior (plain field, no invariant enforcement - see Booking Boundary section).
7. `AvailabilityRule` create + delete (one rule at a time, no replace-all endpoint).
8. Resource capacity field and its role in the availability algorithm (there are no capacity-based
   *query* filters on `GetResourcesQueryRequest` - only `ResourceTypeId`/`Status`; verify this matches
   the actual task requirements before calling it a GAP).
9. `BlackoutPeriod` create/list/update/delete.
10. Blackout lifecycle - NOT-APPLICABLE, no lifecycle states exist; verify no code silently references
    one that was never wired up.
11. Timezone contract: plain `TimeZoneInfo`/`DateTimeOffset`/`TimeOnly` (no NodaTime). DST handling is a
    **documented, accepted v1 limitation** (see the code comment on
    `GetResourceAvailabilityQueryHandler.ConvertLocalToUtc`): each window's start/end is converted to UTC
    independently, which is wrong only if a window straddles a DST transition instant. Verify this is
    still an accepted limitation (not silently worse than documented) rather than treating DST handling
    itself as a gap.
12. Live availability (capacity-aware sweep-line, see Phase C for algorithm depth).
13. Authorization (`[Authorize(Roles = "TenantAdmin,SysAdmin")]` on all writes, plain `[Authorize]` on all reads).
14. Tenant isolation.
15. Structured `ProblemDetails` (404/409/400 via `NotFoundExceptionHandler`/`ConflictExceptionHandler`/
    `ValidationExceptionHandler`, generic 500 via `GlobalExceptionHandler`).
16. Concurrency: unique-index-backed conflict detection only (see Phase A) - explicitly verify no other
    concurrency claim is being silently assumed.
17. Database uniqueness/indexes/constraints (see Phase G for the full list).
18. Development seed (see Phase H).
19. Booking-scope boundary/privacy (see Booking Boundary section above).

==================================================
PHASE C - SENIOR BUSINESS-INVARIANT REVIEW
==================================================

Read the actual handlers and verify business decisions are safe under concurrency, not merely that
happy-path tests pass. There is no PR1/PR2/PR3/PR4 split - group your findings by entity instead.

Resource:

- active-only name uniqueness: **verify whether the unique index is actually filtered to Active-only or
  is a plain global unique index.** As of the last full review, `BookSpaceModelConfiguration.cs` defines
  `entity.HasIndex(resource => new { resource.TenantId, resource.Name }).IsUnique();` with **no
  `.HasFilter(...)` clause** - meaning an Archived resource's name is still considered taken, forever
  (confirmed empirically this session: creating a resource, archiving it via `DELETE`, then trying to
  create a new resource with the same name fails with 409). Determine whether this is the intended
  design (archived names stay reserved, preventing accidental confusion) or an unintended gap versus
  what the original task expected, and classify accordingly - do not silently assume either.
- app-level pre-check (`ExistsByNameAsync`) + DB unique index backstop via
  `SaveChangesHandlingConflictsAsync` both exist and agree - verify the mapping from SQL error 2601/2627
  actually reaches `ConflictException` for this specific table (not just tested for some other table).
- archived Resource CAN still be updated via `UpdateResourceCommandRequest` (the validator only forbids
  setting `Status` TO `Archived` via update, it does not forbid updating an already-Archived resource).
  Determine whether that is intended.
- `DeleteResourceCommandRequest` (archive) is idempotent - re-archiving an already-Archived resource is a
  no-op success, not a conflict/error. Verify a test actually proves this, not just that the code looks
  idempotent.
- capacity is a plain `int > 0` (`CK_Resources_Capacity` check constraint) with no relationship enforced
  to existing bookings when it's *decreased* below currently-booked quantity for a future date - verify
  whether the original task expected such a guard; if not, mark NOT-APPLICABLE rather than inventing a
  requirement.
- `RequiresApproval` can be flipped to `false` even while `Pending` bookings with an outstanding
  `ApprovalRequest` exist for that resource - there is no guard. Same determination as above: check
  against the actual task description before classifying.
- tenant isolation: a cross-tenant `ResourceTypeId` in `CreateResourceCommandRequest` is checked via
  `ResourceTypeExistsAsync` - but `ResourceType` has **no `TenantId` column at all** (it's a shared,
  non-tenant-scoped lookup table, unique on `Name` globally across all tenants). Confirm this is
  intentional (resource types are meant to be shared taxonomy, e.g. "Meeting Room"/"Desk", not
  per-tenant) and not a tenant-isolation leak - there's nothing sensitive on a `ResourceType` row (just
  `Id`/`Name`), so sharing it is likely fine, but verify no other WP-3 code path assumes it's tenant-scoped.

AvailabilityRule:

- exact-duplicate rule `(ResourceId, DayOfWeek, StartTime, EndTime)` is rejected via a unique index +
  app-level `ExistsAsync` pre-check + `ConflictException` (`ErrorCodes.AvailabilityRuleConflict` - verify
  this constant is actually attached at the throw site, not just defined and unused).
- **overlapping-but-not-identical** rules for the same resource/day (e.g. 08:00-14:00 and 12:00-18:00) are
  deliberately allowed - the code comment says the availability handler's own `IntervalMath.Merge` call
  unions them before subtracting occupancies. Verify a test actually exercises this merge (not just that
  create doesn't reject it).
- `DeleteAvailabilityRuleCommandRequest` treats a rule that exists but belongs to a *different* resource
  as 404, same as a rule that doesn't exist at all - verify this is actually tested (a real security-
  relevant check: don't let a mismatched URL pair succeed).
- rules cannot span midnight (`TimeOnly StartTime`/`EndTime` within one calendar day, `EndTime > StartTime`
  enforced by both validator and `CK_AvailabilityRules_TimeRange`) - confirm there is genuinely no way to
  express a resource that's open, say, 22:00-02:00, and confirm this matches what was actually required
  (if the original task expected overnight availability, this is a real GAP, not a stylistic choice).
- race: two concurrent `POST .../availability-rules` for the identical `(ResourceId, DayOfWeek,
  StartTime, EndTime)` - verify the unique index backstop actually catches this, ideally with a real
  SQL Server concurrency test, not just sequential unit tests.

BlackoutPeriod:

- absolute `DateTimeOffset` `StartUtc`/`EndUtc`, `EndUtc > StartUtc` enforced by validator +
  `CK_BlackoutPeriods_TimeRange` - verify both actually agree (see the 24-hour-window-style proof
  methodology below, applied here: does a boundary case the validator accepts also persist correctly on
  real SQL Server, not just SQLite?).
- overlapping/adjacent blackouts on the same resource are deliberately allowed, unconditionally, at both
  Create and Update - verify there is at least one test proving this is allowed (not just that it isn't
  explicitly rejected, which could mean it's simply untested).
- `UpdateBlackoutPeriodCommandRequest`/`DeleteBlackoutPeriodCommandRequest` both apply the same
  "wrong-resource-id in the URL = 404, not silently operate on a different resource's row" pattern as
  AvailabilityRule's delete - verify it's tested here too, not just for AvailabilityRule.
- consider: is there any guard preventing a blackout from being retroactively edited/deleted for a period
  that has *already been queried and shown as busy to a caller*? (There is no lifecycle state, so
  presumably not - confirm this is acceptable given availability is explicitly a snapshot, not a
  reservation guarantee, per the response-shape discussion in AI-USAGE.md.)

ResourceApprover:

- unique `(ResourceId, UserId)` - app pre-check (`FindByResourceAndUserAsync`) + DB unique index backstop,
  same double-guard pattern as the others - verify.
- `AssignResourceApproverCommandRequest` validates the target `UserId` via `IUserRepository.FindByIdAsync`,
  which is itself tenant-filtered - so a cross-tenant `UserId` is invisible and correctly produces 404
  `User.NotFound`, "doubling as a tenant check for free" per the handler's own code comment. Verify this
  claim is actually true by testing it, not just trusting the comment.
- `RemoveResourceApproverCommandRequest` has **no guard at all** against removing the last approver from a
  resource where `RequiresApproval=true` - confirmed above in the Booking Boundary/scope section.
  Classify per the original task's actual requirements, not by assumption.
- no "candidate users" query exists (listing tenant users eligible to become approvers, excluding
  already-assigned ones) - classify NOT-APPLICABLE unless the original task specifically required it.

Live availability (`GetResourceAvailabilityQueryRequest`):

Validate the actual algorithm in `GetResourceAvailabilityQueryHandler`/`IntervalMath.cs`, not just the
DTO shape.

Contract:
- authenticated tenant-facing read (`[Authorize]`, no role restriction - Member/Approver/TenantAdmin/any
  mix all succeed); anonymous request -> 401 (verify: does this endpoint return an empty framework body
  like every other bare-401 in this app, or does something override that? See Phase D/E for the general
  401/403-body pattern already established this session).
- wrong resource id / cross-tenant resource id / archived resource id -> what actually happens? The
  handler only does `FindByIdAsync` (tenant-filtered, no status filter) - so a cross-tenant id is 404, but
  an Archived resource in the SAME tenant is NOT rejected and will happily compute (possibly nonsensical)
  availability for it. Determine if this is acceptable or a real gap.

Request validation (`GetResourceAvailabilityQueryRequestValidator`):
- empty `ResourceId` -> 400.
- `ToDate < FromDate` -> 400.
- range of **exactly 91 days is allowed, 92+ is rejected** (`ToDate.DayNumber - FromDate.DayNumber < 92`) -
  verify the exact boundary with a real test at 91 vs 92, not an approximate one. There is NO
  `attendeeCount`/quantity parameter to validate on this endpoint at all - do not look for one.

Algorithm - verify each of these against `IntervalMath.cs` and `GetResourceAvailabilityQueryHandler.cs`:
- per-day: only weekdays with a matching `AvailabilityRule.DayOfWeek` contribute an `OpenPeriod` for that
  date - no rule for that weekday means zero availability that day, not a default/fallback window.
- same-day overlapping rules are merged (`IntervalMath.Merge`) before capacity computation.
- blackout occupancy consumes the resource's FULL `Capacity` for its duration regardless of the
  configured capacity value (a blackout means the resource is fully down, not merely busy) - verify a
  test actually asserts zero remaining capacity during a blackout, and that the blackout window is
  correctly EXCLUDED from `BookableSlots` entirely (not shown with 0 capacity).
- booking occupancy consumes only `Booking.Quantity`, stacking additively when multiple overlapping
  bookings exist - verify the "overlapping occupancies exceeding capacity -> that sub-interval dropped"
  case (`IntervalMathTests.ComputeAvailableCapacity_WithOverlappingOccupanciesExceedingCapacity_ExcludesOverbookedSlot`
  already covers this at the unit level - verify it's ALSO proven through a real HTTP path, not just the
  pure calculator).
- sub-intervals with zero or negative remaining capacity are dropped from `BookableSlots`, not returned
  as a zero-capacity entry.
- no discretization into a fixed slot grid anywhere - `BookableSlots` entries are variable-length,
  bounded only by occupancy boundaries. Do not look for 15-minute quantization; it does not exist.
- deterministic sorting of `OpenPeriods`/`BookableSlots` by start time - verify.
- a fully-booked/fully-blacked-out range still returns HTTP 200 with empty `BookableSlots`, not an error.

Response/privacy:
- `OpenPeriods`/`Blackouts`/`BusyPeriods`/`BookableSlots` - verify `Blackouts` includes `Reason` (this is
  intentional, an admin-facing detail, not a privacy leak, since the whole endpoint is admin/approver/
  member-facing within the SAME tenant) but `BusyPeriods` never includes `BookingId`/`UserId`/any booking
  metadata beyond `StartUtc`/`EndUtc`/`Quantity`.

Read-only architecture:
- no write anywhere in this query path (`GetResourceAvailabilityQueryHandler` only reads via
  `FindByIdAsync`/`GetByResourceIdAsync`/`GetActiveBookingsAsync`) - verify no `SaveChangesAsync` is ever
  reachable from this handler, directly or transitively.
- verify whether repository reads here use `AsNoTracking()` - if not, determine whether that's a real
  (minor) performance gap worth flagging, not a correctness defect.
- confirm no `IgnoreQueryFilters()` anywhere in this path (would bypass tenant isolation).
- availability is explicitly a snapshot, not a reservation guarantee, given there is no locking
  infrastructure at all in this feature (see Phase A) - this is a known, accepted, already-documented
  limitation (see the saved note about the future Booking-create work needing `UPDLOCK`/`HOLDLOCK` or
  `SERIALIZABLE` isolation) - do not re-flag it as a new discovery, just confirm the documented plan for
  it still makes sense given the actual current read path.

Check the `Booking(TenantId, ResourceId, StartUtc, EndUtc)` index (comment: "Primary lookup shape for
availability/overlap checks") is actually the one `BookingAvailabilityRepository.GetActiveBookingsAsync`
benefits from, given that repository currently filters `ResourceId`+`Status` in SQL and does the
`StartUtc`/`EndUtc` range-overlap filtering **client-side, in memory, after materializing** (a deliberate
fix this session for the SQLite `DateTimeOffset` comparison translation gap - see Phase A). Determine
whether this client-side filtering approach, now that it exists, is still index-efficient against real
SQL Server (which CAN translate the range predicate) or whether it's now needlessly over-fetching rows
that SQL Server could have filtered server-side - this is a legitimate performance question raised by the
SQLite-compatibility fix itself, worth a real answer, not just "it works."

==================================================
PHASE D - SECURITY / TENANT / AUTH REVIEW
==================================================

Audit every WP-3 route, handler, and DB access pattern in `ResourcesController.cs` and everything it calls.

Verify:
- `TenantId` comes from `ICurrentUserContext.TenantId!.Value` on every Create handler, never from client
  input - grep for every `TenantId =` assignment in `backend/BookSpace.Application/Resources/**` and
  confirm none of them come from the request DTO.
- no handler accepts a client-supplied `TenantId` for authorization decisions.
- no `IgnoreQueryFilters()` anywhere in the Resources feature's repositories.
- `Resource`, `AvailabilityRule`, `BlackoutPeriod`, `ResourceApprover` all implement `ITenantOwned` and
  are covered by the reflective global query filter in `BookSpaceDbContext.OnModelCreating` - confirm this
  by reading the reflection code, not by assuming it applies uniformly.
- a resource-id/rule-id/blackout-id from another tenant behaves as 404 everywhere (Get/Update/Delete on
  every sub-resource), never a distinguishable 403 or a leak of the row's existence.
- a rule-id or blackout-id that exists but belongs to a DIFFERENT resource under the SAME tenant also
  correctly 404s (not just cross-tenant - this is the "wrong URL pairing" check noted in Phase C).
- admin writes are `TenantAdmin`/`SysAdmin` only (`[Authorize(Roles = "TenantAdmin,SysAdmin")]` on every
  POST/PUT/DELETE action in `ResourcesController`) - verify every single write action actually has this
  attribute, not just a representative sample (grep for every `[HttpPost]`/`[HttpPut]`/`[HttpDelete]` in
  the controller and confirm each has the role restriction immediately above it).
- Member-only and Approver-only tokens get 403 on a representative admin write (there's an existing test
  for this - verify it, and check whether a user who is BOTH Member and Approver but NOT TenantAdmin also
  correctly gets 403, since that combination isn't obviously covered).
- reads are open to any authenticated role (`[Authorize]` with no `Roles` restriction) - verify Member,
  Approver, and TenantAdmin tokens can all successfully GET resources/rules/blackouts/approvers/availability.
- structured validation/business errors never expose stack traces or raw SQL - verify by reading
  `ValidationExceptionHandler`/`NotFoundExceptionHandler`/`ConflictExceptionHandler`/`GlobalExceptionHandler`
  and confirming `Detail` is only ever set from a message the application authored itself (never
  `exception.ToString()` or a raw driver exception message) for any exception type reachable from this feature.

Do a manual equivalent code review now. Do NOT claim `/tenant-isolation-review` was run automatically -
if you want its checklist applied, say so explicitly and note it as a manual step. At the end, tell the
user whether they should manually run `/tenant-isolation-review` again over the full WP-3 diff.

==================================================
PHASE E - ERROR CONTRACT / VALIDATION REVIEW
==================================================

Inventory every WP-3 `ErrorCode` from `backend/BookSpace.Application/Common/ErrorCodes.cs`:
`Resource.NotFound`, `Resource.ResourceTypeNotFound`, `Resource.NameConflict`,
`AvailabilityRule.NotFound`, `AvailabilityRule.Conflict`, `BlackoutPeriod.NotFound`, `User.NotFound`,
`ResourceApprover.NotFound`, `ResourceApprover.Conflict`.

Verify, for EACH constant, that it is actually attached at its throw site(s), not merely defined and
unused. Specifically check (do not assume - this needs a real grep):
- Does `CreateResourceCommandHandler`'s duplicate-name `ConflictException` actually pass
  `ErrorCodes.ResourceNameConflict`, or was the constant added but the throw site never updated?
- Same question for `UpdateResourceCommandHandler`'s duplicate-name conflict, and for every
  `NotFoundException`/`ConflictException` thrown anywhere under `backend/BookSpace.Application/Resources/`
  - list every throw site and whether it carries an `ErrorCode` or not, and flag the inconsistency (some
  early Resource/AvailabilityRule throws predate the `ErrorCode` convention and may have been left
  without one even after later throws started using it) as a real, concrete Phase E finding rather than
  a vague "check error codes" note.

Verify:
- expected HTTP status per exception type (404/409/400/500) via `NotFoundExceptionHandler`/
  `ConflictExceptionHandler`/`ValidationExceptionHandler`/`GlobalExceptionHandler` registration order in
  `Program.cs` (`AddExceptionHandler` chain).
- correlation ID present in both the `ProblemDetails.Extensions["correlationId"]` and the
  `X-Correlation-Id` response header for every one of these handlers, including on the framework-level
  bare 401/403 responses that have NO body at all (verify: does `CorrelationIdMiddleware` still set the
  header on those, even though there's no JSON body to attach `correlationId` to as an extension?).
- at least one integration/API test proves the mapping for every distinct business error reachable from
  this feature (`ResourcesEndpointsTests.cs` covers RBAC 403s, duplicate-name 409, unknown-resource-type
  404, missing-name 400 - verify AvailabilityRule/BlackoutPeriod/ResourceApprover's own 404/409 paths are
  ALSO proven at the HTTP level, not only in `BookSpace.Application.Tests`'s mocked handler tests).

Check for:
- any expected business exception (`NotFoundException`/`ConflictException`) that could accidentally fall
  through to `GlobalExceptionHandler` as a 500 - specifically check exception handler registration ORDER
  in `Program.cs` (`ValidationExceptionHandler` -> `NotFoundExceptionHandler` -> `ConflictExceptionHandler`
  -> `GlobalExceptionHandler`, first-match-wins) is still correct after all of WP-3's additions.
- DB constraint/unique violations that could leak as 500 instead of mapping to `ConflictException` -
  specifically verify `SaveChangesHandlingConflictsAsync`'s SQL error code list (2601/2627) actually
  covers every unique index WP-3 introduced reliance on (Resource name, AvailabilityRule exact-duplicate,
  ResourceApprover pair) - are there any OTHER SQL Server constraint violation codes (e.g., CHECK
  constraint violations, which are a DIFFERENT error number than a unique-index violation) that a WP-3
  write path could hit and that would currently fall through as an unmapped 500? (Capacity <= 0,
  EndTime <= StartTime, EndUtc <= StartUtc are all also enforced by CHECK constraints AND by
  FluentValidation - confirm the FluentValidation layer genuinely makes the CHECK constraint unreachable
  in practice, or find a route/case where it doesn't and the raw constraint violation would 500.)
- FluentValidation rules duplicating what a handler independently re-checks (or vice versa) unnecessarily.
- `Guid.Empty` route values on every `{id:guid}`/`{ruleId:guid}`/`{blackoutId:guid}`/`{userId:guid}` route
  parameter - what actually happens (404 via a failed lookup, or something else)?
- trim/whitespace/max-length boundaries on `Name` (200 chars), `Description` (2000 chars),
  `Reason` (1000 chars) - are these actually enforced by both the validator AND the DB column's
  `HasMaxLength`, and is there a real test at exactly the boundary (200/201 chars), not just "too long"?

Do NOT modify exception-handling middleware unless an actual framework-wide defect is proven.

==================================================
PHASE F - TEST COVERAGE GAP ANALYSIS
==================================================

Build a matrix of important business branches vs existing tests across all three test projects. Do not
judge coverage by test count alone (there are 178 tests as of the last full run - 121 unit + 10
infrastructure + 47 API - but count alone proves nothing).

For each missing scenario classify:
A. MUST ADD - required invariant/security/concurrency/acceptance.
B. NICE TO HAVE - useful but not release-blocking.
C. DUPLICATE - already indirectly/adequately proven elsewhere.

For MUST ADD scenarios, you MAY add focused tests automatically IF they only encode an already-decided
business rule and require no production design decision. Follow the existing conventions:
`.claude/skills/write-handler-tests/SKILL.md` for unit tests (`Mock<T>` + `CreateSut()` pattern,
`Handle_With{Scenario}_{Outcome}` naming), and match `ResourcesEndpointsTests.cs`'s existing style for
integration tests (`IClassFixture<CustomWebApplicationFactory>`, `TestDataSeeder` fixtures - never the
dev seed).

Tests must:
- be deterministic;
- arrange their own data (never depend on `DevelopmentSeeder`, which is dev-only and gated on an empty DB);
- assert DB state when relevant;
- assert status + `errorCode` for expected API errors;
- avoid sleeps.

If a newly-added test FAILS because production behavior is wrong: STOP production changes for that
defect. Report the failing test and classify the issue. Do NOT silently modify production code to make it green.

Explicitly check whether tests exist for these high-risk, project-specific cases:

- Resource name uniqueness race under real concurrent SQL Server writes (not just sequential unit tests
  with mocks) - likely a GAP.
- AvailabilityRule exact-duplicate uniqueness under real concurrent writes - likely a GAP.
- ResourceApprover duplicate-assignment race - likely a GAP.
- A positive test proving overlapping-but-not-identical AvailabilityRules ARE allowed and correctly
  merged by the availability algorithm (not just that duplicates are rejected).
- A positive test proving overlapping BlackoutPeriods ARE allowed (not just that CRUD works on one at a time).
- Combined-role-without-TenantAdmin (e.g., Member + Approver) still gets 403 on an admin write.
- Every distinct `NotFoundException`/`ConflictException` throw site's `ErrorCode` presence/absence proven
  by an actual assertion, per the Phase E inventory.
- The exact 91-vs-92-day availability range boundary.
- `Cancelled`/`Rejected`/`Completed`/`NoShow` bookings correctly do NOT block availability (only Pending/
  Confirmed do) - proven at the real HTTP level, not just via a mocked unit test.
- Overlapping occupancies (blackout + booking, or two bookings) that together exceed capacity correctly
  drop that sub-interval from `BookableSlots`, proven at the real HTTP level (currently only proven in
  `IntervalMathTests`, a pure-calculator unit test).
- Archiving a Resource and then attempting to reuse its exact name for a new Resource - proven either way
  (currently this DOES 409, per the empirical finding in Phase C - is there an actual test asserting this,
  or was it only discovered manually this session?).
- A wrong-resource-id-in-URL 404 for AvailabilityRule delete AND for BlackoutPeriod update/delete (not
  just one of them).
- Cross-tenant 404 for every sub-resource route (rules/blackouts/approvers/availability), not just the
  top-level Resource routes.
- All four role combinations (Member-only, Approver-only, TenantAdmin-only, SysAdmin) against a
  representative read AND a representative write.

==================================================
PHASE G - DATABASE / EF / PERFORMANCE SANITY
==================================================

Inspect `BookSpaceModelConfiguration.cs` and confirm (read-only, no new migration expected - WP-3 added
no schema changes, the tables were all created in WP-1):

- `Resource`: `HasIndex(TenantId, Name).IsUnique()` - **confirm this has no `.HasFilter(...)` clause**
  (i.e., it is NOT scoped to Active-only) and cross-reference against Phase C's finding on archived-name
  reservation.
- `AvailabilityRule`: `HasIndex(ResourceId, DayOfWeek, StartTime, EndTime).IsUnique()`, plus
  `CK_AvailabilityRules_TimeRange = "[EndTime] > [StartTime]"`.
- `BlackoutPeriod`: non-unique `HasIndex(ResourceId, StartUtc, EndUtc)` (query-shape index, not a
  uniqueness constraint - overlaps are intentionally allowed), plus
  `CK_BlackoutPeriods_TimeRange = "[EndUtc] > [StartUtc]"`.
- `ResourceApprover`: `HasIndex(ResourceId, UserId).IsUnique()`.
- `Resource`: `CK_Resources_Capacity = "[Capacity] > 0"`.
- `Booking`: `HasIndex(TenantId, ResourceId, StartUtc, EndUtc)` (non-unique, the "primary lookup shape for
  availability/overlap checks" per its own code comment) - confirm this index is genuinely still useful
  given `BookingAvailabilityRepository` now filters `StartUtc`/`EndUtc` client-side (see Phase C's
  performance question) rather than in the SQL `WHERE` clause.
- app pre-check and DB unique index agree on comparison semantics as much as SQL Server's default
  collation allows (e.g., is Resource name uniqueness case-sensitive or case-insensitive, and does the
  app-level `ExistsByNameAsync` pre-check agree with whatever the collation actually does?).
- no dangerous cascade deletes: confirm `AvailabilityRule`/`BlackoutPeriod`/`ResourceApprover` cascade
  from `Resource` (acceptable, since Resource is soft-deleted anyway - a hard delete of a Resource would
  be unusual, but if it ever happened, cascading its rules/blackouts/approvers is reasonable) while
  `Booking -> Resource` is `Restrict` (a Resource with booking history cannot be hard-deleted at all,
  which is exactly why Delete is soft/archive-only - confirm this FK constraint is the actual reason
  documented in code, not a coincidence).
- no obvious N+1 in `GetResourceAvailabilityQueryHandler` (it issues one query each for the resource, the
  rules, the blackouts, and the active bookings - four total, not per-day or per-rule) or in
  `GetResourcesQueryRequest`'s paged list.
- `GetResourceAvailabilityQueryHandler`'s UTC range predicates against `BlackoutPeriod`/`AvailabilityRule`
  - are they filtered in SQL or fetched-then-filtered client-side? (`AvailabilityRuleRepository`/
  `BlackoutPeriodRepository`'s `GetByResourceIdAsync` fetch ALL rows for the resource with no date-range
  filter at all, then the handler filters in memory - confirm this is acceptable given a resource
  realistically has few rules/blackouts, not a scalability concern worth blocking release over, but worth
  a NON-BLOCKING note if the row counts could plausibly grow large.)

Pay special attention to whatever the actual DB behavior is for the boundary cases FluentValidation
allows at exactly its limit (`EndTime` exactly equal to `StartTime` plus the smallest representable
`TimeOnly` tick, `Capacity` exactly 1, name exactly 200 chars, `Reason` exactly 1000 chars, availability
range exactly 91 days) - prove each against real SQL Server (LocalDB), not just against the SQLite test
double or a validator unit test, given the SQLite-vs-SQL-Server divergences already found this session.

==================================================
PHASE H - DEVELOPMENT SEED AUDIT
==================================================

Read-only audit of `backend/BookSpace.Infrastructure/Persistence/DevelopmentSeeder.cs`.

Verify:
- it only runs when `dbContext.Tenants.AnyAsync()` is false (idempotent-by-emptiness, confirmed at the
  top of `SeedAsync`) - and only in the Development environment (check `Program.cs` for the actual gate).
- the seed password is a **hardcoded constant** (`private const string SeedPassword = "Passw0rd!";`) used
  via the real `IPasswordHasher` (non-generic interface - it's `IPasswordHasher`, not
  `IPasswordHasher<UserEntity>`) to produce a real hash, not a precomputed/fallback hash. Determine
  whether a hardcoded dev-only constant (never used against a real environment, gated behind an empty-DB
  + Development-only check) is an acceptable pattern here, or whether it should instead come from
  configuration/User Secrets - do not assume either answer, and do not print the constant's value in your
  report even though it's already visible in the source file.
- `.test` email domains (`admin@acme.test`, `approver@acme.test`, `member@acme.test`, `admin@globex.test`,
  `member@globex.test`) - confirm none of these could collide with a real domain.
- exactly two tenants (Acme, Globex), with Acme on `Europe/Sarajevo` and Globex on `UTC` - confirm this
  matches what's actually seeded, not what's assumed.
- a `SysAdmin` `Role` row is created but **never assigned to any seeded user** - confirm this is true
  (grep the `UserRoles.AddRange(...)` call and check whether any user gets the `sysAdminRole.Id`) and
  flag it as a real observation: there is no way to log in as SysAdmin against the dev seed today. There
  is no `SysAdmin.TenantId == null` invariant to verify here since no SysAdmin user exists at all in
  this seed - do not invent one.
- every `RequiresApproval=true` resource (Conference Room A) has at least one seeded `ResourceApprover`
  row (`approver@acme.test`) - confirm this is actually true in the seed data, not just plausible.
- the seeded `Confirmed`/`Pending` bookings have consistent `TenantId`/`ResourceId`/`UserId` relationships
  (no cross-tenant mismatch).
- there is no `SystemWriteContext`-style bypass mechanism anywhere in this codebase - confirm this by
  grepping for it; if it genuinely doesn't exist, mark this whole checklist item NOT-APPLICABLE rather
  than treating its absence as a gap.
- confirm `BookSpace.Api.Tests`' own `TestDataSeeder.cs` (a SEPARATE, smaller fixture set, NOT the same
  as `DevelopmentSeeder`) is what integration tests actually use, and that no integration test
  accidentally depends on `DevelopmentSeeder`'s richer dataset instead.

DO NOT display the seed password in your report.

==================================================
PHASE I - BUILD + TEST EXECUTION
==================================================

After read-only review and any missing TEST-ONLY additions:

1. `dotnet build` from `backend/` - summarize warnings/errors.
2. `dotnet test BookSpace.Application.Tests` - report exact pass/fail counts (baseline: 121 passed).
3. `dotnet test BookSpace.Infrastructure.Tests` - report exact counts (baseline: 10 passed).
4. `dotnet test BookSpace.Api.Tests` - report exact counts (baseline: 47 passed, including the 17 in
   `ResourcesEndpointsTests.cs`).
5. Re-run any newly-added tests independently to confirm they're not flaky on their own.

There is no known concurrency test suite yet for WP-3 (see Phase F - real concurrent-write races are
currently a gap, not an existing suite to re-run) and no previously-observed SQL Server teardown deadlock
flakiness has been documented for this project's test infrastructure - if you encounter an intermittent
failure, do not assume it's a known flake; capture the exact failure, rerun the specific test class
independently, and only call it infrastructure flakiness if the focused rerun passes cleanly and no
deterministic production assertion failed. Otherwise treat it as a real, new finding.

==================================================
PHASE J - FINAL REPORT
==================================================

Do NOT give a vague "looks good". Return a senior audit report in this exact structure:

1. FINAL VERDICT
   - READY
   - READY WITH NON-BLOCKING CLEANUP
   - NOT READY

2. BLOCKING DEFECTS
For each:
- severity: Critical / High / Medium
- violated requirement/invariant
- exact file + line/area
- reproduction/test
- why it matters
- minimal fix direction
- DO NOT implement the production fix yet

3. MISSING REQUIRED TESTS
- what was missing
- test added? yes/no
- result
- production defect exposed? yes/no

4. SOURCE-OF-TRUTH CONFLICTS
List anything where AI-USAGE.md's account, a skill file, or the actual code disagree with each other or
with the original task description. There is no old-vs-new PR history to reconcile - conflicts here mean
"the code doesn't match what AI-USAGE.md/a skill file says it does," not "an old design was superseded."

5. WORK-PACKAGE PROOF MATRIX (from Phase B)

6. BUSINESS INVARIANT AUDIT (from Phase C) - group by entity (Resource / AvailabilityRule /
   BlackoutPeriod / ResourceApprover / Live Availability), not by PR number.

7. SECURITY / TENANT / AUTH AUDIT (from Phase D)

8. ERROR CONTRACT AUDIT (from Phase E) - including the full `ErrorCode`-attached-vs-missing inventory.

9. DATABASE / TRANSACTION / LOCKING / CONCURRENCY AUDIT (from Phases A + G) - be explicit that this
   feature's ONLY concurrency guard is unique-index-backed conflict detection, and list every write path
   that either has or lacks that guard.

10. TEST EXECUTION RESULTS - exact build/unit/integration results (baseline to compare against: 121 + 10
    + 47 = 178 passing before this audit's additions) and any flake classification.

11. NON-BLOCKING CODE QUALITY OBSERVATIONS - only meaningful issues, no style nitpicking.

12. EXPLICIT OUT-OF-SCOPE REMINDERS
- controller naming/route grouping/OpenAPI polish intentionally deferred;
- Postman collection / AI-USAGE.md further polish can happen after production correctness is approved;
- Booking lifecycle/API must remain out of scope;
- the already-documented future Booking-create locking design (`UPDLOCK`/`HOLDLOCK` or `SERIALIZABLE`,
  re-summing inside the transaction) is a KNOWN follow-up, not something to design or implement here.

13. MANUAL FOLLOW-UP
State whether `/tenant-isolation-review` should be manually run once more after any production fixes.

IMPORTANT FINAL BEHAVIOR:
- Be adversarial. Try to break the implementation.
- Evidence over assumptions - if you can't point to a file/line/test, don't claim PASS.
- Green tests do NOT automatically mean correct business behavior - the SQLite-vs-SQL-Server divergences
  found this session are a direct proof that green tests can hide real gaps.
- Do not downgrade a real business/security/concurrency defect because tests currently pass.
- Do not invent requirements not supported by the actual task description, AI-USAGE.md, or the code
  itself - in particular, do not import assumptions from a different project (NodaTime, category
  lifecycle, full-schedule-replacement PUT, 15-minute slot grids, `attendeeCount`, Pending-doesn't-block
  - none of these exist here).
- Do not modify production code during this audit.
- Test-only additions are allowed as described in Phase F.
- Do not commit/push.
