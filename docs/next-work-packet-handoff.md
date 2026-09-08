# Before Implementing the Next Work Packet

## Booking Work Packet — completed

This document originally prepped the Booking Work Packet (single-user creation, then concurrency-safe
capacity enforcement) against the architecture that existed at the time. That work is now done - see
[bookings-and-concurrency.md](bookings-and-concurrency.md) for what was actually built, and
[open-questions.md](open-questions.md) for what it deliberately left open (TenantAdmin cancellation of
another user's booking, `RequiresApproval`/approval-workflow wiring, idempotency keys). The original
preparation notes are kept below, unedited, as a record of what was checked before implementation began -
every invariant listed was in fact preserved; see `bookings-and-concurrency.md` §7-§9 for how.

## Recurrence/Approvals Work Packet — completed

The `RequiresApproval`/approval-workflow wiring the Booking Work Packet deliberately left open is now
also done, alongside recurring bookings - see
[recurring-bookings-and-approvals.md](recurring-bookings-and-approvals.md). Two things worth knowing
before touching this area again:

- **A real concurrency bug was found and fixed during this work**, not merely avoided: a naive "read
  before the lock, read again after acquiring it" pattern in `ApproveBookingCommandHandler` looked
  correct but wasn't - EF Core's change tracker (identity map) silently returned the same stale tracked
  `Booking` instance on the second read instead of the row's current state, because both reads ran on the
  same `DbContext`. See [recurring-bookings-and-approvals.md](recurring-bookings-and-approvals.md) §8 -
  **any future handler that needs to resolve a lock key from an entity, then reload that same entity
  after acquiring the lock, must make the pre-lock read a projection, not a tracked load, or it will hit
  the identical trap.**
- The existing capacity-reduction concurrency fix (commit `186ae66`,
  `UpdateResourceCommandHandler`/`IResourceBookingLock`) was inspected and confirmed to need no changes -
  see [recurring-bookings-and-approvals.md](recurring-bookings-and-approvals.md) §9.

## What is already stable and should not be unnecessarily rewritten

- The mediator/validation/error-handling pipeline (see [architecture.md](architecture.md)) - a new
  Booking vertical should follow the exact same file-per-request, verb-grouped convention as Resources.
- The tenant isolation mechanism (fail-closed global query filter) - a new `Booking`-owned entity
  already implements `ITenantOwned` and gets this for free; do not add a parallel scoping mechanism.
- The `IBookingAvailabilityRepository` read path and `IntervalMath` calculator - both already correctly
  handle the booking-status filtering and interval math a Booking-create flow will need to *read*
  against; a Booking-create handler should reuse them for its own pre-check, not reinvent equivalent
  logic.
- The `SaveChangesHandlingConflictsAsync` / `ConflictException` / `409` pattern - reuse it rather than
  inventing a new conflict-reporting shape for booking-creation races.

## Architectural invariants that must be preserved

- **Tenant isolation**: `TenantId` on a new `Booking` row must be server-derived from
  `ICurrentUserContext`, never client-supplied. The parent `Resource` (and any `RecurringSeries`) must
  be loaded through a tenant-filtered lookup before being referenced - see the checklist in
  [tenant-isolation.md](tenant-isolation.md).
- **ResourceStatus gating**: a Booking-create handler must reject (or otherwise account for) an
  Archived, Inactive, or Maintenance resource - see the matrix in
  [resource-lifecycle-and-capacity.md](resource-lifecycle-and-capacity.md). The availability query
  already reports zero `BookableSlots` for any non-Active status; Booking-create must not bypass that
  by, say, allowing a request that never checked availability first.
- **UTC storage**: `Booking.StartUtc`/`EndUtc` must be absolute UTC, matching every other timestamp in
  this schema - never store a local time directly.
- **Resource timezone / wall-clock availability**: if Booking-create accepts a *local* time from a
  client (plausible for a UI that shows a resource's schedule in its own timezone), it must convert
  through the same `ConvertLocalToUtc` logic and DST policy documented in
  [availability-and-timezones.md](availability-and-timezones.md) - do not write a second, divergent
  conversion path.
- **Blackout conflicts**: a new booking must be checked against `BlackoutPeriod` the same way the
  availability query already does (full capacity consumption for the blackout's duration), not treated
  as a separate, unrelated concern.

## Deferred risks that must be considered when Booking is introduced

- **Availability is a read model, not a concurrency guarantee** - see the "Booking Concurrency" open
  question in [open-questions.md](open-questions.md). This must be answered and designed *before*
  writing the create handler, not discovered after a race condition ships.
- **Capacity invariant symmetry**: the existing capacity-reduction check on `UpdateResource` (see
  [resource-lifecycle-and-capacity.md](resource-lifecycle-and-capacity.md)) only protects against
  capacity being reduced below existing bookings - it says nothing about a *new* booking being created
  that would itself exceed capacity. That's Booking-create's own responsibility to check, using the
  same sweep-line/status-filtering logic (or `IntervalMath.ComputeAvailableCapacity` directly).
- **Approval semantics are undecided** - see the "Approval Semantics" open question. Do not hardcode an
  assumption about what Pending means for capacity beyond what's already documented as a placeholder.
- **Idempotency** is unresolved - decide it as part of the API design, not as an afterthought once
  duplicate-booking bug reports arrive.
- **Cancellation semantics** (who can cancel, does cancelling release capacity immediately, is there a
  cancellation-reason/audit requirement) have no precedent in this codebase to follow - `Booking` has
  `CancelledAtUtc`/`CancelledByUserId`/`CancellationReason` columns already in the schema from WP-1, but
  no handler populates them yet, so there is no existing pattern to match, only a schema shape to
  respect.
- **Transaction boundaries**: decide explicitly whether a booking-creation write needs to happen inside
  a single transaction alongside its capacity re-check (almost certainly yes, per the open concurrency
  question) - the current codebase has no precedent for an explicit multi-step transaction, since every
  existing write path is a single load-mutate-save sequence.

## Regression protection - keep these green

Current counts, after the Recurrence/Approvals Work Packet (previously 207/92/24 after Booking; 165/79/21
before that):

- `BookSpace.Application.Tests`: 271 tests. Adds `RecurringOccurrenceGeneratorTests` (pure, DST-critical),
  `CreateRecurringSeriesCommandHandlerTests`, `Approve`/`RejectBookingCommandHandlerTests`,
  `GetPendingApprovalsQueryHandlerTests`, `GetRecurringSeriesQueryHandlerTests`, and cascade-cancellation
  cases added to `CancelBookingCommandHandlerTests`, on top of everything from the Booking Work Packet.
- `BookSpace.Api.Tests`: 105 tests, SQLite-backed. Adds `RecurringSeriesAndApprovalEndpointsTests` (series
  creation with/without conflicts, cascade cancellation, `RequiresApproval` over the wire, the
  pending-approval queue, approve/reject, RBAC, cross-tenant 404s).
- `BookSpace.Infrastructure.Tests`: 30 tests, real-LocalDB-backed. Adds `UpdateResourceCapacityConcurrencyTests`
  (the fix for commit `186ae66`) and `ApprovalConcurrencyTests` (the two tests that caught and then
  proved the fix for the identity-map bug in §8 above), on top of the existing `BookingConcurrencyTests`.

Any future work packet touching Bookings, RecurringSeries, Approvals, Resources, or availability should
extend these suites deliberately, not silently break them.
