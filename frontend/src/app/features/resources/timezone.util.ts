import { DateTime } from 'luxon';

// BookSpace's TimeZoneId validator (CreateResourceCommandRequestValidator) accepts "a valid IANA or
// Windows time zone id," because .NET's TimeZoneInfo.FindSystemTimeZoneById understands both - but
// Luxon (and the browser's Intl API it's built on) only resolves IANA names. A resource stored with a
// Windows-style id like "Eastern Standard Time" makes DateTime.setZone(...) silently produce an
// "Invalid DateTime" - which then gets formatted into that literal string and sent to the API as a date
// query param, producing a 400. This maps the common Windows zone names to their IANA equivalent so
// those resources still work; an id this doesn't recognize falls back to the viewer's own local zone
// (better than crashing, even though it may not show the resource's true local hours).
const WINDOWS_TO_IANA: Readonly<Record<string, string>> = {
  UTC: 'UTC',
  'GMT Standard Time': 'Europe/London',
  'W. Europe Standard Time': 'Europe/Berlin',
  'Central Europe Standard Time': 'Europe/Budapest',
  'Central European Standard Time': 'Europe/Sarajevo',
  'Romance Standard Time': 'Europe/Paris',
  'FLE Standard Time': 'Europe/Kyiv',
  'Russian Standard Time': 'Europe/Moscow',
  'Eastern Standard Time': 'America/New_York',
  'Central Standard Time': 'America/Chicago',
  'Mountain Standard Time': 'America/Denver',
  'Pacific Standard Time': 'America/Los_Angeles',
  'China Standard Time': 'Asia/Shanghai',
  'Tokyo Standard Time': 'Asia/Tokyo',
  'India Standard Time': 'Asia/Kolkata',
  'AUS Eastern Standard Time': 'Australia/Sydney',
  'SA Eastern Standard Time': 'America/Sao_Paulo',
};

export function resolveLuxonZone(timeZoneId: string): string {
  if (DateTime.local().setZone(timeZoneId).isValid) {
    return timeZoneId;
  }

  const mapped = WINDOWS_TO_IANA[timeZoneId];
  if (mapped && DateTime.local().setZone(mapped).isValid) {
    return mapped;
  }

  return 'local';
}
