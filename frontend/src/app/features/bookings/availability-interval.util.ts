import { DateTime } from 'luxon';
import { BlackoutWindow, BusyPeriod, OpenPeriod } from '../resources/resource.models';

// Live, pre-submit validation for the integrated booking form - deliberately reuses the EXACT same raw
// data GetResourceAvailabilityQueryHandler already returned (openPeriods/blackouts/busyPeriods/capacity),
// not a separate frontend-computed availability model. This mirrors BookingEligibilityChecker's own
// check order (resource status is checked separately, before this ever runs) so the four surfaced
// messages line up with what the backend would actually reject the request for - it's a UX aid, not a
// second source of truth; the backend re-validates authoritatively at submit time regardless.
export type IntervalValidationResult =
  | { status: 'valid' }
  | { status: 'end-before-start' }
  | { status: 'outside-availability' }
  | { status: 'blackout-conflict' }
  | { status: 'booking-conflict' };

export interface DayAvailabilityData {
  openPeriods: OpenPeriod[];
  blackouts: BlackoutWindow[];
  busyPeriods: BusyPeriod[];
  capacity: number;
}

// Half-open [start, end) overlap test, matching the backend's own convention throughout (see
// docs/bookings-and-concurrency.md §3) - an interval ending exactly when another begins does not
// overlap it.
function overlapsMs(aStart: number, aEnd: number, bStart: number, bEnd: number): boolean {
  return aStart < bEnd && aEnd > bStart;
}

// True only if [startMs, endMs) is covered with no gaps by the union of the given periods - the same
// half-open coverage AvailabilityCalculator's open periods already express, just re-evaluated against
// one candidate interval instead of a whole day's worth of buttons.
function isFullyCovered(periods: { startUtc: string; endUtc: string }[], startMs: number, endMs: number): boolean {
  const relevant = periods
    .map((period) => ({ start: DateTime.fromISO(period.startUtc).toMillis(), end: DateTime.fromISO(period.endUtc).toMillis() }))
    .filter((period) => overlapsMs(period.start, period.end, startMs, endMs))
    .sort((a, b) => a.start - b.start);

  let cursor = startMs;
  for (const period of relevant) {
    if (period.start > cursor) {
      return false; // a gap exists before this period starts
    }
    cursor = Math.max(cursor, period.end);
    if (cursor >= endMs) {
      return true;
    }
  }
  return cursor >= endMs;
}

// Peak concurrent busy quantity actually overlapping [startMs, endMs) - a sweep restricted to just the
// candidate window, not the whole day. Ties (an end and a start landing on the identical instant) apply
// the end first, so two back-to-back busy periods are never briefly summed together - the same tie-break
// UpdateResourceCommandHandler's own capacity sweep uses for the same half-open reasoning.
function maxConcurrentQuantity(busyPeriods: BusyPeriod[], startMs: number, endMs: number): number {
  const events: { at: number; delta: number }[] = [];
  for (const period of busyPeriods) {
    const periodStart = DateTime.fromISO(period.startUtc).toMillis();
    const periodEnd = DateTime.fromISO(period.endUtc).toMillis();
    if (!overlapsMs(periodStart, periodEnd, startMs, endMs)) {
      continue;
    }
    events.push({ at: Math.max(periodStart, startMs), delta: period.quantity });
    events.push({ at: Math.min(periodEnd, endMs), delta: -period.quantity });
  }
  events.sort((a, b) => a.at - b.at || a.delta - b.delta);

  let running = 0;
  let peak = 0;
  for (const event of events) {
    running += event.delta;
    peak = Math.max(peak, running);
  }
  return peak;
}

export function validateInterval(day: DayAvailabilityData, start: DateTime, end: DateTime, quantity: number): IntervalValidationResult {
  if (end <= start) {
    return { status: 'end-before-start' };
  }

  const startMs = start.toMillis();
  const endMs = end.toMillis();

  if (!isFullyCovered(day.openPeriods, startMs, endMs)) {
    return { status: 'outside-availability' };
  }
  const blackoutHit = day.blackouts.some((blackout) =>
    overlapsMs(DateTime.fromISO(blackout.startUtc).toMillis(), DateTime.fromISO(blackout.endUtc).toMillis(), startMs, endMs),
  );
  if (blackoutHit) {
    return { status: 'blackout-conflict' };
  }
  if (day.capacity - maxConcurrentQuantity(day.busyPeriods, startMs, endMs) < quantity) {
    return { status: 'booking-conflict' };
  }
  return { status: 'valid' };
}
