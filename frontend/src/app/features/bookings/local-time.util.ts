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
