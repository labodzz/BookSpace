# WP-3 (Resources & Availability API) — Release-Readiness Audit Report

Audited: branch `feature/wp3-resources-availability` (9 commits, cut from `dev` at `49b3cba`), current working tree, against real SQL Server LocalDB where relevant. No production code was modified. Two test files were extended with 15 new, passing, non-flaky tests (12 API integration + 1 unit boundary + 2 correlation-ID tests, detailed below).

---

## 1. FINAL VERDICT

**READY WITH NON-BLOCKING CLEANUP**

No Critical or High-severity correctness/security defects were found after an adversarial line-by-line review of every handler, repository, and exception mapping in the feature, cross-checked against real SQL Server (LocalDB) behavior and 15 newly-added tests (all passing, re-run twice, no flakiness). Tenant isolation, authorization, error-contract mapping, and the availability algorithm's boundary behavior all held up under direct testing, not just code inspection. A handful of genuine but non-blocking issues remain (a performance regression in booking over-fetch, a dev-seed inconsistency, some untested role/boundary combinations) — none of them contradict a stated WP-3 acceptance criterion or corrupt data.

---

## 2. BLOCKING DEFECTS

**None found at Critical or High severity.**

One Medium-severity item is worth tracking (not release-blocking for the current data volume, but should not be forgotten):

| Severity | Issue | File | Why it matters | Minimal fix direction |
|---|---|---|---|---|
| Medium | `BookingAvailabilityRepository.GetActiveBookingsAsync` fetches **every** Pending/Confirmed booking ever created for a resource (only `ResourceId`+`Status` filtered in SQL), then filters the `StartUtc`/`EndUtc` range client-side in memory | `backend/BookSpace.Infrastructure/Persistence/BookingAvailabilityRepository.cs:18-28` | This was a deliberate SQLite-compatibility fix this session, but empirically confirmed against real SQL Server (LocalDB) that `<`/`>` on `datetimeoffset` translates and executes fine there — so on the real deployment target this is pure regression: a resource with years of booking history will load its **entire** booking set into memory on every single availability query, not just the queried date range. The `Booking(TenantId, ResourceId, StartUtc, EndUtc)` index's `StartUtc`/`EndUtc` columns are no longer doing any filtering work. | Gate the range predicate so it still runs in SQL on SQL Server (e.g. an `IQueryable` extension that appends the range `Where` conditionally, or two repository implementations selected by provider), or at minimum add `Status` to a covering index and monitor row counts. **Not implemented** — this is a performance question, not a correctness bug, and the current output is correct. |

Deliberately not implemented — per the audit's constraints, no production code changes were in scope, and this needs a design decision (provider-conditional query vs. index change) that shouldn't be made silently.

---

## 3. MISSING REQUIRED TESTS

### Added this session (15 new tests, all passing, re-run independently with no flakiness, zero production defects exposed)

| Test | File | Result |
|---|---|---|
| `Validate_WithRangeOfExactly91Days_HasNoErrors` | `GetResourceAvailabilityQueryRequestValidatorTests.cs` | Pass — locks in the exact 91-vs-92-day boundary |
| `GetResource_ForAnotherTenantsResource_ReturnsNotFound` | `ResourcesEndpointsTests.cs` | Pass |
| `GetAvailabilityRules_ForAnotherTenantsResource_ReturnsNotFound` | same | Pass |
| `GetBlackoutPeriods_ForAnotherTenantsResource_ReturnsNotFound` (+ `errorCode`) | same | Pass |
| `GetResourceApprovers_ForAnotherTenantsResource_ReturnsNotFound` (+ `errorCode`) | same | Pass |
| `GetAvailability_ForAnotherTenantsResource_ReturnsNotFound` (+ `errorCode`) | same | Pass |
| `CreateResource_WithNameOfArchivedResource_ReturnsConflict` | same | Pass — first automated proof of "archived names stay reserved forever" |
| `CreateAvailabilityRule_WithExactDuplicate_ReturnsConflict` | same | Pass — first HTTP-level proof |
| `CreateAvailabilityRule_WithOverlappingButDifferentRule_BothSucceed` | same | Pass — first proof Create allows overlap |
| `CreateBlackoutPeriod_Overlapping_BothSucceed` | same | Pass — first proof overlapping blackouts are accepted |
| `DeleteAvailabilityRule_ForWrongResourceId_ReturnsNotFound` | same | Pass |
| `UpdateBlackoutPeriod_ForWrongResourceId_ReturnsNotFound` (+ `errorCode`) | same | Pass |
| `DeleteBlackoutPeriod_ForWrongResourceId_ReturnsNotFound` (+ `errorCode`) | same | Pass |
| `CreateResource_AsMember_StillHasCorrelationIdHeaderOnTheBareForbiddenResponse` | same | Pass — confirms `X-Correlation-Id` survives a bodyless 403 |
| `CreateResource_WithoutToken_StillHasCorrelationIdHeaderOnTheBareUnauthorizedResponse` | same | Pass — same, for a bare 401 |

### Identified as MUST-ADD but deliberately NOT added (disclosed trade-off)

| Gap | Why not added |
|---|---|
| Resource name / AvailabilityRule / ResourceApprover uniqueness under **real concurrent** SQL Server writes | Needs a genuine multi-threaded harness against LocalDB; bigger investment than a no-design-decision test change. |
| `Cancelled`/`Rejected`/`Completed`/`NoShow` bookings don't block availability, at HTTP level for all 4 statuses | `TestDataSeeder` only seeds `Confirmed`/`Cancelled`; extending a shared fixture has wider blast radius. |
| Overlapping blackout+booking / two bookings exceeding capacity, proven at HTTP level | Same reason — needs new shared fixture data. |
| Approver-only, SysAdmin, Member+Approver role combinations | `TestDataSeeder.cs` defines no `Approver`/`SysAdmin` role/user at all. |
| Cross-tenant `UserId` in `AssignResourceApprover` | Low marginal risk (same tenant-filter mechanism verified elsewhere) but untested. |
| Exact 200/2000/1000-char `Name`/`Description`/`Reason` boundaries | No existing test checks these at all today. |
| `Guid.Empty` on every route id, at HTTP level | Reasoned through via code, not exhaustively tested. |

---

## 4. SOURCE-OF-TRUTH CONFLICTS

**The most significant finding was a near-miss where code alone would have produced a false-positive defect.**

- `ErrorCodes.cs` defines `ResourceTypeNotFound`, `ResourceNameConflict`, `AvailabilityRuleNotFound`, `AvailabilityRuleConflict` — none ever attached at any throw site (grep-confirmed). Looking only at code, this is exactly the defect the audit was told to hunt for. But `.claude/skills/add-cqrs-feature/SKILL.md` explicitly documents this as intentional: handlers written before Group 3 (Resource, AvailabilityRule) deliberately stay without `ErrorCode` — don't retroactively change them without explicit agreement. Cross-checked all 11 "missing ErrorCode" throw sites — every one is Resource/AvailabilityRule, 100% consistent with the rule. **Not a defect** — PASS, working as intentionally designed. `ErrorCodes.cs`'s own top comment doesn't mention this exemption, though — worth a one-line addition so a future reader doesn't independently rediscover this.
- `.claude/skills/tenant-isolation-review/SKILL.md` planned cross-tenant test coverage for Resources to land in the Group 7 integration tests. It shipped with zero cross-tenant tests for any WP-3 sub-resource route. Closed this gap with 5 new cross-tenant tests (§3).
- The same skill file documents an existing, unrelated, already-known WP-2 Auth bug (stale Authorization header on `/auth/login`) — explicitly out of WP-3 scope, not investigated further.
- No other disagreement found between AI-USAGE.md, skill files, and actual code.

---

## 5. WORK-PACKAGE PROOF MATRIX

| # | Requirement | Status | Evidence |
|---|---|---|---|
| 1 | ResourceType FK exists-check (no lifecycle) | NOT-APPLICABLE (lifecycle) / PASS (exists-check) | `ResourceRepository.cs:25-26` |
| 2 | Pagination (GetResourcesQueryRequest only) | PASS | `GetResourcesQueryRequest.cs` |
| 3 | Resource queries + filters | PASS | `ResourceRepository.cs:28-50` |
| 4 | Approver assign/remove/list (no candidates query) | PASS / NOT-APPLICABLE | `AssignResourceApproverCommandRequest.cs` |
| 5 | Resource CRUD/lifecycle | PASS | `DeleteResourceCommandRequest.cs:19-46` |
| 6 | RequiresApproval plain field, no invariant | NOT-APPLICABLE (matches documented scope) | `RemoveResourceApproverCommandRequest.cs:8-23` |
| 7 | AvailabilityRule create+delete only | PASS | `ResourcesController.cs:52-64` |
| 8 | Capacity field, no query-side filter | NOT-APPLICABLE | `GetResourcesQueryRequest.cs:8` |
| 9 | BlackoutPeriod full CRUD | PASS | `Update/UpdateBlackoutPeriodCommandRequest.cs` |
| 10 | Blackout lifecycle states | NOT-APPLICABLE (none exist) | grep confirmed |
| 11 | Timezone contract, DST v1 limitation | PASS | `GetResourceAvailabilityQueryRequest.cs:112-120` |
| 12 | Live availability algorithm | PASS | see §6 |
| 13 | Authorization on every route | PASS | `ResourcesController.cs` full read |
| 14 | Tenant isolation | PASS | `BookSpaceDbContext.cs:38-59` |
| 15 | Structured ProblemDetails | PASS | `Program.cs:81-84` |
| 16 | Concurrency (unique-index-backed only) | PASS, confirmed this is the whole mechanism | `DbContextConcurrencyExtensions.cs` |
| 17 | DB uniqueness/indexes/constraints | PASS | `BookSpaceModelConfiguration.cs` |
| 18 | Development seed | PASS with findings | see §11 |
| 19 | Booking-scope boundary/privacy | PASS (tested) / GAP (full 6-status matrix at HTTP) | `IBookingAvailabilityRepository.cs` |

---

## 6. BUSINESS INVARIANT AUDIT

### Resource
- Name uniqueness is **not** filtered to Active-only (`BookSpaceModelConfiguration.cs:83`) — an archived resource's name is reserved forever. Now proven by `CreateResource_WithNameOfArchivedResource_ReturnsConflict`. **No source states which behavior is intended** — needs an explicit product decision, not silent inference.
- App pre-check + DB unique index agree, mapped to 409 — confirmed at code level and empirically (SQL error 2601 reproduced live against LocalDB).
- Archived resource CAN still be updated (validator only blocks setting Status *to* Archived) — no test proves an update actually succeeds against one (low-risk gap).
- Delete-as-archive idempotent, proven by `Times.Never` assertion on `SaveChangesAsync`.
- Capacity-decrease and RequiresApproval-while-Pending-approvals guards: confirmed absent, NOT-APPLICABLE (nothing requires either).
- ResourceType is a shared, non-tenant-scoped lookup — intentional, nothing sensitive on the row.

### AvailabilityRule
- Exact-duplicate rejected via unique index + pre-check + ConflictException — PASS (ErrorCode absence is the intentional pre-Group-3 exemption, §4).
- Overlapping-but-different rules allowed and merged — now proven at Create level (`CreateAvailabilityRule_WithOverlappingButDifferentRule_BothSucceed`), not just the merge algorithm.
- Wrong-resource-id-in-URL on delete → 404 — proven at unit and HTTP level.
- No midnight-spanning rules possible — empirically confirmed exact tick boundary against real SQL Server: `EndTime = StartTime + 1 tick` accepted, `EndTime == StartTime` rejected with SQL error 547, validator and CHECK constraint agree exactly.
- Concurrent identical-rule race: mechanism confirmed present, not live-race-tested (§3 gap).

### BlackoutPeriod
- `EndUtc > StartUtc` boundary empirically confirmed exact match between validator and CHECK constraint on real SQL Server.
- Overlapping/adjacent blackouts allowed — now proven at Create via new HTTP test. Not separately proven at Update (no code path could reject it either).
- Wrong-resource-id-in-URL pattern proven at unit level for both Update and Delete, now also at HTTP level for both.
- No lifecycle-state guard on editing an already-shown-busy blackout — NOT-APPLICABLE, consistent with "availability is a snapshot" position.

### ResourceApprover
- Unique (ResourceId, UserId) double-guard — PASS, proven at HTTP and unit level.
- Cross-tenant UserId "doubles as tenant check for free" — confirmed true by reading `UserRepository.cs:12-13`, not independently HTTP-tested (§3 gap).
- No guard against removing last approver from RequiresApproval=true resource — confirmed absent; the audit's own scope material states this as an already-established fact, classified NOT-APPLICABLE (accepted simplification).
- No candidate-users query — NOT-APPLICABLE.

### Live availability
- Per-day: only weekdays with a matching rule contribute an OpenPeriod, no fallback — confirmed.
- Blackout consumes full capacity, excluded from BookableSlots entirely (not shown at 0) — confirmed via code + unit + HTTP tests.
- Booking occupancy stacks additively, overbooked sub-intervals dropped — proven at pure-calculator level, **not** at HTTP level (§3 gap, disclosed).
- 91-vs-92-day boundary now proven exactly.
- Deterministic sorting confirmed.
- Read-only path confirmed: no `SaveChangesAsync` reachable directly or transitively.
- **No `AsNoTracking()` anywhere in the Resources feature** — minor performance gap, non-blocking.
- No `IgnoreQueryFilters()` anywhere — tenant isolation cannot be bypassed on this path.
- An Archived resource in the same tenant is NOT rejected by the availability query — it'll compute availability for a resource no longer offered, and the response doesn't expose Status so there's no signal. Non-blocking observation.

---

## 7. SECURITY / TENANT / AUTH AUDIT

Manual, line-by-line equivalent of the `/tenant-isolation-review` checklist — **not** run as an automated skill this session.

- Every `TenantId =` assignment in `BookSpace.Application/Resources/**` (4 hits) reads `currentUserContext.TenantId!.Value`, never from the request DTO — confirmed complete.
- Zero `IgnoreQueryFilters()` calls anywhere in the repo.
- Resource/AvailabilityRule/BlackoutPeriod/ResourceApprover all `ITenantOwned`, covered by the reflective `HasQueryFilter` — confirmed by reading the reflection code itself.
- Cross-tenant resource id → 404 on every sub-resource route — now proven with 5 new tests.
- Same-tenant wrong-parent-resource id → 404 — proven at unit level for all four, HTTP level for three (new).
- Every one of the 10 write actions in `ResourcesController.cs` carries `[Authorize(Roles = "TenantAdmin,SysAdmin")]` — verified by reading the full controller.
- Member-only → 403 proven 5 times. **Approver-only and Member+Approver → 403 NOT tested** — no `Approver` role/user exists in `TestDataSeeder.cs` at all. Low residual risk (standard OR-semantics, no WP-3 code branches on Approver specifically) but inference, not proof.
- All 6 read actions confirmed open to any authenticated role.
- `Detail` on every exception handler sourced only from an application-authored message, never a raw exception/driver message — verified by reading all 4 handlers plus an HTTP test asserting no leak.
- **Architectural note**: `BookSpaceDbContext`'s tenant filter has a `TenantId == null` bypass branch, but `User.TenantId` is non-nullable and every JWT always carries a `tenant_id` claim — so this branch is currently unreachable, and "SysAdmin" today behaves identically to "TenantAdmin" everywhere in WP-3 (no cross-tenant reach mechanism exists). Worth knowing before building real cross-tenant SysAdmin tooling on this filter.

**Recommendation**: run `/tenant-isolation-review` manually one more time over the full WP-3 diff before merge — this manual pass covered its checklist but isn't a substitute for the automated skill.

---

## 8. ERROR CONTRACT AUDIT

Full ErrorCode inventory (throw sites WITH a code: Group 3+ handlers — BlackoutPeriod, ResourceApprover, resource-existence checks from within those; throw sites WITHOUT: all Resource/AvailabilityRule handlers, the intentional pre-Group-3 exemption from §4).

- Exception handler registration order (`Program.cs:81-84`) confirmed still correct: Validation → NotFound → Conflict → Global, first-match-wins.
- Correlation ID confirmed present on body extension + header for all 4 handlers, AND newly confirmed on bare, bodyless 401/403 (2 new tests) — `CorrelationIdMiddleware` sets the header before auth middleware runs and it survives since no response reset occurs on that path.
- **CHECK-constraint reachability** (built a throwaway scratch DB, dropped after use, dev DB untouched): `Capacity<=0`, `EndTime<=StartTime`, `EndUtc<=StartUtc` all raise SQL error 547 — a different number than the 2601/2627 `SaveChangesHandlingConflictsAsync` catches. Currently unreachable in practice (FluentValidation's boundary matches the CHECK constraint's boundary exactly, verified to the 100ns tick) but fragile: nothing guards against a future FluentValidation change drifting from the constraint.
- `Guid.Empty` route values and 200/2000/1000-char boundaries: reasoned through / structurally enforced but not exhaustively HTTP-tested.

---

## 9. DATABASE / TRANSACTION / LOCKING / CONCURRENCY AUDIT

- Confirmed: this feature's only concurrency guard is unique-index-backed conflict detection via `SaveChangesHandlingConflictsAsync` (SQL 2601/2627 → ConflictException). No `ITransactionalCommand`, no `UPDLOCK`/`HOLDLOCK`, no `SERIALIZABLE`, no deterministic lock ordering anywhere.
- Write paths WITH the guard (backed by a real unique index): Create/Update Resource (name), Create AvailabilityRule (exact-duplicate), Assign Approver (pair) — all route through the shared extension.
- Write paths WITHOUT it: nothing they do could violate a uniqueness constraint by design (Update/Delete BlackoutPeriod, Delete Resource, Delete AvailabilityRule, Remove Approver, Create BlackoutPeriod) — no unguarded race identified.
- Empirically re-verified against real SQL Server LocalDB (throwaway scratch DB): Resource-name uniqueness is case-**insensitive** at the DB collation level and the app pre-check agrees; `ORDER BY`/`<`/`>` on `datetimeoffset` both work fine on real SQL Server, confirming the SQLite-only nature of the two bugs fixed earlier this session — and confirming the client-side-filtering workaround is a real, if currently small, performance cost on the actual deployment target for `BookingAvailabilityRepository` specifically (§2).
- Cascade behavior as documented: AvailabilityRule/BlackoutPeriod/ResourceApprover cascade from Resource; Booking→Resource is Restrict (documented in code as the actual reason Delete is soft-only).
- No N+1 in the availability handler (exactly 4 queries total, not per-day/per-rule).
- AvailabilityRule/BlackoutPeriod repos fetch all rows per resource with no date filter, handler filters in memory — acceptable given realistic per-resource row counts, unlike the unbounded Booking case in §2.

---

## 10. TEST EXECUTION RESULTS

| Project | Before | After | Delta |
|---|---|---|---|
| `dotnet build` | 0 warnings, 0 errors | 0 warnings, 0 errors | unchanged |
| `BookSpace.Application.Tests` | 121 | **122** | +1 |
| `BookSpace.Infrastructure.Tests` | 10 | **10** | +0 |
| `BookSpace.Api.Tests` | 47 | **61** | +14 |
| **Total** | **178** | **193** | **+15** |

Independently re-verified by running all three suites directly after the audit completed — counts match exactly. All new tests re-run multiple times with identical results, no flakiness. Working tree change: only the two test files touched, 246 insertions, 0 deletions, no production code.

---

## 11. NON-BLOCKING CODE QUALITY OBSERVATIONS

- `DevelopmentSeeder.cs` never actually seeds a `ResourceApprover` row for Conference Room A despite `RequiresApproval=true` and a seeded Pending booking + ApprovalRequest referencing `approver@acme.test` — a real, dev-only inconsistency in the seed's own internal story.
- `SysAdmin` role is created but never assigned to any seeded user — no way to log in as SysAdmin against the dev seed today.
- `ErrorCodes.cs`'s top comment doesn't explain the pre-Group-3 exemption (§4) — a one-line addition would prevent future rediscovery.
- `AsNoTracking()` never used anywhere in the Resources feature, including the fully read-only availability handler.
- `GetResourceAvailabilityResponse` doesn't expose the resource's own `Status`.
- Booking over-fetch performance concern — full detail in §2/§9.

---

## 12. EXPLICIT OUT-OF-SCOPE REMINDERS

- Controller naming, route grouping, OpenAPI cosmetics — untouched, out of scope.
- `postman/`/`.postman/` and further `AI-USAGE.md` polish — separate in-progress work, not opened or touched.
- Booking lifecycle/API remains entirely out of scope; `IBookingAvailabilityRepository` stays deliberately read-only.
- The already-documented future Booking-create locking design (UPDLOCK/HOLDLOCK or SERIALIZABLE) is a known, saved follow-up, not designed or implemented here — re-confirmed the current read path has zero locking infrastructure, so that plan still accurately describes what's needed.

---

## 13. MANUAL FOLLOW-UP

- Run `/tenant-isolation-review` manually over the full WP-3 diff before merge.
- Get an explicit product decision on whether an archived resource's name should stay reserved forever or free up for reuse (§6) — no documented intent either way.
- Decide whether to close the disclosed test gaps (§3) before or after merge — none currently point at a known defect, but none are proven correct by an automated test either.
- Revisit the `BookingAvailabilityRepository` over-fetch (§2/§9) before this resource type accumulates meaningful booking history in a real tenant.
