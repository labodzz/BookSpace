import { DateTime } from 'luxon';

// Luxon's DateTime.fromISO(text) keeps whatever offset is embedded in the string (a UTC ISO timestamp
// parses into a UTC-zoned DateTime) - it does NOT automatically convert to the viewer's local zone. Every
// "personal" booking display (My Bookings, the calendar, cancel confirmations, the approval queue) is
// deliberately shown in the viewer's own local time, so every read of a *Utc timestamp for that purpose
// must go through this helper rather than a bare DateTime.fromISO, or it silently displays/buckets by
// UTC time instead - the exact off-by-one-day/hour bug class this project was told to avoid.
export function toLocalDateTime(utcIso: string): DateTime {
  return DateTime.fromISO(utcIso).toLocal();
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
