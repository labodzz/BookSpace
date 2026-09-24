import { detectViewerTimeZone, formatDualZoneRange, parseStrictLocalDateTime } from './local-time.util';

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

describe('formatDualZoneRange', () => {
  // The task's own worked example: a Sarajevo resource booked by a viewer in Tokyo.
  it('shows the resource-local time as first and the viewer-local time as second when the zones differ', () => {
    const result = formatDualZoneRange('2026-07-15T08:15:00Z', '2026-07-15T09:15:00Z', 'Europe/Sarajevo', 'Asia/Tokyo');

    expect(result.first).toEqual({ zoneId: 'Europe/Sarajevo', text: '10:15–11:15' }); // CEST, UTC+2 in July
    expect(result.second).toEqual({ zoneId: 'Asia/Tokyo', text: '17:15–18:15' }); // UTC+9, no DST
  });

  it('returns a null second when both zones resolve to the same IANA zone - never duplicating the same time', () => {
    const result = formatDualZoneRange('2026-07-15T08:15:00Z', '2026-07-15T09:15:00Z', 'Europe/Sarajevo', 'Europe/Sarajevo');

    expect(result.second).toBeNull();
    expect(result.first.text).toBe('10:15–11:15');
  });

  // Neither zone's own start/end crosses midnight internally here - only the two zones disagree with
  // EACH OTHER about which calendar date the same instant falls on (Jan 1 evening in Los Angeles is
  // already Jan 2 morning in Tokyo). Both sides must still switch to the full-date form, not just
  // whichever one a naive per-zone-only check would flag.
  it('shows the full date on both sides when the two zones disagree on the calendar date', () => {
    const result = formatDualZoneRange('2026-01-01T23:00:00Z', '2026-01-02T00:00:00Z', 'America/Los_Angeles', 'Asia/Tokyo');

    expect(result.first.text).toContain('Jan 1');
    expect(result.second!.text).toContain('Jan 2');
  });

  // Also directly covers required scenario 7's frontend half: this is a pure display computation - it
  // never mutates or re-sends the UTC strings it's given.
  it("computes the resource zone's offset from the given date (DST), not a fixed value", () => {
    const winter = formatDualZoneRange('2026-01-15T10:00:00Z', '2026-01-15T11:00:00Z', 'Europe/Sarajevo', 'Europe/Sarajevo');
    const summer = formatDualZoneRange('2026-07-15T10:00:00Z', '2026-07-15T11:00:00Z', 'Europe/Sarajevo', 'Europe/Sarajevo');

    expect(winter.first.text).toBe('11:00–12:00'); // CET, UTC+1 in January
    expect(summer.first.text).toBe('12:00–13:00'); // CEST, UTC+2 in July
  });
});

describe('detectViewerTimeZone', () => {
  afterEach(() => vi.restoreAllMocks());

  it("returns the browser's resolved IANA zone", () => {
    vi.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockReturnValue({ timeZone: 'Asia/Tokyo' } as Intl.ResolvedDateTimeFormatOptions);

    expect(detectViewerTimeZone()).toBe('Asia/Tokyo');
  });

  it('falls back to UTC when Intl throws', () => {
    vi.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockImplementation(() => {
      throw new Error('Intl unavailable');
    });

    expect(detectViewerTimeZone()).toBe('UTC');
  });
});
