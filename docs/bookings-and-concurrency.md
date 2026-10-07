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
or - as of the interim implementation below - a `TenantAdmin` can cancel any booking in the
tenant (`CancelBookingCommandHandler` checks `booking.UserId == currentUserContext.UserId` OR
`ApprovalAuthorization.IsTenantAdmin(currentUserContext)`, throwing `NotFoundException` - not
`ConflictException`/403 - when neither holds, so cancelling a booking that belongs to someone else without
the privileged role looks identical to cancelling one that doesn't exist, matching this codebase's
existing "never leak that another tenant's/user's row exists" convention).

A `TenantAdmin` cancelling someone else's booking can supply an optional `reason`, recorded on
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

- The booking's own `UserId`, or a `TenantAdmin` acting on any booking in the tenant, may
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
- `CancelBookingCommandHandler` now acquires `IResourceBookingLock` too - not for this section's own
  capacity invariant (unchanged), but to coordinate with `CheckInBookingCommandHandler` and a future
  no-show job, both of which mutate the same booking outside of Cancel's control. A booking that has
  already been checked in (`CheckedInAtUtc is not null`) can no longer be cancelled through this handler at
  all - a new conflict, `Booking.AlreadyCheckedIn` (409), with no admin override. See §12 ("Booking
  check-in - the check-in/cancel race, now closed") for the full design and real-LocalDB proof.

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
- `TenantAdmin` can cancel another user's booking (§2), but there is still no *active*
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

## 12. Booking check-in

`CheckInBookingCommandHandler` (`POST /api/bookings/{id}/check-in`) lets a `Confirmed` booking's owner (or
an authorized approver/admin - see below) record that they actually showed up, by setting
`Booking.CheckedInAtUtc` - a nullable `DateTimeOffset?` column that has existed on `Booking` since
`InitialCreate`, reserved for exactly this. **This is the signal a future no-show job will use to decide
which `Confirmed` bookings to expire** (see `docs/background-jobs.md`, "What the earlier WP-8 branches did
not implement") - `CheckedInAtUtc IS NULL` past the check-in window will mean "nobody showed up." That job
is **not** implemented by this change: no `BackgroundService`, no automatic `BookingStatus.NoShow`
transition, no no-show email, no reminder-with-check-in-link, no QR/NFC/geolocation, no ICS feed. Check-in
does **not** change `BookingStatus` - a checked-in booking stays `Confirmed`, exactly like it was before
check-in; `CheckedInAtUtc` is purely an additional marker, the same pattern `ReminderQueuedAtUtc` already
established for "an orthogonal fact about this booking, not a state transition."

### Why check-in is useful even though it isn't proof of physical presence

Anyone with access to the booking (the owner, or an authorized approver/admin - see below) can call this
endpoint from anywhere; nothing here verifies the caller is physically at the resource. This is a
deliberate, documented limitation, not an oversight - QR/NFC/geolocation-based verification is explicitly
out of this task's scope. Check-in is a **self-reported attendance signal**, valuable because the
alternative (no signal at all) means the future no-show job could only ever guess from `StartUtc` alone,
with no way to distinguish "showed up and used it" from "forgot and left it empty" - the exact gap this
column exists to close.

### The check-in window

Opens `Tenant.CheckInLeadMinutes` (default 15) minutes before `Booking.StartUtc`; closes
`Tenant.NoShowGraceMinutes` (default 15, pre-existing) minutes after `Booking.StartUtc`. **Both boundaries
are inclusive** (`CheckInEligibilityChecker.Evaluate`): `nowUtc == opensAtUtc` is already open,
`nowUtc == closesAtUtc` is not yet closed - proven exactly at both boundaries by
`CheckInEligibilityCheckerTests.Evaluate_ExactlyAtWindowOpen_IsEligible`/`..._ExactlyAtWindowClose_IsEligible`,
with the adjacent one-second-outside instant proven to fail
(`..._OneSecondBeforeWindowOpens_IsTooEarly`/`..._OneSecondAfterWindowCloses_IsTooLate`).

`NoShowGraceMinutes` (already on `Tenant`, previously unused by any handler) is reused as-is for the
closing edge - introducing a second, independent "how late is too late" setting would create two different
answers to the same question once the no-show job is actually built. `CheckInLeadMinutes` is new, added
directly on `Tenant` (not nested in `BackgroundJobsOptions`) because it is per-tenant, live, user-facing
booking policy - the same architectural category `ReminderLeadMinutes`/`NoShowGraceMinutes`/
`ApprovalExpiryHours` already occupy, not a background-job orchestration knob. Migration
`AddTenantCheckInLeadMinutesAndBookingCheckInIndex` backfills every pre-existing tenant to the documented
default (15) via an explicit SQL `DEFAULT` constraint - without it, every tenant that existed before this
migration would have silently gotten 0 (no lead time at all) instead of the intended default.

`CheckInEligibilityChecker.Evaluate` is a small, pure, static function taking `nowUtc` as an explicit
parameter (same shape as `BookingEligibilityChecker`) rather than reading `DateTimeOffset.UtcNow` itself -
this is what makes the exact-boundary tests above possible without any clock abstraction or timing-sensitive
waits. The command handler itself still calls `DateTimeOffset.UtcNow` directly (no `TimeProvider`
injection) - confirmed as the authoritative pattern for a request-scoped command handler by inspecting
every other Bookings handler (`RejectBookingCommandRequest.cs`, `CancelBookingCommandRequest.cs`): none of
them inject `TimeProvider` either; that abstraction is reserved for the background-job subsystem
(`StaleApprovalProcessor`, `ReminderProducer`, ...), which genuinely needs it for deterministic cycle
testing in a way a single HTTP request does not.

### Who may check in which booking

| Caller | Allowed when |
|---|---|
| The booking's own owner (`Booking.UserId`) | Always (subject to status/window) |
| `ResourceApprover` for `Booking.ResourceId` | Always, for ANY booking on that resource - this is a resource-level grant (the same `ResourceApprover` table `ApprovalAuthorization` already reads), independent of whether that specific booking ever needed approval |
| `TenantAdmin` | Any booking in their own tenant (enforced automatically by the global query filter) - the global `SysAdmin` deliberately does NOT get this, see `ApprovalAuthorization.IsTenantAdmin` |
| Anyone else | Never - `Booking.NotFound` (404) |

The route (`[HttpPost("{id:guid}/check-in")]`) is plain `[Authorize]`, **not** role-gated the way
approve/reject are (`[Authorize(Roles = "Approver,TenantAdmin")]`) - a route-level role gate would
block a plain `Member` from checking in their own booking, which is the primary use case. All three
authorization branches are checked inside `CheckInBookingCommandHandler.CanCheckInAsync`.

**Not-owner, not-admin, and not-an-assigned-approver all return the same 404**, never a 403 - the same
"don't leak existence via a different error shape" philosophy `CancelBookingCommandHandler` already applies
to a non-owner's cancel attempt. This differs from `ApprovalAuthorization`'s own forbidden check (a 409 for
an unassigned `Approver`), deliberately: an `Approver` who already sees a booking in their pending-approval
queue "already knows" it exists, so a 409 there is more honest than a fake 404; check-in has no such queue
naming a specific booking to a would-be approver, so there's nothing to be more honest about - a plain 404
is the right, narrower default here.

### Concurrency

`Booking` still has no `RowVersion` column (unchanged - see [optimistic-concurrency.md](optimistic-concurrency.md)).
Check-in uses `IResourceBookingLock` - not because it affects capacity (it doesn't; `CheckedInAtUtc` isn't
read by `BookingAvailabilityRepository` at all), but for the identical reason `RejectBookingCommandHandler`
already does: without a shared serialization point, two concurrent check-in requests for the SAME booking
could both read `CheckedInAtUtc == null` and both write it, the second silently overwriting the first with
a different instant - directly violating the idempotency requirement that a repeated check-in must never
change the original timestamp. `CheckInBookingCommandHandler` follows the exact same shape as
Approve/Reject: a pre-lock `FindResourceIdAsync` projection (never tracked), then the first and only
tracked `FindByIdAsync` load taken *after* the lock is held - never trusting the pre-lock state.

Proven against real SQL Server (LocalDB, not SQLite - see `CheckInConcurrencyTests`,
`TwoConcurrentCheckInRequestsForTheSameBooking_ExactlyOneWritesAndTheOtherReportsAlreadyCheckedInWithTheSameInstant`):
two genuinely concurrent requests for the same booking always produce exactly one write and one
`AlreadyCheckedIn` response reporting the **same** instant the write produced - never two different
timestamps, never two rows, never a lost update. The same lock protects a future no-show job's eventual
`Confirmed -> NoShow` transition too, **provided** that job acquires the identical
`IResourceBookingLock` before mutating `Booking.Status` - exactly as `StaleApprovalProcessor` already does
against Approve/Reject (see `docs/background-jobs.md`, "Stale-approval job").

### The check-in/cancel race - now closed

An earlier version of this document accepted a check-in racing with `CancelBookingCommandHandler` as an
open, pre-existing gap, reasoning that Cancel never needed `IResourceBookingLock` for its own capacity
invariant. That reasoning is no longer sufficient: once `CheckInBookingCommandHandler` started setting
`CheckedInAtUtc` under the lock, Cancel became a SECOND concurrent writer of the same booking that did not
share that lock - two concurrent requests could both read `Confirmed` before either committed and both
"succeed", leaving a booking either `Cancelled` while also bearing a `CheckedInAtUtc` value, or silently
cancelled out from under its own just-recorded attendance. `CancelBookingCommandHandler` now acquires the
SAME `IResourceBookingLock` too - **not** because cancellation affects capacity (it still doesn't; this is
unchanged from §6/§7 above), but purely to coordinate with check-in, and with a future no-show job's
eventual `Confirmed -> NoShow` transition, both of which mutate the same row outside of Cancel's own
control. `CancelBookingCommandHandler.Handle` now follows the identical pre-lock-projection / fresh-
tracked-reload shape every other lock-using handler in this file already uses (Create, Approve, Reject,
Check-in): `FindResourceIdAsync` before the lock, `FindByIdAsync` as the first tracked load after it.

**Why locking the primary booking's resource is sufficient for `CancelRemainingSeries`.** Every occurrence
of one `RecurringSeries` carries the same `ResourceId` as the series itself, assigned once by
`CreateRecurringSeriesCommandHandler` at occurrence-generation time and never mutated by any handler
afterward - confirmed by inspecting every write path to `Booking.ResourceId` in this codebase, there is
none outside that one creation point. So a cascade can never need a second resource's lock; this is the
exact same assumption `ApproveBookingCommandHandler`'s own `ApproveRemainingSeries` cascade already relies
on (one `RunExclusiveAsync` call covers the primary and every cascaded sibling).

**Rule 1 - cancel wins the lock.** The booking becomes `Cancelled`; `CheckedInAtUtc` stays `null`. A
check-in attempt after that re-reads the booking inside ITS OWN lock acquisition (which could not even
begin until cancel's transaction fully committed), sees `Status != Confirmed`, and is refused with the
already-existing `Booking.CheckInNotAllowed` conflict (`CheckInEligibilityChecker`'s `NotConfirmed` case) -
no new rule was needed on the check-in side for this ordering, only the shared lock itself.

**Rule 2 - check-in wins the lock.** `CheckedInAtUtc` is set; the booking stays `Confirmed`. A plain
member's subsequent cancel attempt is refused with a new conflict, `Booking.AlreadyCheckedIn` (409) -
**no admin override exists for this rule**, confirmed directly from `CancelBookingCommandHandler`'s own
code: the `TenantAdmin` bypass only ever widens WHO may act (the ownership check), never WHICH
states are eligible to cancel - the pre-existing `CancellableStatuses` check already applies unconditionally
to every caller including admins (a `TenantAdmin` could never cancel a `Completed` booking either), and the
new `CheckedInAtUtc is not null` check follows that identical, already-established precedent rather than
inventing a new one. `CheckedInAtUtc` is never cleared or overwritten by any Cancel code path - a
checked-in booking's attendance record is permanent once set.

**Recurring cascade, atomic boundary.** A `CancelRemainingSeries` cascade's per-sibling loop treats an
already-checked-in sibling exactly like it already treats an already-`Cancelled`/`Rejected`/`Completed`
sibling: silently excluded from the cascade (never retroactively cancelled), never a thrown exception. The
PRIMARY target's own `CheckedInAtUtc is not null` check (the only place this rule can actually throw) runs
**before any mutation at all** - so there is no scenario where the loop cancels some siblings, then
"discovers" a problem and aborts, leaving an arbitrary partial state; the whole operation's one atomic
boundary remains exactly what it always was: `INotificationOutboxWriter.EnqueueManyAsync`'s single
`SaveChangesAsync`, now running inside `IResourceBookingLock`'s transaction instead of a bare one. Every
occurrence this call actually cancels, and every one of their cancellation outbox rows, commit together or
none of it does - proven unchanged by `NotificationOutboxCascadeAtomicityTests` (still green after this
change) and newly by `CheckInVsCancelConcurrencyTests.CheckInOnASiblingOccurrence_RacingWithCancelRemainingSeries_NeverProducesAnInconsistentCascade`.

**Proven against real SQL Server (LocalDB, not SQLite) by `CheckInVsCancelConcurrencyTests`**: both
documented orderings run deterministically (sequential calls, each exercising the real lock/transaction
machinery) and confirm the exact rule each one claims; a genuinely concurrent race
(`ConcurrentCheckInAndCancel_NeverProduceAContradictoryFinalState`, a shared `SemaphoreSlim` start gate)
confirms the booking always ends in exactly one of the two valid states, never a hybrid, regardless of which
request the lock happens to let through first; two concurrent plain cancels on the same booking still
produce exactly one cancellation outbox row; and a `SaveChangesAsync` failure injected into check-in still
leaves neither `Status` nor `CheckedInAtUtc` persisted (Cancel's own equivalent booking-status+outbox
rollback proof already existed and remains green).

**The future no-show job must use this same lock too.** Its eventual `Confirmed -> NoShow` transition is a
THIRD concurrent writer of the same field set - omitting `IResourceBookingLock` there would reopen the
identical class of race this change just closed, between no-show processing and check-in/cancel.

### Idempotency

Calling check-in again after a successful check-in is a no-op: `CheckInEligibilityChecker.Evaluate` checks
`CheckedInAtUtc is not null` **first**, before status or window - the response reports
`AlreadyCheckedIn: true` with the **original** `CheckedInAtUtc`, never a new one, and never re-validates
the window. This means confirming an existing check-in can never fail just because the window has since
closed - proven by `CheckInBookingCommandHandlerTests.Handle_AlreadyCheckedIn_SucceedsEvenIfTheWindowHasSinceClosed`.

### Response shape and frontend behavior

`CheckInBookingResponse` carries `CheckedInAtUtc` (always set in the response, never null - either just-now
or the original instant) and `AlreadyCheckedIn` (whether this call performed a new check-in or confirmed an
existing one), alongside the same `Id/ResourceId/StartUtc/EndUtc/Quantity/Status` shape every other booking
action response already uses.

On My Bookings, `BookingRowComponent` shows a `Check in` button whenever a booking is `Confirmed` and not
yet checked in (`canCheckInBooking`, mirroring `CheckInEligibilityChecker`'s window logic client-side,
computed from `GetOwnBookingsResponseItem`'s new `CheckInWindowOpensAtUtc`/`ClosesAtUtc` fields - server-
computed from the caller's own tenant settings, never hardcoded minutes on the client) - **disabled**
outside the window, **always visible** within it so the rule ("Check-in window: HH:mm–HH:mm...") is
discoverable before it's actionable. A submitting row disables its own button only (`checkInSubmitting`
input, keyed by booking id) so multiple rows never interfere, and a failed attempt (booking cancelled/
expired/window closed since the page loaded) shows the server's own message inline, next to that row -
the same `rowErrors`-style pattern the approval queue already uses for its own per-row decision failures.
`CheckedInAtUtc` is stored and transmitted as UTC throughout; the frontend formats it (and the window
boundaries) in the viewer's own local time via the existing `toLocalDateTime` helper, never raw UTC.

**Approver/TenantAdmin check-in has no dedicated UI yet** - this task adds the backend authorization and
tests for it (see the table above), but there is no staff-facing screen today where an approver would find
a Confirmed booking to check in on someone else's behalf (the approval queue only lists `Pending`
bookings). Documented here as the next UI task, not built speculatively ahead of a concrete design.

### Index for the future no-show job

No existing index covers `Status`/`CheckedInAtUtc` together. Added (migration
`AddTenantCheckInLeadMinutesAndBookingCheckInIndex`), mirroring the reminder index's own filtered-covering
shape exactly:

```sql
CREATE INDEX [IX_Bookings_StartUtc_NoShowCandidate] ON [Bookings] ([StartUtc])
  INCLUDE ([TenantId], [UserId], [ResourceId], [EndUtc], [Quantity], [CreatedAtUtc])
  WHERE [Status] = 'Confirmed' AND [CheckedInAtUtc] IS NULL
```

An explicit `HasDatabaseName` (passed directly to `HasIndex`, not attached afterward via
`.HasDatabaseName()`) was required: EF Core keys an index builder by its property list alone, so a second
`HasIndex(booking => booking.StartUtc)` without an explicit name at the call site silently collapsed into
the *same* index object as the existing reminder index (`IX_Bookings_StartUtc`) - the first scaffolded
migration would have **dropped the reminder index** and replaced it with this one, caught before it shipped
by inspecting the generated migration and model snapshot.

**What was actually verified, honestly reported**: the index's filter predicate (`Status='Confirmed' AND
CheckedInAtUtc IS NULL`) was confirmed exactly selective as intended - a direct `COUNT(*)` against a seeded
~204,000-row `Bookings` table matched only the 5 genuine candidates. Forcing the index
(`WITH (INDEX(IX_Bookings_StartUtc_NoShowCandidate))`) produces a valid plan (`Index Scan` + `Nested Loops`
+ `Clustered Index Seek`) at an estimated subtree cost roughly 900x lower than the plan SQL Server chose
without the hint (`0.0086` vs `7.86`). However, **the LocalDB instance used for this verification did not
naturally choose the filtered index for this query shape without the hint** - and, reproduced as a control,
neither did it naturally choose the pre-existing, already-shipped reminder index (`IX_Bookings_StartUtc`)
for the equivalent reminder-shaped query, under the same session, with `UPDATE STATISTICS ... WITH
FULLSCAN`, `OPTION (RECOMPILE)`, and a cleared plan cache all ruled out as explanations. This reads as an
environment-specific optimizer behavior in this LocalDB installation, not a defect introduced by this
index - but it is reported exactly as observed rather than overstated as "Index Seek confirmed," consistent
with this project's standard for query-plan evidence. The index is kept because it is correctly defined,
highly selective, and demonstrably much cheaper when used; a real SQL Server deployment (or a future
re-verification once the no-show job's actual query shape exists) should be re-checked before relying on
this index's natural selection in production.

### Tests

`CheckInEligibilityCheckerTests` (pure, no mocks): both window boundaries inclusive, one second outside
each boundary, every non-`Confirmed` status, already-checked-in short-circuiting the window, per-tenant
`CheckInLeadMinutes`/`NoShowGraceMinutes` actually respected (not hardcoded). `CheckInBookingCommandHandlerTests`
(mocked repositories, `PassThroughResourceBookingLock`): owner success, unknown booking, another member's
booking, assigned/unassigned approver, `TenantAdmin` bypass, idempotent repeat, already-checked-in
surviving a closed window, every non-`Confirmed` status, too-early, too-late, cross-tenant. `CheckInConcurrencyTests`
(real LocalDB): two genuinely concurrent check-ins on one booking, assigned-vs-unassigned approver against
real data. `BookingsEndpointsTests` (SQLite, full HTTP + auth pipeline): success, repeat-is-idempotent,
too-early, too-late, `Pending` booking, another user's booking (404), another tenant's booking (404),
unknown id (404), assigned approver (200), unassigned approver (404), `TenantAdmin` for another member's
booking (200), and cancelling an already-checked-in booking (409, `Booking.AlreadyCheckedIn`). Frontend:
`canCheckInBooking` boundary tests (both edges inclusive, one second outside each, non-`Confirmed`,
already-checked-in), `BookingRowComponent` button visibility/enabled/loading-state/emit tests (fake timers
for deterministic window-relative "now"), `mapCheckInBookingResponse` mapping.

`CancelBookingCommandHandlerTests` (mocked repositories, `PassThroughResourceBookingLock`, extended for the
check-in/cancel race): already-checked-in primary target refused with `Booking.AlreadyCheckedIn` (including
for `TenantAdmin`, proving no override), a checked-in sibling silently excluded from a
`CancelRemainingSeries` cascade without aborting the rest, cancellation-token propagation. Every pre-existing
Cancel test still passes - extended only to also stub the new pre-lock `FindResourceIdAsync` projection.
`CheckInVsCancelConcurrencyTests` (real LocalDB, the authoritative proof for this fix): cancel-wins and
check-in-wins run deterministically to their documented final states; a genuinely concurrent race
(`SemaphoreSlim` start gate) proves no contradictory final state regardless of which request the lock lets
through first; two concurrent plain cancels still produce exactly one outbox row; check-in racing a
`CancelRemainingSeries` cascade on a sibling occurrence produces no partial/inconsistent cascade; a
`SaveChangesAsync` failure during check-in leaves neither `Status` nor `CheckedInAtUtc` persisted; an
already-cancelled token propagates as `OperationCanceledException`, never a business conflict.
`NotificationOutboxCascadeAtomicityTests`'s existing Cancel-cascade atomicity tests were updated only to
construct the handler with the new lock argument and remain green.

## 13. No-show processing

The background counterpart to §12's check-in: `NoShowProcessor` (`BookSpace.Infrastructure.Persistence`,
see `docs/background-jobs.md` for the full orchestration/cycle/notification/query-plan design) finds
`Confirmed` bookings nobody checked in once their deadline has passed, marks them `NoShow`, and releases
their capacity - the system's own answer to "what happens if nobody ever checks in."

**Definition**: `Status == Confirmed`, `CheckedInAtUtc == null`, and
`DateTimeOffset.UtcNow > StartUtc + Tenant.NoShowGraceMinutes` - the SAME `NoShowGraceMinutes` column §12's
`CheckInEligibilityChecker` already uses for the check-in window's own closing edge. The boundary is
strictly `>`, never `>=`: at the exact deadline instant, check-in is still allowed (§12's own boundary is
inclusive, `nowUtc <= deadline`), so no-show must never be able to win that same instant - the two rules
share one boundary, and that boundary belongs to check-in until the instant it doesn't. `NoShowGraceMinutes`
is the sole source of truth for this cutoff; no second, independent grace-period setting was introduced (see
`docs/background-jobs.md`, "No-show job - why there is no new grace-period setting").

**Why all three flows - check-in, cancel, and no-show - share one resource lock.** §12 already established
that `CheckInBookingCommandHandler` and `CancelBookingCommandHandler` share `IResourceBookingLock` to
coordinate two concurrent WRITERS of the same `Booking` row. `NoShowProcessor` is the THIRD writer of that
same row (`Status`, implicitly `CheckedInAtUtc` by virtue of checking it), so it joins the identical
protocol rather than inventing a fourth, separate one: pre-lock `FindResourceIdAsync`-style projection (here,
the reader's own untracked candidate), `RunExclusiveAsync(candidate.ResourceId, ...)`, then a tracked,
genuinely fresh re-read of both the `Booking` row AND `Tenant.NoShowGraceMinutes` - never trusting the
pre-lock candidate for either. All three pairwise race orderings resolve cleanly, proven against real
LocalDB (`NoShowVsCheckInVsCancelConcurrencyTests`):

- **Check-in wins** -> `CheckedInAtUtc` is set; no-show's re-read sees it and skips (the `CheckedIn`
  outcome) - never marked, never notified.
- **No-show wins** -> `Status` becomes `NoShow`; a subsequent check-in attempt is refused by the
  ALREADY-EXISTING `Booking.CheckInNotAllowed` conflict (`Status != Confirmed`) - no new rule needed.
- **Cancel wins** -> `Status` becomes `Cancelled`; no-show's re-read sees it and skips (`AlreadyCompleted`).
  The reverse order (no-show wins first) leaves a subsequent cancel refused by the ALREADY-EXISTING
  `Booking.CancellationNotAllowed` conflict (`NoShow` isn't in `CancellableStatuses`) - again, no new rule.

A genuinely concurrent race (shared `SemaphoreSlim` start gate, not just a sequential ordering) between
check-in and no-show, and separately between cancel and no-show, confirms the booking always ends in exactly
one of the two valid states - never `NoShow` while also bearing a `CheckedInAtUtc` value. **Any future
mutator of `Booking.Status`/`CheckedInAtUtc` (a manual admin correction tool, say) must join this same lock
or it reopens the exact class of race this protocol exists to close.**

**Atomic boundary**: identical shape to every other lock-protected transition in this document - the
`Status = NoShow` mutation and the `Booking.NoShow` notification enqueue commit together inside the lock's
one transaction (`INotificationOutboxWriter.EnqueueAsync`'s own `SaveChangesAsync`), or neither does. Proven
via the same `SaveChangesInterceptor` failure-injection technique used throughout this codebase.

**Recurring bookings**: each occurrence is an independent `Booking` row with its own `StartUtc`/
`CheckedInAtUtc` - marking one occurrence `NoShow` never touches a sibling occurrence in the same series,
and there is no cascade (a no-show on one occurrence says nothing about a member's intent for any other).

**Capacity release**: `BookingAvailabilityRepository.ActiveStatuses` is `[Pending, Confirmed]` - `NoShow`
was never added to it, so capacity releases the instant `Status` flips, with zero additional code. Proven
for a booking marked PARTWAY through its own window (e.g. a 10:00-12:00 booking processed at 10:15): the
already-elapsed portion cannot be un-elapsed, but the remainder of the window - and the resource's capacity
for the rest of the overlap - becomes bookable again immediately.

**`IgnoreQueryFilters()` and `TenantId` retention**: `NoShowReader`'s due query is a system-wide background
read with no HTTP request/JWT claim to scope it by - the same narrow, already-established exception to the
global tenant filter `NotificationOutboxReader`/`BookingReminderReader`/`StaleApprovalReader` already use.
Every projected `DueNoShowBooking` row keeps its own `TenantId` explicitly, and `NoShowProcessor`'s
per-candidate re-read re-applies an exact `TenantId` + `Id` match under `IgnoreQueryFilters()` rather than
ever trusting that the bypass alone is safe - a row can never be acted on under the wrong tenant's identity.

**Self-check-in's limits still apply**: see §12, "Why check-in is useful even though it isn't proof of
physical presence" - no-show processing inherits the exact same limitation, since it is simply the absence
of that same self-reported signal. QR/NFC/geolocation-based verification remains out of scope.

**The SMTP crash window is unchanged, not newly introduced**: as with every other notification type in this
system (`docs/background-jobs.md`, "Per-item processing and retry"), a crash between a successful send and
marking the outbox item `Sent` could theoretically cause a member to receive the same `Booking.NoShow` email
twice - never zero times silently dropped, and never a duplicate `Booking` status transition or a duplicate
outbox row (those two guarantees are what this task's atomicity/idempotency actually cover; physical,
exactly-once email delivery was never a guarantee any part of this system makes).

See `docs/background-jobs.md` ("No-show job") for configuration, the due-query/index/query-plan evidence,
the notification/email template, cycle ordering, and the full test inventory.
