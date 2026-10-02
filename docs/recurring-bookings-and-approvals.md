# Recurring Bookings and Approvals

What WP-5 built on top of the `RecurringSeries`/`ApprovalRequest`/`ResourceApprover` schema that existed
since WP-1 with zero Application-layer logic, and on top of the single-booking flow from
[bookings-and-concurrency.md](bookings-and-concurrency.md), which this document assumes throughout.

## 1. Recurrence storage model

Hybrid, as scoped: `RecurringSeries` stores the recurrence *rule*; every occurrence it produces is
materialized as a real `Booking` row (`SeriesId` FK, already existed) with its own `Id`, independently
viewable, cancellable, and status-aware. All valid occurrences are generated and evaluated at series-
creation time - there is no lazy/virtual occurrence computed later.

**Why hybrid, not rule-only or full pre-materialization-without-limits**: the requirements explicitly
need every occurrence independently viewable/cancellable/approvable, and conflicts must be surfaced *at
creation*, which requires evaluating every candidate occurrence's eligibility up front regardless of
storage strategy. A two-year daily series is ~730 rows - trivial for this schema (the existing `Booking`
indexes already cover it); there's no volume reason to avoid materializing. Rule-only storage would need
a second, on-the-fly occurrence-expansion code path at every read (list, cancel, approve), duplicating
exactly the generation logic already needed at creation time - hybrid needs only one.

**Schema change required**: `RecurringSeries.StartUtc`/`EndUtc` (fixed UTC instants, the original WP-1
shape) cannot express "9am local every day" in a DST-safe way - `AddDays`/`AddMonths` on a UTC instant
does not re-resolve local wall-clock time per occurrence. Replaced with a wall-clock shape mirroring
`AvailabilityRule`'s existing convention: `StartDate`/`StartTime`/`EndTime` (local, resolved to UTC
per-occurrence-date), `EndDate?`/`OccurrenceCount?` (exactly one, the end condition), `Quantity`.
`TimeZoneId` is a snapshot of `Resource.TimeZoneId` at series-creation time, never client-suppliable, so
a later Resource timezone change never retroactively reinterprets already-generated occurrences.
Migration: `RedesignRecurringSeriesAndNullableApproverId`.

**Occurrence cap**: `RecurringOccurrenceGenerator.MaxOccurrences = 750` (the task's own "2 years daily"
reference ceiling, rounded up). Enforced twice: `CreateRecurringSeriesCommandRequestValidator` rejects a
request whose parameters would generate more than this before any DB work happens (computed from pure
date arithmetic on the request's own fields - `RecurrenceFrequency`/`Interval`/date range - no repository
call, matching this codebase's shape-only validator convention), and the generator's own date-production
loop hard-stops at the same number regardless, as a backstop independent of validation.

## 2. Monthly recurrence semantics

Same day-of-month as `StartDate`, computed **independently for every occurrence from the original
`StartDate` anchor** (`StartDate.AddMonths(occurrenceIndex * Interval)`), never chained from the previous
occurrence's date. `DateOnly.AddMonths` already clamps to the target month's last valid day when the
anchor day doesn't exist there (Jan 31 -> Feb 28/29) - no custom clamping code needed. Computing from the
fixed anchor (not chaining) matters specifically here: it's what makes March return to the 31st after
February's forced clamp to the 28th, instead of permanently drifting to the 28th for every later month.
Proven by `RecurringOccurrenceGeneratorTests.GenerateCandidateDates_MonthlyAfterAClampedMonth_ReturnsToTheOriginalDayOfMonth`.

Only `Daily`/`Weekly`/`Monthly` are accepted (validator-enforced). `RecurrenceFrequency.Yearly` exists in
the enum (inherited from WP-1) but is deliberately not wired up - not a requirement here, and adding it
speculatively would be scope creep.

## 3. Occurrence conflict handling at series creation

Every candidate date is evaluated - none are ever silently dropped. For each: resolve the local
`(StartTime, EndTime)` to a UTC window (§5 below); if that fails (spring-forward gap), record a
`NonexistentLocalTime` conflict for that date and move on. If it resolves, run it through
`BookingEligibilityChecker` (the exact same status/availability/blackout/capacity check
`CreateBookingCommandHandler` and `ApproveBookingCommandHandler` use); a rejection records that
occurrence's specific `ErrorCode` as its conflict reason and moves on; success materializes a `Booking`
row (`Confirmed`, or `Pending` + its own `ApprovalRequest` if `Resource.RequiresApproval`).

The response (`CreateRecurringSeriesResponse`) always reports `RequestedOccurrenceCount` alongside
`CreatedOccurrences` and `Conflicts` (each conflict paired with its `Date` and machine-readable
`Reason`) - `CreatedOccurrences.Count + Conflicts.Count` always equals `RequestedOccurrenceCount`. If
**zero** occurrences are eligible, no `RecurringSeries` row is created at all
(`RecurringSeries.NoValidOccurrences`, 409) rather than leaving a pointless empty series behind.

Rules/blackouts for the resource are fetched **once**, before the per-occurrence loop, and reused for
every candidate occurrence - not re-fetched per occurrence. For a long series this avoids hundreds of
redundant, identical round-trips for data that cannot change mid-loop (the whole operation runs inside
one `IResourceBookingLock` acquisition - see §7). Occurrences within a single series never overlap each
other in time by construction (distinct calendar dates, `EndTime > StartTime` same-day, validator-
enforced), so accumulated-but-not-yet-saved same-series occurrences never need to be counted against each
other's own capacity check - only already-committed rows from other bookings/series matter, and those are
still re-read fresh per occurrence via `IBookingAvailabilityRepository`.

## 4. Approval workflow

`Pending -> Approved` and `Pending -> Rejected`. `Resource.RequiresApproval = true` at booking (or
per-occurrence series) creation time produces a `Pending` `Booking` plus its own `ApprovalRequest`
(`ApproverId = null`, `RequestedAtUtc = now`, `ExpiresAtUtc = now + Tenant.ApprovalExpiryHours`) instead
of going straight to `Confirmed` - the one prior work packet's docs explicitly deferred this decision to
"the Approval Work Packet." A resource with `RequiresApproval = true` but zero configured
`ResourceApprover`s rejects the booking/occurrence outright (`Booking.NoApproverConfigured`) rather than
silently confirming it (defeats the flag) or creating a `Pending` row nobody could ever approve.

**Who may decide**: any `ResourceApprover` for that specific resource, or `TenantAdmin`/`SysAdmin`
(the same administrative-override role this codebase already gives them over resource management
generally). `ApprovalRequest.ApproverId` is nullable and populated only at decision time with whoever
actually acted - not pre-assigned at request time, since any of potentially several approvers may act on
it. `ApprovalAuthorization.EnsureCallerCanDecideAsync` is the one shared check both
`ApproveBookingCommandHandler` and `RejectBookingCommandHandler` use. A caller who fails this check
already knows the booking exists (RBAC already let them reach the handler, and they hold the `Approver`
role) - this isn't a cross-tenant/cross-user existence leak the way plain `NotFoundException` protects
elsewhere, so a distinguishable `Booking.ApprovalForbidden` conflict is more honest than a fake 404.

Approve/Reject only ever transition **from** `Pending` - anything else (`Confirmed`, `Rejected`,
`Cancelled`, ...) is `Booking.ApprovalNotAllowed`, not idempotent (unlike Cancel): re-deciding an
already-decided booking is always a genuine state error, never a legitimate retry.

## 5. Availability re-check at approval time

Availability MUST be re-checked at approval time, never trusted from creation time. `ApproveBookingCommandHandler`
re-runs the exact same `BookingEligibilityChecker` sequence `CreateBookingCommandHandler` uses (status,
availability, blackout, capacity), excluding the booking's own row from the capacity sum (it's already
counted as active from `Pending` onward - see §6). If eligibility no longer holds, this throws *before
any write* - the booking stays exactly `Pending`, never partially approved. `RejectBookingCommandHandler`
needs no re-check at all: moving a booking **out** of `Pending` can only reduce demand, never invalidate
anything. This is not the same as needing no lock - Reject still acquires `IResourceBookingLock` for
Approve-vs-Reject serialization, just not for a capacity re-check (see §7).

**What can actually make a previously-eligible `Pending` booking ineligible by approval time?** Given
`Pending` already fully reserves capacity identically to `Confirmed` from the moment of creation (§6),
a second booking racing to fill the same window can never be created in the first place - it would fail
its own creation-time capacity check. The re-check exists for genuine *external* changes made after the
booking was created: the resource's status changing to non-`Active`, or (most concretely, and the one
proven end-to-end) a new blackout being added over the booking's window - proven by
`ApproveBookingCommandHandlerTests.Handle_WhenABlackoutWasAddedAfterCreationMakingTheSlotNoLongerAvailable_ThrowsConflictExceptionAndLeavesBookingPending`.
"Another booking takes the slot" in the literal sense the task describes cannot happen under this
codebase's existing capacity model without also weakening it - see §9 for why that model was preserved
rather than reinterpreted.

## 6. Which statuses count toward capacity

Unchanged from the pre-existing rule (`BookingAvailabilityRepository.ActiveStatuses`): `Pending` and
`Confirmed` both count identically; `Rejected`/`Cancelled`/`Completed`/`NoShow` never do. This work packet
did not revisit that decision - it's exactly what makes the capacity invariant hold continuously through
a booking's Pending phase, not just after approval.

## 7. Concurrency boundary: which operations use `IResourceBookingLock`, and why

| Operation | Uses the lock? | Why |
|---|---|---|
| `CreateBookingCommandHandler` | Yes | Check-then-insert must be atomic against concurrent creates for the same resource (WP-4). |
| `CreateRecurringSeriesCommandHandler` | Yes (once, for the whole series) | Same reasoning, applied per-occurrence inside one lock acquisition - see §3. |
| `UpdateResourceCommandHandler` (capacity reduction) | Yes | See §9 - closes a real race with concurrent booking creation. |
| `ApproveBookingCommandHandler` | Yes | The re-check (§5) and the `Pending -> Confirmed` write must be atomic - see §8 for the subtle bug this surfaced. |
| `RejectBookingCommandHandler` | Yes | Not for capacity (rejecting only reduces demand) - to serialize against a concurrent `Approve` decision on the same booking. `Booking` has no `RowVersion`, so without the shared lock two racing Approve/Reject calls could both "succeed," leaving `Booking.Status`/`ApprovalRequest.Status` inconsistent. |
| `CancelBookingCommandHandler` (single or cascade) | No | Same reasoning as Reject - removing bookings only ever reduces demand, series-wide or not. |

No new locking mechanism was introduced. Every locked operation uses the exact same
`IResourceBookingLock.RunExclusiveAsync(resourceId, ...)` boundary from WP-4 (transaction-scoped
`UPDLOCK`/`HOLDLOCK` on the target `Resources` row) - see [bookings-and-concurrency.md](bookings-and-concurrency.md)
§7 for the full mechanism and why it was chosen over `sp_getapplock`/`SERIALIZABLE`.

## 8. A real concurrency bug this work surfaced: EF Core's identity map defeats a naive "re-read under the lock"

`ApproveBookingCommandHandler` needs to know a booking's `ResourceId` *before* it can call
`RunExclusiveAsync(resourceId, ...)` (the lock needs to know which resource to lock, but resolving that
requires loading the booking first) - a chicken-and-egg problem `CreateBookingCommandHandler` and
`UpdateResourceCommandHandler` never have, since neither loads anything before entering its lock.

The first implementation did the obvious thing: load the `Booking` via `IBookingRepository.FindByIdAsync`
before the lock (to get `ResourceId` and authorize the caller), then load it again via the same method
*inside* the lock, intending a genuinely fresh read. **This does not work.** Both calls run against the
same `DbContext` instance (the same scoped instance every repository call in one request shares). EF
Core's change tracker is an identity map: once an entity with a given key is tracked, a later query for
that same key - even a real SQL query that would return updated column values - returns the **existing
tracked instance**, discarding the freshly-queried row's values rather than refreshing them. The second
"fresh" read silently wasn't fresh at all.

This was caught, not assumed: `BookSpace.Infrastructure.Tests.ApprovalConcurrencyTests.TwoConcurrentApproveRequestsForTheSameBooking_ExactlyOneSucceedsAndTheOtherIsRejectedCleanly`
failed against the first implementation with **both** concurrent approve calls succeeding - the second
call's "fresh" read returned the first call's already-tracked (but, at read time, still `Pending`)
instance instead of the first call's *committed* `Confirmed` state.

**Fix**: `IBookingRepository.FindResourceIdAsync(bookingId, ct)` - a `.Select(...)` projection, not an
entity load. EF Core never adds a projected scalar result to the change tracker, so the pre-lock step
that only needs `ResourceId` (for locking) and nothing else (authorization only needs `ResourceId` too)
can no longer collide with the real, tracked, genuinely-fresh `FindByIdAsync` call made once the lock is
held. After the fix, the same test passes deterministically (verified across 5 repeated runs).

**Why this matters beyond this one handler**: any future handler that needs to inspect an entity before
deciding what to lock, then mutate that same entity after acquiring the lock, will hit the identical trap
if both reads go through the same tracking repository method on the same `DbContext`. The rule going
forward: a pre-lock read that exists only to resolve a lock key or do a lock-independent check must be a
projection (or otherwise untracked), never a full tracked entity load of the same type/key the
lock-protected code will reload later.

## 9. Why the existing capacity-reduction fix (commit `186ae66`) was preserved unchanged

This work packet's own instructions explicitly required not weakening or redesigning it, and inspection
confirmed no genuine conflict: `UpdateResourceCommandHandler`'s `IResourceBookingLock` usage and
`EnsureCapacityCoversExistingBookingsAsync`'s "why not RowVersion" reasoning apply identically regardless
of whether the demand being protected comes from one-off bookings or recurring-series occurrences (both
are just `Booking` rows with the same `Pending`/`Confirmed` semantics) - nothing about recurrence or
approval required touching that fix. See [bookings-and-concurrency.md](bookings-and-concurrency.md) for
the full writeup, including why `RowVersion` alone cannot protect this cross-table invariant (it only
detects a competing write to the `Resources` row itself; a `Booking` insert never touches `Resources` at
all).

## 10. UTC storage, resource timezone, DST policy

Unchanged invariants, reused exactly: `Booking.StartUtc`/`EndUtc` are always absolute UTC;
`RecurringSeries.StartDate`/`StartTime`/`EndTime` are wall-clock, interpreted in the **resource's**
timezone (`Resource.TimeZoneId`, snapshotted onto the series at creation - never the booker's own
timezone, and never client-suppliable). `AvailabilityCalculator.ConvertLocalToUtc` (WP-4, unchanged) is
still exactly what `BookingEligibilityChecker` uses to compute open periods for the eligibility check.

**`RecurringOccurrenceGenerator` deliberately does NOT reuse `ConvertLocalToUtc`** - recurrence
generation needs a genuinely different DST policy than continuous availability-window definition does,
and the two are kept as two clearly-named, separately-tested functions rather than one shared function
with divergent behavior hidden behind a flag:

- **`AvailabilityCalculator.ConvertLocalToUtc`** (open-hours definition): spring-forward gap ->
  silently normalizes forward past the gap; fall-back ambiguity -> resolves to the standard/later offset
  (.NET's own documented default for `ConvertTimeToUtc`). Appropriate here because this defines
  *continuous* open hours, not a specific claimed instant - a small shift in exactly when "open" begins
  is low-stakes.
- **`RecurringOccurrenceGenerator.TryResolveOccurrence`** (discrete occurrence generation):
  spring-forward gap -> **never shifts** - returns a `NonexistentLocalTime` conflict for that one
  occurrence, surfaced to the caller like any other conflict reason (§3), never silently rescheduled or
  duplicated. Fall-back ambiguity -> resolves **deterministically to the earlier** of the two valid UTC
  instants, via `TimeZoneInfo.IsAmbiguousTime`/`GetAmbiguousTimeOffsets` (picking the larger of the two
  possible offsets, since `UTC = local - offset` means a larger offset yields an earlier UTC instant -
  the daylight, pre-transition occurrence). Never both, never a coin flip - proven deterministic by
  `RecurringOccurrenceGeneratorTests.TryResolveOccurrence_ForTheSameAmbiguousLocalTime_AlwaysResolvesToTheSameSingleInstant_NeverTwo`.

**Why the two policies differ**: a recurring occurrence is a discrete calendar commitment a real person
is claiming for a real purpose - silently relocating someone's meeting to a different hour because of a
DST quirk is a materially worse outcome than refusing that one date's occurrence and telling the caller
exactly why (`NonexistentLocalTime`), which is a real value they can act on (pick a different time,
accept the gap, or handle it manually). This is a deliberate, documented divergence, not the "second,
divergent conversion path" WP-4's docs warn against - that warning is about *accidentally* reimplementing
the same concept differently; this is *intentionally* solving a different concept (open-hours definition
vs. one-off-in-a-series occurrence creation) correctly for each.

## 11. Cancellation semantics

**One occurrence** (`DELETE /api/bookings/{id}`, `CancelRemainingSeries` omitted or `false`): only that
`Booking` row is cancelled - identical to a one-off booking's cancellation (WP-4). Other occurrences in
the same series are never touched.

**Remaining series** (`DELETE /api/bookings/{id}?cancelRemainingSeries=true`): cancels the target occurrence
**and** every other occurrence in the same series with `StartUtc >= ` the target's own `StartUtc`, that
is still in a cancellable status (`Pending` or `Confirmed`). Earlier occurrences (`StartUtc` before the
target) are never touched, regardless of their status. Occurrences already `Completed`, `NoShow`,
`Rejected`, or already `Cancelled` are never touched either - "remaining" means "not yet resolved," not
"every future row regardless of what already happened to it." The response's `CascadedOccurrenceIds`
lists every occurrence affected *besides* the one named in the URL, so a client is always told exactly
what else happened, never left to infer it from a second query. A one-off booking (`SeriesId = null`)
silently ignores `cancelRemainingSeries=true` - there is no "remaining series" for a booking that was
never part of one, and rejecting the flag as an error would be a pointless extra failure mode for no
benefit.

This extends the *existing* `DELETE /api/bookings/{id}` endpoint/handler rather than adding a new route -
API-shape decision confirmed directly rather than assumed.

## 12. Blackout-after-series behavior

Creating or updating a `BlackoutPeriod` is **never blocked** by bookings (one-off or series occurrences)
it now overlaps, but the response always reports which currently-active bookings conflict with it
(`ConflictingBookingIds` on both `CreateBlackoutPeriodResponse` and `UpdateBlackoutPeriodResponse`) -
nothing is ever silently lost.

**Why not block creation**: blackout creation itself never force-cancels a conflicting booking
automatically - blocking blackout creation whenever it overlaps an existing booking would create a dead
end (a TenantAdmin needing to declare "the building has no power next Tuesday" could never do so if
anyone already had so much as a 5-minute booking that day, with no way to clear it). Surfacing the
conflict and letting a human decide what to do about it is the only option that doesn't create that dead
end. A TenantAdmin/SysAdmin can now follow up by manually cancelling the conflicting bookings listed in
`ConflictingBookingIds` (`CancelBookingCommandHandler` - see
[bookings-and-concurrency.md](bookings-and-concurrency.md#2-ownership-and-tenant-isolation)), but that's a
separate, deliberate action, not something blackout creation does on its own; whether and how the
affected owner is actively notified of either the blackout or a resulting cancellation remains open (see
[open-questions.md](open-questions.md#tenantadmin-cancellation-of-another-users-booking--interim-implementation-notification-still-open)).

Since every occurrence is materialized at series-creation time (§1), there is no "future occurrence not
yet materialized" case to treat differently from an ordinary one-off booking - the exact same overlap
check (`IBookingAvailabilityRepository.GetActiveBookingsAsync` against the blackout's window) already
finds every affected row, series or not.

## 13. Tenant isolation

No new mechanism. `RecurringSeries` and `ApprovalRequest` already implemented `ITenantOwned` (WP-1) - the
existing fail-closed global query filter ([tenant-isolation.md](tenant-isolation.md)) scopes every new
repository call automatically. `TenantId`/`UserId` on a new `RecurringSeries` are always server-derived
from `ICurrentUserContext`, never client-supplied. `GetRecurringSeriesQueryHandler` uses the exact same
"NotFound, not Forbidden" convention as booking cancellation - a series belonging to another user (or
tenant) looks identical to one that doesn't exist. `GetPendingApprovalsQueryHandler` additionally scopes
a plain `Approver`'s results to only the resources they're assigned to (`IResourceApproverRepository.GetResourceIdsByUserAsync`) -
`TenantAdmin`/`SysAdmin` see every `Pending` booking in their own tenant (never another tenant's, via the
same global filter), with no per-resource restriction.

## 13a. `ResourceApprover` eligibility is live-checked, never baked into the token

`ApprovalAuthorization.EnsureCallerCanDecideAsync` reads the caller's `ResourceApprover` assignment (and
their current global roles) fresh from the database on every approve/reject call - never from anything
carried in the JWT. Two consequences, both now proven end-to-end rather than only asserted in prose
(`RecurringSeriesAndApprovalEndpointsTests.cs`):

- **Grant is immediate**: a user's access token can be issued *before* they're ever assigned as a
  `ResourceApprover` for a given resource, and the very same, still-valid token can decide an approval on
  that resource the moment an admin creates the assignment - no re-login, no new token required
  (`AssignResourceApprover_ThenApproveWithATokenIssuedBeforeTheAssignmentExisted_Succeeds`).
- **Revocation is immediate**: removing a `ResourceApprover` assignment takes effect on the very next
  request with the same, still-otherwise-valid token - `Booking.ApprovalForbidden`, not a stale grant
  persisting until the token naturally expires (`RemoveResourceApprover_ThenApproveWithTheSameToken_IsImmediatelyRejected`).

This is the one authorization concept in this codebase that behaves this way; contrast with global roles
(`Member`/`Approver`/`TenantAdmin`/`SysAdmin`), which *are* baked into the JWT at issuance and only take
effect on the session's next refresh or login - see
[user-administration.md](user-administration.md) §5's "JWT staleness" tradeoff.

## 14. Testing strategy and evidence

- **`BookSpace.Application.Tests/Bookings/RecurringOccurrenceGeneratorTests.cs`**: pure, DB-free - daily/
  weekly/monthly generation, interval handling, monthly clamping and anchor-not-chained drift avoidance,
  `EndDate`/`OccurrenceCount` end conditions, the `MaxOccurrences` backstop, and every DST case (ordinary
  time, spring-forward conflict for both `StartTime` and `EndTime`, fall-back deterministic-earlier-
  instant resolution, determinism across repeated calls, non-DST-date fixed-offset conversion).
- **`BookSpace.Application.Tests/Bookings/Create/CreateRecurringSeriesCommandHandlerTests.cs`**: all-
  eligible happy path, one-occurrence-conflicts-rest-created, zero-eligible rejection,
  `RequiresApproval` producing `Pending` occurrences with their own `ApprovalRequest`s, no-approver
  rejection, unknown resource.
- **`BookSpace.Application.Tests/Bookings/Update/{Approve,Reject}BookingCommandHandlerTests.cs`**: happy
  path, `TenantAdmin` bypassing the per-resource check, forbidden-for-an-unassigned-approver, unknown
  booking, every non-`Pending` status rejected, and - the scenario in §5 - a blackout added after
  creation causing approval to fail cleanly with the booking left exactly `Pending`.
- **`BookSpace.Api.Tests/Bookings/RecurringSeriesAndApprovalEndpointsTests.cs`**: full HTTP-pipeline
  coverage - series creation with and without conflicts, cross-tenant 404 on series create/view, cascade
  cancellation, `RequiresApproval` producing `Pending` over the wire, the pending-approval queue, approve/
  reject, RBAC (`Approver`/`TenantAdmin`/`SysAdmin` vs. plain `Member`), cross-tenant 404 on approve,
  re-approving an already-Confirmed booking.
- **`BookSpace.Infrastructure.Tests/Persistence/ApprovalConcurrencyTests.cs`** (real SQL Server LocalDB,
  required - SQLite has no `UPDLOCK`/`HOLDLOCK` support): two genuinely concurrent approve calls on the
  *same* booking (exactly one succeeds, the other gets a clean `ConflictException`, never both) - this is
  the test that caught §8's bug - and a concurrent approve-vs-create race for a fully-booked resource,
  proving the two operations serialize through the shared lock without deadlocking or corrupting the
  capacity invariant. Both stable across 5 repeated runs.

## 15. Known limitations

- No automatic `ApprovalRequest.ExpiresAtUtc`-driven expiry - nothing in this codebase transitions a
  `Pending` approval when its expiry passes (would need a background job; no scheduling infrastructure
  exists anywhere in this codebase yet). The field is populated and readable, but purely informational
  until an Approval work packet designs that lifecycle - matches the pre-existing "Approval Semantics"
  open question, unchanged.
- Same-resource booking-creation/series-creation/approval/capacity-update calls fully serialize through
  one lock regardless of whether their specific windows overlap (inherited trade-off from WP-4, §7 of
  [bookings-and-concurrency.md](bookings-and-concurrency.md) - unchanged, still acceptable at expected
  per-resource throughput).
- No idempotency-key support on series creation, same as single-booking creation (pre-existing open
  question, unchanged).

## 16. Open questions

Unchanged, still open (see [open-questions.md](open-questions.md)): **TenantAdmin cancellation of
another user's booking**, **booking idempotency keys**. **Booking Approval Workflow** is now resolved by
this work packet (§4-§5) - marked accordingly.
