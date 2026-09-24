import { DateTime } from 'luxon';
import { resolveLuxonZone } from '../resources/timezone.util';

// Luxon's DateTime.fromISO(text) keeps whatever offset is embedded in the string (a UTC ISO timestamp
// parses into a UTC-zoned DateTime) - it does NOT automatically convert to the viewer's local zone. Every
// "personal" booking display (My Bookings, the calendar, cancel confirmations, the approval queue) is
// deliberately shown in the viewer's own local time, so every read of a *Utc timestamp for that purpose
// must go through this helper rather than a bare DateTime.fromISO, or it silently displays/buckets by
// UTC time instead - the exact off-by-one-day/hour bug class this project was told to avoid.
export function toLocalDateTime(utcIso: string): DateTime {
  return DateTime.fromISO(utcIso).toLocal();
}

// Centralizes the one Intl call every dual-timezone display needs for "the viewer's own zone" - falls
// back to 'UTC' only if Intl itself throws, which practically never happens in a real browser.
export function detectViewerTimeZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
  } catch {
    return 'UTC';
  }
}

export interface ZoneRangeText {
  zoneId: string;
  text: string;
}

// `second` is null when secondZoneId resolves (via resolveLuxonZone) to the exact same zone as
// firstZoneId - showing the identical time twice, once per zone label, is exactly the duplication the
// dual-timezone display must never do.
export interface DualZoneRange {
  first: ZoneRangeText;
  second: ZoneRangeText | null;
}

function zoneInterval(startUtcIso: string, endUtcIso: string, zoneId: string): { start: DateTime; end: DateTime } {
  const zone = resolveLuxonZone(zoneId);
  return {
    start: DateTime.fromISO(startUtcIso, { zone: 'utc' }).setZone(zone),
    end: DateTime.fromISO(endUtcIso, { zone: 'utc' }).setZone(zone),
  };
}

function formatInterval(interval: { start: DateTime; end: DateTime }, fullDate: boolean): string {
  return fullDate
    ? `${interval.start.toFormat('cccc, LLL d, HH:mm')} – ${interval.end.toFormat('cccc, LLL d, HH:mm')}`
    : `${interval.start.toFormat('HH:mm')}–${interval.end.toFormat('HH:mm')}`;
}

// Formats one UTC start/end pair against two IANA zones at once, applying real DST rules for the actual
// calendar date involved (via Luxon's setZone/toFormat, resolved through the same resolveLuxonZone every
// other resource-zone display already uses) rather than any stored/cached offset - winter vs. summer just
// falls out of which date is passed in, with no separate DST logic here.
//
// Deliberately zone-order-agnostic: most screens pass (resourceZoneId, viewerZoneId) for a
// resource-primary display, but the calendar's own event-detail dialog passes (viewerZoneId,
// resourceZoneId) instead, since the grid's own frame of reference is the viewer's zone there - the
// caller decides which zone is "first" by argument order, this function has no opinion.
//
// Full-date rule: both first.text and second.text switch from "HH:mm–HH:mm" to the full-date form the
// moment ANY of the following holds: first's own start/end land on different calendar dates in its zone,
// second's own start/end do too, the two zones simply disagree on which date the start falls on, or the
// caller passes alwaysShowDate (for a flat, multi-day list - My Bookings, the approval queue, a recurring
// series' occurrence list - where nothing else on screen anchors each row to a particular day, unlike a
// single booking-form confirmation or a slot already grouped under its own day heading). Applying the
// date to both sides together (not just whichever one crosses midnight) is deliberate - "10:15 today"
// next to "17:15" with only one of the two showing a date is exactly the half-labelled, ambiguous state a
// dual-timezone display exists to remove.
export function formatDualZoneRange(
  startUtcIso: string,
  endUtcIso: string,
  firstZoneId: string,
  secondZoneId: string,
  options: { alwaysShowDate?: boolean } = {},
): DualZoneRange {
  const first = zoneInterval(startUtcIso, endUtcIso, firstZoneId);
  const sameZone = resolveLuxonZone(firstZoneId) === resolveLuxonZone(secondZoneId);
  const second = sameZone ? null : zoneInterval(startUtcIso, endUtcIso, secondZoneId);

  const firstCrosses = !first.start.hasSame(first.end, 'day');
  const secondCrosses = second ? !second.start.hasSame(second.end, 'day') : false;
  const zonesDisagree = second ? !first.start.hasSame(second.start, 'day') : false;
  const needsFullDate = options.alwaysShowDate || firstCrosses || secondCrosses || zonesDisagree;

  return {
    first: { zoneId: firstZoneId, text: formatInterval(first, needsFullDate) },
    second: second ? { zoneId: secondZoneId, text: formatInterval(second, needsFullDate) } : null,
  };
}

// Matches the `${date}T${time}` strings both booking forms build from their separate date/time
// inputs (no seconds, no offset).
const LOCAL_DATE_TIME_FORMAT = "yyyy-MM-dd'T'HH:mm";

// Luxon does NOT mark a nonexistent local wall-clock time as invalid - one that falls in a
// spring-forward DST gap (e.g. 2:30 AM on the day a resource's timezone jumps its clocks from 2:00 to
// 3:00) silently normalizes to a real instant instead (typically shifted forward by the size of the
// gap), so `DateTime.fromISO(...).isValid` alone can never catch it. Formatting the parsed result back
// through the exact same format and comparing it to the original string does: a genuine nonexistent
// time round-trips to a DIFFERENT wall-clock string, while every time that actually exists - including
// an ambiguous fall-back one (a wall-clock time that occurs twice when clocks go back) - round-trips
// back to itself exactly. Ambiguous times are therefore accepted here, resolved to whichever single
// offset Luxon's own default picks (the offset in effect immediately before the fall-back, i.e. the
// FIRST of the two occurrences) - a real, valid instant, just not the only one a human could have meant.
export function parseStrictLocalDateTime(localDateTime: string, zone: string): DateTime | null {
  const parsed = DateTime.fromISO(localDateTime, { zone });
  return parsed.isValid && parsed.toFormat(LOCAL_DATE_TIME_FORMAT) === localDateTime ? parsed : null;
}
