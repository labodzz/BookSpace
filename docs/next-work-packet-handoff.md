# Before Implementing the Next Work Packet

Preparation for evaluating the likely next Work Packet (Booking) against the current architecture -
**not** an instruction to implement Booking now.

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

## Regression protection already in place - keep these green

- `BookSpace.Application.Tests`: 165 tests, including the full `GetResourceAvailabilityQueryHandlerTests`
  suite (DST, capacity-aware slots, resource-status gating) and `UpdateResourceCommandHandlerTests`
  (capacity sweep-line boundary cases) - a Booking-create handler will very likely change behavior these
  tests currently lock in (e.g. `BookableSlots` shape), so expect to extend them deliberately, not
  silently break them.
- `BookSpace.Api.Tests`: 79 tests, SQLite-backed, the fast integration-test net for every existing
  endpoint including tenant isolation and RBAC.
- `BookSpace.Infrastructure.Tests`: 21 tests, real-LocalDB-backed, covering exactly the scenarios
  SQLite cannot: unique-constraint conflicts, tenant-filter fail-closed behavior, refresh-token and
  Resource/BlackoutPeriod optimistic concurrency, and bounded SQL range filtering. A Booking-concurrency
  test almost certainly belongs here too, following the same throwaway-LocalDB-database pattern.
