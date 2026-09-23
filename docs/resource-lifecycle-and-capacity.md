# Resource Lifecycle and Capacity Invariants

## ResourceStatus behavior matrix

`ResourceStatus` (`backend/BookSpace.Domain/Enums/ResourceStatus.cs`) has exactly four values:
`Active`, `Inactive`, `Maintenance`, `Archived`.

| Status | General Update | Archive (Delete) | Add AvailabilityRule/Blackout/Approver | Availability query | Historical data |
|---|---|---|---|---|---|
| Active | Allowed | Archives it | Allowed | Computed normally | - |
| Inactive | Allowed | Archives it | Allowed | `BookableSlots` empty; schedule/occupancy still shown | Still readable |
| Maintenance | Allowed | Archives it | Allowed (deliberate - see below) | `BookableSlots` empty; schedule/occupancy still shown | Still readable |
| Archived | **Rejected** (`ConflictException`) | Idempotent no-op | **Rejected** (`ConflictException`) - includes *editing* an existing Blackout, not just adding a new one | Computed (not 404); `BookableSlots` empty because non-Active | Still readable |

Notes:

- **Archived is terminal.** `UpdateResourceCommandHandler` rejects any edit once `Status == Archived` -
  there is no path back to a different status through the general-purpose Update action. This is
  deliberate: a full field rewrite bundled with a status flip would let Update silently "reactivate" a
  resource nobody explicitly asked to reactivate. **If reactivation ever becomes a real requirement,
  it must be its own explicit lifecycle action, not a side effect of relaxing Update's Archived check.**
  Do not build this speculatively - see [open-questions.md](open-questions.md).
- **Maintenance is deliberately NOT blocked from new child configuration.** Unlike Archived, a resource
  in Maintenance can still have `AvailabilityRule`/`BlackoutPeriod`/`ResourceApprover` rows added to it -
  it's temporary, and configuring a blackout for the exact maintenance window (or otherwise preparing
  the resource for when it comes back) is a legitimate thing to do while it lasts.
- **`UpdateBlackoutPeriodCommandHandler` enforces Archived-is-terminal too, not just Create.** Every
  sibling Create/Update handler in this feature (`CreateBlackoutPeriodCommandHandler`,
  `AssignResourceApproverCommandHandler`, `UpdateResourceCommandHandler`) already called
  `ResourceGuard.EnsureNotArchived(resource)`; Update for BlackoutPeriod specifically was missing it -
  editing an existing blackout on an Archived resource was possible until this was found and fixed.
  Covered by `UpdateBlackoutPeriodCommandHandlerTests.Handle_ForAnArchivedResource_...` and
  `ResourcesEndpointsTests.UpdateBlackoutPeriod_OnAnArchivedResource_ReturnsConflict`.
- **Delete/Remove operations on child entities have no parent-status check at all**, for any status
  including Archived - cleanup should always be possible regardless of the parent's lifecycle state.
  Only *creating new* child configuration is blocked, and only when the parent is Archived.
- **Archived does not hide history.** The availability query still loads and computes normally for an
  Archived resource (no 404) - only `BookableSlots` is forced empty, the same mechanism used for
  Inactive/Maintenance. A caller can still see what an archived resource's schedule/blackouts/bookings
  were.

## Capacity invariant

> Reducing a Resource's `Capacity` must never create a state where an existing active reservation
> becomes impossible while still appearing to exist.

Enforced in `UpdateResourceCommandHandler.EnsureCapacityCoversExistingBookingsAsync`, invoked only when
`request.Capacity < resource.Capacity` (an increase never needs this check).

### Which statuses count

Only `BookingStatus.Pending` and `BookingStatus.Confirmed` count as "active" demand against capacity -
the same `ActiveStatuses` definition `BookingAvailabilityRepository` already uses for the availability
query itself (Pending counts because you should not be able to double-book a slot while someone else's
approval is pending). `Cancelled`, `Rejected`, `Completed`, and `NoShow` bookings are invisible to this
check, exactly as they are to availability.

### The sweep-line and endpoint overlap semantics

Each active booking contributes two events: `(StartUtc, +Quantity)` and `(EndUtc, -Quantity)`. Events
are sorted by timestamp; the running sum's maximum across the sweep is the peak concurrent demand. A
capacity reduction is rejected only if the new `Capacity` would be lower than that peak.

**`[Start, End)` intervals are treated as non-overlapping when one ends exactly when another begins.**
This matches the half-open convention used everywhere else in this feature (e.g. the availability
query's own `StartUtc < rangeEndUtcExclusive && EndUtc > rangeStartUtc` checks). Concretely: when a
start event and an end event land on the identical timestamp, **the end event is applied first**. Two
back-to-back bookings - one ending at 14:00, the next starting at 14:00 - are correctly treated as
never needing combined capacity, not as briefly overlapping. Without this explicit tie-break, a naive
sweep would sum both bookings' quantities for an instant and could reject a perfectly legitimate
capacity reduction; this exact scenario is covered by
`Handle_DecreasingCapacityWithBackToBackNonOverlappingBookings_TreatsThemAsNonOverlapping`.

### Concurrency: shares the same resource-row lock as booking creation

**This check now runs inside the same `IResourceBookingLock` boundary `CreateBookingCommandHandler`
uses**, keyed by the same `Resources.Id` - see [bookings-and-concurrency.md](bookings-and-concurrency.md)
for the full mechanism. This closed a real race: a capacity reduction and a concurrent booking creation
for the same resource used to be able to interleave, because `Resource.RowVersion` only detects a
competing write to the `Resources` row itself, and booking creation never writes to `Resources` at all
(only `Bookings`) - so a capacity reduction validated against a stale (too-low) demand snapshot could
commit successfully with no RowVersion conflict, even though a booking committed in between made the new
capacity invalid. `UpdateResourceCommandHandler.EnsureCapacityCoversExistingBookingsAsync` now always
re-reads active bookings *after* the lock is acquired, never reusing a value read before it. Proven by
`BookSpace.Infrastructure.Tests.Persistence.UpdateResourceCapacityConcurrencyTests` (real SQL Server
LocalDB - required, since SQLite has no `UPDLOCK`/`HOLDLOCK` support and the lock is a no-op there).
