import { parseStrictLocalDateTime } from './local-time.util';

describe('parseStrictLocalDateTime', () => {
  it('parses a normal, unambiguous local time', () => {
    const result = parseStrictLocalDateTime('2026-06-15T09:00', 'America/New_York');

    expect(result?.toUTC().toISO()).toBe('2026-06-15T13:00:00.000Z');
  });

  // America/New_York springs forward on 2027-03-14 (2:00 -> 3:00) - 2:30 AM never happens that day.
  // Luxon's default fromISO would silently shift this to 3:30 instead of flagging it, which this
  // function must catch via the round-trip check.
  it('returns null for a nonexistent spring-forward local time', () => {
    expect(parseStrictLocalDateTime('2027-03-14T02:30', 'America/New_York')).toBeNull();
  });

  it('still parses valid times just outside the spring-forward gap', () => {
    expect(parseStrictLocalDateTime('2027-03-14T01:59', 'America/New_York')).not.toBeNull();
    expect(parseStrictLocalDateTime('2027-03-14T03:00', 'America/New_York')).not.toBeNull();
  });

  // America/New_York falls back on 2027-11-07 (2:00 -> 1:00) - 1:30 AM happens twice. Both are real
  // instants, so this must resolve to one (Luxon's default: the offset in effect before the fall-back).
  it('resolves an ambiguous fall-back local time to a real instant rather than rejecting it', () => {
    const result = parseStrictLocalDateTime('2027-11-07T01:30', 'America/New_York');

    expect(result).not.toBeNull();
    expect(result!.toUTC().toISO()).toBe('2027-11-07T05:30:00.000Z');
  });

  it('returns null for a genuinely malformed input', () => {
    expect(parseStrictLocalDateTime('not-a-date', 'America/New_York')).toBeNull();
  });
});
