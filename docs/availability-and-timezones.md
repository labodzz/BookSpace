# Availability, Timezones, and DST

This is especially relevant for the future Booking Work Packet - read
[resource-lifecycle-and-capacity.md](resource-lifecycle-and-capacity.md)'s "not a booking-creation
guarantee" section first.

## Storage model

- `AvailabilityRule.StartTime`/`EndTime` are **wall-clock local times** (`TimeOnly`) in the resource's
  own timezone (`Resource.TimeZoneId`), paired with a `DayOfWeek` - not absolute instants.
- `BlackoutPeriod` and `Booking` store **absolute UTC** (`DateTimeOffset`) directly - no timezone
  conversion is ever needed for them.
- The availability query (`GetResourceAvailabilityQueryHandler`) converts each `AvailabilityRule`
  occurrence to UTC independently, per calendar date in the requested range, via
  `ConvertLocalToUtc(date, time, timeZone)`.

## DST policy

`ConvertLocalToUtc` converts each **endpoint** (a rule's start, a rule's end) to UTC completely
independently - each one resolves to its own correct offset for that specific local instant. This
matters for what follows: a window that straddles a DST transition is not "approximately" correct, it
is exactly correct, because independently resolving two instants to their true UTC values inherently
produces the true physical duration between them.

### Spring-forward (invalid local time)

A local time that never occurred (e.g. 02:30 on the day clocks jump 02:00 -> 03:00) throws from
`TimeZoneInfo.ConvertTimeToUtc` if passed through unchanged. `ConvertLocalToUtc` detects this
(`timeZone.IsInvalidTime(local)`) and normalizes forward, one hour at a time, until the instant is
valid - real-world DST gaps are one hour, and checking `IsInvalidTime` again after each step (rather
than assuming exactly one hour) keeps this correct even for a historical zone with a larger offset
change, without hardcoding "1 hour" as a fix amount.

### Fall-back (ambiguous local time)

A local time that occurs twice (e.g. 02:30 on the day clocks fall 03:00 -> 02:00) is resolved via
**.NET's own default for `TimeZoneInfo.ConvertTimeToUtc`: the standard, post-transition (non-daylight)
offset - i.e., the LATER of the two occurrences.** This is relied upon deliberately, not accidentally;
proven by `Handle_WithRuleStartingInsideFallBackAmbiguousHour_ResolvesToStandardOffsetWithoutThrowing`.

### A window crossing a transition

Once each endpoint independently resolves to a well-defined instant (via the two rules above), the
resulting UTC duration is exactly correct - a window spanning a spring-forward gap really is shorter by
the gap's size in physical elapsed time, and one spanning a fall-back really is longer by the same
amount. **An earlier version of this code's own comment claimed the duration would be "off by the DST
delta" in this case - that claim was traced through with concrete numeric examples this session and
found to be incorrect**, not merely optimistic. `Handle_WithRuleSpanningTheSpringForwardTransition_ProducesThePhysicallyCorrectDuration`
and its fall-back counterpart assert the exact expected UTC bounds, not just "doesn't throw."

### Midnight-crossing / multi-day ranges

The overall query range's exclusive end is `ConvertLocalToUtc(request.ToDate.AddDays(1), TimeOnly.MinValue, timeZone)`
- each day in the range is converted independently inside its own loop iteration, so a multi-day query
naturally produces one `OpenPeriod` per day rather than assuming a fixed 24-hour offset between days
(which DST would make incorrect on a transition day). See
`Handle_WithRuleActiveEveryDayAcrossAMultiDayRange_ProducesOnePeriodPerDayAtTheCorrectMidnightBoundary`.

## Tested timezone assumptions

DST tests use `Europe/Sarajevo` (standard CET/CEST rules: spring-forward on the last Sunday of March,
fall-back on the last Sunday of October), computed dynamically per test run
(`LastSundayOfMonth(DateTime.UtcNow.Year + 1, month)`) rather than a hardcoded date, so the tests stay
valid regardless of which year they run in. No other timezone's DST rules are exercised by name; the
policy above is a general `TimeZoneInfo` behavior, not specific to this one zone, but has only been
verified against it.

## Capacity-aware bookable slots

`IntervalMath.ComputeAvailableCapacity` (unchanged by this remediation pass - already existing,
already tested) splits each `OpenPeriod` at every occupancy boundary and reports remaining capacity per
sub-interval; a blackout consumes the resource's full configured capacity, a booking consumes only its
own `Quantity`, and overlapping occupancies stack additively. See
[resource-lifecycle-and-capacity.md](resource-lifecycle-and-capacity.md) for the parallel sweep-line
used by the *capacity-reduction* check, which is a related but separate calculation over the same
underlying booking data.

## Read model, not a booking guarantee

Repeating the warning because it matters most here: this entire query is a snapshot calculation with no
locking behind it. **A resource being reported as available at query time does not guarantee it will
still be available by the time a future Booking-create request commits.** Do not build the Booking
Work Packet on the assumption that this query's output is itself a reservation.
