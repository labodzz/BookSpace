# Bookings and Concurrency

What the Booking work packet (single-user correctness, then concurrency) actually built, on top of the
`Booking` entity/table/status enum that already existed in the schema since WP-1. Read
[resource-lifecycle-and-capacity.md](resource-lifecycle-and-capacity.md) and
[availability-and-timezones.md](availability-and-timezones.md) first - this document assumes both.
Recurring bookings and the approval workflow build on everything here without changing it - see
[recurring-bookings-and-approvals.md](recurring-bookings-and-approvals.md), including a real EF Core
concurrency bug that work found and fixed.

## 1. Booking lifecycle

`BookingStatus`: `Pending, Confirmed, Rejected, Cancelled, Completed, NoShow` (unchanged - this enum
already existed). This work packet only ever creates a booking directly into `Confirmed` - it never
produces `Pending`, `Rejected`, `Completed`, or `NoShow`. `Resource.RequiresApproval` is deliberately
**not** wired into booking creation yet: doing so would mean creating `Pending` bookings with no
approve/reject handler to ever move them out of that state, which is a half-built feature, not a smaller
version of one. See [open-questions.md](open-questions.md#booking-approval-workflow).

The only state transition this work packet implements is `Pending|Confirmed -> Cancelled` (member
self-cancellation). `Rejected`/`Completed`/`NoShow` have no producer yet either - they exist in the
schema and are already handled correctly everywhere they're read (e.g. excluded from active-booking
capacity), but nothing in the API produces them.

## 2. Ownership and tenant isolation

`Booking.TenantId` and `Booking.UserId` are always server-derived from `ICurrentUserContext`
(`TenantId!.Value`, `UserId!.Value`) on create - never accepted from the client request body. `Booking`
already implemented `ITenantOwned`, so it inherits the fail-closed global query filter for free (see
[tenant-isolation.md](tenant-isolation.md)) - no new scoping mechanism was introduced.

A member can only ever create bookings in their own tenant (the parent `Resource` is loaded through the
tenant-filtered `IResourceRepository`, so a cross-tenant `ResourceId` simply isn't found - 404, not a
403 that would confirm the resource exists), only ever list their own bookings
(`GetOwnBookingsQueryRequest` has no `UserId` parameter to accept from the client at all - the repository
call is always `GetOwnBookingsAsync(currentUserContext.UserId, ...)`), and can cancel their own booking,
or - as of the interim implementation below - a `TenantAdmin`/`SysAdmin` can cancel any booking in the
tenant (`CancelBookingCommandHandler` checks `booking.UserId == currentUserContext.UserId` OR
`ApprovalAuthorization.IsTenantAdminOrSysAdmin(currentUserContext)`, throwing `NotFoundException` - not
`ConflictException`/403 - when neither holds, so cancelling a booking that belongs to someone else without
the privileged role looks identical to cancelling one that doesn't exist, matching this codebase's
existing "never leak that another tenant's/user's row exists" convention).

A `TenantAdmin`/`SysAdmin` cancelling someone else's booking can supply an optional `reason`, recorded on
`Booking.CancellationReason` alongside `CancelledByUserId` (the acting admin, not the booking's owner) -
`GetOwnBookingsQueryRequest` surfaces these as `CancelledByAdmin`/`CancellationReason`/`CancelledAtUtc` so
the owner can see who cancelled it and why in their own booking history. There is still no *active*
notification (email/push/in-app) sent at cancellation time - that remains a genuinely open product
question. See
[open-questions.md](open-questions.md#tenantadmin-cancellation-of-another-users-booking--interim-implementation-notification-still-open)
for the full writeup: this cancellation capability was implemented as a proposed interim middle ground,
not a final product decision.

## 3. Interval semantics (reused, not reinvented)

`[Start, End)` half-open throughout, exactly matching `IntervalMath`'s existing semantics:
`start == end` is rejected by `CreateBookingCommandRequestValidator` (`EndUtc` must be strictly greater
than `StartUtc`); a booking ending exactly when another begins does not overlap it; a blackout ending
exactly when a booking begins (or vice versa) does not overlap. These are proven directly:
`CreateBookingCommandHandlerTests.Handle_WithBlackoutEndingExactlyAtBookingStart_DoesNotOverlapAndSucceeds`,
`..._WithBookingEndingExactlyWhenBlackoutStarts_...`, and
`..._WithExistingBookingEndingExactlyWhenNewOneStarts_...`.

`IntervalMath` gained one new pure function, `Covers(window, periods)` - "do these periods collectively
span the entire window with no gap" - used for two different purposes in `CreateBookingCommandHandler`:
checking the requested window is entirely inside the resource's open hours, and (separately) checking
remaining capacity holds for the *entire* requested duration, not just at one instant. It has its own
unit tests in `IntervalMathTests.cs` (touching periods, gaps, empty input, exact-boundary coverage).

## 4. Validation and rejection-reason contract

Shape-only checks run in `CreateBookingCommandRequestValidator` (matches the existing convention: never
touches the database): `ResourceId` not empty, `StartUtc` not in the past, `EndUtc > StartUtc` (rejects
`start == end` too), `Quantity > 0`. These map to the existing `400 ValidationProblemDetails` shape - no
new mechanism.

Business-rule rejections reuse the existing `ConflictException`/`NotFoundException` + `ErrorCodes`
contract (see `docs/architecture.md`'s error-handling section) rather than inventing a third exception
type or status code. `ErrorCodes.cs` gained:

| Code | HTTP | Meaning |
|---|---|---|
| `Booking.NotFound` | 404 | Booking id doesn't exist, or belongs to a different tenant/user |
| `Booking.ResourceUnavailable` | 409 | Resource status isn't `Active` (Archived/Inactive/Maintenance) |
| `Booking.OutsideAvailability` | 409 | Requested window isn't fully covered by an `AvailabilityRule` |
| `Booking.BlackoutConflict` | 409 | Requested window overlaps a `BlackoutPeriod` |
| `Booking.CapacityExceeded` | 409 | Remaining capacity can't cover the requested quantity for the whole window |
| `Booking.CancellationNotAllowed` | 409 | Cancelling a booking that's `Completed`/`Rejected`/`NoShow` |

All four booking-creation checks were considered 409, not a mix of 400/404/409, on the reasoning that a
well-formed request being rejected by current domain state (not by malformed shape, not because the
referenced row doesn't exist) is exactly what `ConflictException` already means elsewhere in this
codebase (e.g. duplicate resource names). This is a judgment call, not something the existing code
already answered definitively - flagged here rather than assumed silently.

Checks run in a fixed order (resource status -> availability -> blackout -> capacity), so a request that
fails more than one check always gets the same, deterministic reason back.

## 5. Capacity semantics

The invariant, unchanged from the pre-existing availability query: `sum(Quantity of overlapping active
bookings) + new Quantity <= Resource.Capacity`, where "active" means `Pending` or `Confirmed`
(`BookingAvailabilityRepository.ActiveStatuses` - reused, not redefined) and blackouts consume the
resource's *full* capacity for their span. `Cancelled`/`Rejected`/`Completed`/`NoShow` never count.

The check isn't a flat sum over the whole requested window - that would wrongly reject a booking that
only conflicts with part of an existing booking's span (see `IntervalMath.ComputeAvailableCapacity`'s
existing sub-interval decomposition, already proven correct for the availability query and reused
as-is here). `CreateBookingCommandHandlerTests.Handle_WhenCapacityLeavesExactlyEnoughRoom_Succeeds`
proves the boundary: requesting exactly the remaining capacity succeeds, not just strictly-less-than.

## 6. Cancellation semantics

- The booking's own `UserId`, or a `TenantAdmin`/`SysAdmin` acting on any booking in the tenant, may
  cancel it (see §2).
- Cancelling an already-`Cancelled` booking is **idempotent**: it returns the current state without
  writing again. This mirrors `DeleteResourceCommandHandler`'s existing Archive-idempotency pattern
  exactly, applied to the same "soft-terminal-state" shape.
- Cancelling a booking in a genuinely different terminal state (`Completed`, `Rejected`, `NoShow`) is
  **not** idempotent - it's a real conflict (`Booking.CancellationNotAllowed`, 409), since none of those
  three can ever have been legitimately reached via "cancel" and treating them as a silent no-op would
  hide a real state-machine violation.
- Cancellation populates `CancelledAtUtc`/`CancelledByUserId` (already-existing schema columns from
  WP-1, never populated by any handler before this).
- Cancelled bookings stop counting toward capacity immediately - proven end-to-end by
  `BookingsEndpointsTests.CancelBooking_ThenAvailability_ShowsCapacityRestoredForThatSlot`, which books a
  resource to full capacity, confirms the slot is unavailable, cancels, and confirms the slot is fully
  available again via the real availability endpoint.
- `Booking` deliberately did **not** get a `RowVersion` column. Idempotent cancellation means two
  concurrent cancel requests on the same booking are harmless (both converge on `Cancelled`), so there is
  no destructive-overwrite scenario that optimistic concurrency would need to guard against - adding one
  speculatively would violate this codebase's own stated policy against adding `RowVersion` "just in
  case" (see [optimistic-concurrency.md](optimistic-concurrency.md)).

## 7. The concurrency strategy (the hard problem)

### The invariant that must hold under concurrency

> Two concurrent booking-creation requests for the same resource must never together create bookings
> whose combined `Quantity` (plus everything already active) exceeds `Resource.Capacity` for any instant
> they overlap.

SQL Server has no PostgreSQL-style exclusion constraint for overlapping ranges, so - unlike every other
conflict this codebase protects with a plain unique index plus
`SaveChangesHandlingConflictsAsync` (duplicate names, duplicate rule windows, duplicate approver
assignments) - this invariant cannot be enforced by a constraint the database checks for you. It has to
be enforced by making the check-then-insert sequence atomic with respect to other concurrent attempts for
the same resource.

### Two strategies considered

**A) `sp_getapplock` keyed by resource.** Correct, and the right lock granularity, but calling a stored
procedure with an output parameter through EF Core is foreign to this codebase's all-LINQ style, and it
requires inventing and keeping collision-free a synthetic lock-name namespace with no natural home.

**B) `SERIALIZABLE` isolation + a covering index + deadlock retry**, scanning `Bookings` for the
overlapping range. SQL Server's locking-based `SERIALIZABLE` takes **key-range locks** over whatever the
query plan actually scans. The overlap predicate (`StartUtc < newEnd AND EndUtc > newStart`) isn't a
single contiguous seek against the `(TenantId, ResourceId, StartUtc, EndUtc)` index, so the engine would
range-lock "everything for this resource with `StartUtc < newEnd`" - i.e. that resource's **entire
booking history up to the new booking's end time**, not just the rows that actually overlap. That's
unbounded lock footprint that grows with history, exactly the kind of scan this codebase's own indexing
guidance warns against, and `SERIALIZABLE` under contention is deadlock-prone, requiring an explicit
app-level retry loop.

### Chosen: a transaction-scoped exclusive lock on the parent `Resource` row

`SELECT TOP (1) Id FROM Resources WITH (UPDLOCK, HOLDLOCK) WHERE Id = @resourceId`, executed at the start
of a transaction, before any capacity/availability/blackout check runs. This is functionally "Option A's
granularity" (one lock per resource, held for the transaction's life), implemented with SQL Server's
native row-locking hints instead of a stored procedure - it reuses the exact row the handler needs to
load anyway, has no synthetic naming concern, and its lock footprint is exactly one indexed PK row rather
than a range that grows with a resource's history.

Implementation: `IResourceBookingLock` (`BookSpace.Application/Bookings/IResourceBookingLock.cs`), backed
by `ResourceBookingLock` (`BookSpace.Infrastructure/Persistence/ResourceBookingLock.cs`).
`CreateBookingCommandHandler.Handle` is a two-line wrapper:
`resourceBookingLock.RunExclusiveAsync(request.ResourceId, ct => CreateUnderLockAsync(request, ct), ct)`.
Everything from the `Resource.Status` check through `SaveChangesAsync` runs inside `CreateUnderLockAsync`,
i.e. entirely inside the lock.

**What exactly is locked**: one row - the target `Resource`'s primary-key row - for the life of the
enclosing transaction (`HOLDLOCK` holds it to commit/rollback, not just for the instant of the read).

**Transaction boundary**: `BeginTransactionAsync` -> acquire the row lock -> re-load `Resource` and check
`Status` -> compute open periods and check availability -> check blackout overlap -> re-fetch active
bookings and check capacity -> insert `Booking` -> `SaveChangesAsync` -> `CommitAsync`. Any exception
before commit (a rejected check, or a `SaveChanges` failure) rolls the transaction back, so a rejected
attempt writes nothing - proven by every failure-path test asserting `SaveChangesAsync` was never called,
and the concurrency test asserting the database ends with exactly the allowed number of rows, never an
orphan or partial one.

**Why it prevents the race**: two concurrent create-booking calls for the *same* resource both attempt
`UPDLOCK` on that same row; SQL Server lets exactly one proceed, the other blocks until the first commits
or rolls back. The second then runs its own checks against the now-current, committed state - it can
never observe the "the slot looked free" snapshot the first request saw before writing.

**Different resources**: fully independent. The lock is per-PK-row, so a request for resource A never
waits on a request for resource B.

**Overlapping vs. non-overlapping bookings for the *same* resource**: this is a deliberate, stated
trade-off, not an oversight - **every** create-booking call for a given resource serializes against every
other one, even for non-overlapping time windows, because the lock is resource-scoped, not
interval-scoped. A single room/desk/asset does not receive booking-creation throughput anywhere near
where this matters in practice; the alternative (interval-scoped locking) would need to reason correctly
about partial overlaps at the SQL level, reintroducing exactly the unbounded-range-lock problem Option B
has.

**Cancellation does not need this lock at all**: removing an active booking can only *reduce* summed
demand, which can never turn a valid capacity state into an invalid one. `CancelBookingCommandHandler` is
an ordinary load-mutate-save, with no interaction with `IResourceBookingLock`.

**Reducing `Resource.Capacity` DOES need this lock, and originally didn't have it.**
`UpdateResourceCommandHandler.EnsureCapacityCoversExistingBookingsAsync` re-validates that a proposed
capacity reduction still covers existing bookings' peak demand (see
[resource-lifecycle-and-capacity.md](resource-lifecycle-and-capacity.md)) - but until this was fixed, it
did so with a plain, unlocked read, then saved relying solely on `Resource.RowVersion` for conflict
detection. That's not sufficient: `RowVersion` only detects a competing *write to the `Resources` row
itself*, and booking creation never writes to `Resources` at all (only `Bookings`), so this race was
real:

1. `Resource.Capacity = 10`, one Confirmed booking already uses 5.
2. Request A (reduce capacity to 6) reads demand = 5, validates `5 <= 6`, but hasn't saved yet.
3. Request B (`CreateBookingCommandHandler`, quantity 3) acquires the resource lock, reads
   `Capacity = 10` (A's change isn't saved yet), demand = 5, validates `5 + 3 = 8 <= 10`, inserts, commits.
4. Request A saves `Capacity = 6`. Its `Resource` row's `RowVersion` is still the one it read in step 2 -
   nothing touched the `Resources` row between steps 2 and 4, so the save succeeds with no conflict.
5. Final state: `Capacity = 6`, committed demand = 8. Invariant violated, with no exception raised
   anywhere.

The fix: `UpdateResourceCommandHandler.Handle` now wraps its entire body in the same
`resourceBookingLock.RunExclusiveAsync(request.Id, ...)` boundary `CreateBookingCommandHandler` uses, and
the `Resource` read plus the demand recheck both happen *after* the lock is acquired - never reusing a
value read before it. This makes step 3 and step 2-4 mutually exclusive: whichever request acquires the
lock first runs to completion (commit or rollback) before the other even starts its read, so the other
always re-validates against the true, current state. Proven by
`BookSpace.Infrastructure.Tests.Persistence.UpdateResourceCapacityConcurrencyTests` - including a test
that runs the two requests above concurrently via a shared start gate and asserts the database never ends
up with `Capacity` below actual committed demand, regardless of which request happens to win the race
(either resolution - the reduction rejected, or the new booking rejected - is correct; what must never
happen is both succeeding).

**Deadlocks**: every booking-create transaction acquires exactly one lock (one resource, one row, always
first), so no lock-ordering cycle is possible between two such transactions. The remaining transient
failure mode is a genuine SQL Server deadlock victim or lock-wait timeout (error 1205 / 1222) - caught
explicitly in `ResourceBookingLock`, logged as a warning, rolled back, and surfaced as a plain
`ConflictException` ("please retry"), never a raw `SqlException`/500.

**Timeout behavior**: governed by SQL Server's own `LOCK_TIMEOUT` (the connection default - not
overridden) and its deadlock detector; no custom timeout logic was added.

**Failure/rollback**: any exception inside `CreateUnderLockAsync` - a rejected check or a
`SaveChangesHandlingConflictsAsync` failure - triggers `transaction.RollbackAsync()` before rethrowing,
so a failed attempt never leaves a partial row and always releases the lock.

**Performance**: acquiring/releasing a single row lock is materially cheaper than a `SERIALIZABLE` range
scan whose size grows with a resource's booking history, and there is no retry loop needed for the
ordinary contention case - a second caller simply waits, then proceeds normally.

**SQL Server/LocalDB**: `UPDLOCK`/`HOLDLOCK` are native SQL Server hints, identical behavior on LocalDB
(same engine). On SQLite (`Database.IsSqlServer()` is `false`) `RunExclusiveAsync` runs `operation`
directly with no locking - the exact same provider-branch idiom `BookingAvailabilityRepository` already
established for its range-filtered query. This is safe because SQLite is never the correctness authority
for this invariant; it's only used for the fast, effectively-single-request `BookSpace.Api.Tests` suite.

**Why preferable to `SERIALIZABLE`+retry**: smaller and more precisely bounded lock footprint (one row,
not a resource's entire booking history up to the new end time), no app-level retry loop needed for
ordinary contention, and it reuses a row the handler already has to load rather than adding a second
query or a synthetic lock name.

## 8. Testing strategy and evidence

**`BookSpace.Application.Tests/Bookings/`** (mocked repositories, no DB): every rejection reason, both
interval-boundary "touches but doesn't overlap" cases, the exact-remaining-capacity boundary, cancel
ownership/idempotency/terminal-state rules. `IntervalMathTests.cs` gained direct unit tests for `Covers`.

**`BookSpace.Api.Tests/Bookings/BookingsEndpointsTests.cs`** (SQLite, real HTTP pipeline): auth/RBAC
wiring, ProblemDetails/`errorCode` shape on the wire, cross-tenant 404 on both create and cancel,
cross-user 404 on cancel (same tenant, different owner), the freed-capacity-after-cancellation proof
against the real `/api/resources/{id}/availability` endpoint.

**`BookSpace.Infrastructure.Tests/Persistence/BookingConcurrencyTests.cs`** (real SQL Server LocalDB,
throwaway database per test-class run, same pattern as `OptimisticConcurrencyTests`) - this is the only
place the actual double-booking guarantee is proven, and it proves both sides:

1. `NaiveCheckThenInsert_RacedByTwoRequests_CanOverbook` - a deliberately reconstructed "read the active
   booking sum, then insert" anti-pattern (**not** the shipped code - `CreateBookingCommandHandler` was
   never written this way), with both requests forced through a `Barrier` so both finish reading before
   either writes. This reliably (not statistically - the barrier makes it deterministic) produces 2
   bookings against `Capacity = 1`, concretely demonstrating why a lock is required at all rather than
   asserting it from first principles.
2. `CreateBookingCommandHandler_TwoConcurrentRequestsForCapacityOne_ExactlyOneSucceedsAndOneIsRejected` -
   the real handler and the real `ResourceBookingLock`, two requests released from a shared start gate at
   the same instant. Exactly one succeeds; the other gets an ordinary `Booking.CapacityExceeded` 409 (not
   a generic lock-conflict error - it waited, then correctly saw the slot was full); the database ends
   with exactly one `Booking` row.
3. `CreateBookingCommandHandler_FourConcurrentRequestsForCapacityThree_ExactlyThreeSucceed` - the same
   proof at `Capacity = 3` with four simultaneous requests, confirming the invariant holds at
   capacity > 1, not just the capacity = 1 edge case.

All three were run five consecutive times during development with zero flaky failures.

## 9. Known limitations

- Same-resource booking-creation calls fully serialize regardless of whether their time windows overlap
  (§7). Acceptable at expected per-resource throughput; would need re-evaluating if a single resource
  ever needs to accept a high rate of concurrent, genuinely-disjoint bookings.
- `Resource.RequiresApproval` is not wired into booking creation (§1) - every booking this work packet
  creates is `Confirmed` regardless of that flag.
- No idempotency-key support on `POST /api/bookings` - a client retrying a timed-out request could create a
  duplicate booking. See [open-questions.md](open-questions.md#idempotency).
- `TenantAdmin`/`SysAdmin` can cancel another user's booking (§2), but there is still no *active*
  notification (email/push/in-app) telling the affected owner it happened - only an audit trail visible
  if/when they check their own booking history (§10).
- Booking creation only accepts an already-UTC `DateTimeOffset` window (matching
  `CreateBlackoutPeriodCommandRequest`'s existing precedent), not a local wall-clock time - a client
  showing a resource's schedule in its own timezone must convert client-side using the same policy
  documented in [availability-and-timezones.md](availability-and-timezones.md).

## 10. Open questions

See [open-questions.md](open-questions.md) for the full entries: **TenantAdmin cancellation of another
user's booking** (and how that user would be notified), **booking approval workflow**
(`RequiresApproval` -> `Pending` -> approve/reject), and **booking idempotency keys**. None of these are
silently decided by this work packet.

## 11. Frontend: the integrated browse-availability-and-book flow

`BookingFormComponent` (`/bookings/new`) is a single page that merges what used to be two separate steps
(browse `ResourceAvailabilityComponent`, then re-enter the same date/time on a bare booking form) into
one: picking a date immediately shows that day's real availability, and selecting a free interval fills
in Start/End directly, all without leaving the page. `ResourceAvailabilityComponent`
(`/resources/{id}/availability`) still exists as a genuinely different, complementary tool - scanning a
multi-day window (up to the API's own 92-day cap) to find a good day in the first place - and its own
"Book" action on a slot now lands here with `start`/`end` query params prefilled.

**No new backend endpoint or contract change was needed.** `GET /api/resources/{id}/availability` already
returns everything a single day's decision needs - `openPeriods`, `blackouts`, `busyPeriods`, and the
pre-computed, capacity-aware `bookableSlots` - so the form fetches it with `from == to == the selected
date` (never a wider range) every time the date changes, via an RxJS `switchMap` so a fast date change
can never let a stale response for an abandoned date overwrite a newer one.

**Resource-local time throughout.** Every date/time shown or entered on this page is in the resource's
own `TimeZoneId` (`resolveLuxonZone`, `parseStrictLocalDateTime` - see
[availability-and-timezones.md](availability-and-timezones.md)), labeled explicitly ("Times shown in
Europe/Sarajevo"), never the viewer's own browser zone. `parseStrictLocalDateTime`'s existing
round-trip check still rejects a nonexistent spring-forward local time before submit; an ambiguous
fall-back time is still accepted and resolved to Luxon's own default (the earlier of the two
occurrences) exactly as it already was.

**Frontend pre-validation vs. backend final authority.** `availability-interval.util.ts`'s
`validateInterval` re-derives the same four rejection reasons `BookingEligibilityChecker` already
enforces (outside open hours, blackout conflict, capacity/overlap conflict, end-before-start) from the
exact same raw `openPeriods`/`blackouts`/`busyPeriods` data the availability response already returned -
not a second, independently-invented availability model. This lets the form show a specific inline
message and block Confirm before a round trip, but it is explicitly advisory: `CreateBookingCommandRequest`
is still sent through the same `BookingEligibilityChecker` + `IResourceBookingLock` path described in §7
above, and that remains the only authority that actually decides whether a booking is created.

**The race this doesn't (and can't) close client-side**: availability is a read-model snapshot with no
lock behind it (§7) - between the page loading a day's data and the user pressing Confirm, someone else's
booking, a new blackout, or an archived resource can make the chosen interval genuinely invalid, and the
live check above has no way to know that until it happens. When `POST /api/bookings` rejects the request with
`Booking.OutsideAvailability`, `Booking.BlackoutConflict`, or `Booking.CapacityExceeded`, the form:
keeps every entered field exactly as the user left it (never resets the date/time/quantity inputs),
shows a distinct inline notice that the time became unavailable, and automatically reloads that day's
availability so the (now-updated) slots are immediately visible again - it never automatically retries
the create request itself, since a blind retry against the same now-known-bad interval would just fail
the same way again.

**Recurring bookings remain a separate flow**, reachable via a tab at the top of both booking pages
(preserving `resourceId` across the switch) rather than folded into the same form - `CreateRecurringSeriesCommandRequest`'s
per-occurrence, partial-success response (some occurrences created, some individually rejected - see
[recurring-bookings-and-approvals.md](recurring-bookings-and-approvals.md)) is a fundamentally different
result shape than a single booking's pass/fail, and `RecurringBookingFormComponent` still only validates
its *first* occurrence's local time client-side, exactly as before this feature - later occurrences'
per-date conflicts (including `NonexistentLocalTime`) are only known once the server actually generates
and checks them. This feature does not attempt to preview a whole series' availability up front; doing
so would require either a new backend endpoint or many per-occurrence availability calls, neither of
which this work packet builds.
