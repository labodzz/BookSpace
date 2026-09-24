import { DateTime } from 'luxon';
import { DayAvailabilityData, validateInterval } from './availability-interval.util';

const ZONE = 'Europe/Sarajevo';

function local(text: string): DateTime {
  return DateTime.fromISO(text, { zone: ZONE });
}

function day(overrides: Partial<DayAvailabilityData> = {}): DayAvailabilityData {
  return {
    openPeriods: [{ startUtc: local('2026-10-05T09:00').toUTC().toISO()!, endUtc: local('2026-10-05T17:00').toUTC().toISO()! }],
    blackouts: [],
    busyPeriods: [],
    capacity: 2,
    ...overrides,
  };
}

describe('validateInterval', () => {
  it('accepts an interval fully inside an open period with no conflicts', () => {
    const result = validateInterval(day(), local('2026-10-05T10:00'), local('2026-10-05T11:00'), 1);
    expect(result).toEqual({ status: 'valid' });
  });

  it('rejects an end time not after the start time', () => {
    const result = validateInterval(day(), local('2026-10-05T11:00'), local('2026-10-05T11:00'), 1);
    expect(result).toEqual({ status: 'end-before-start' });
  });

  it('rejects an interval entirely outside every open period', () => {
    const result = validateInterval(day(), local('2026-10-05T18:00'), local('2026-10-05T19:00'), 1);
    expect(result).toEqual({ status: 'outside-availability' });
  });

  it('rejects an interval only partially covered by open periods (a real gap in the middle)', () => {
    const gappy = day({
      openPeriods: [
        { startUtc: local('2026-10-05T09:00').toUTC().toISO()!, endUtc: local('2026-10-05T10:00').toUTC().toISO()! },
        { startUtc: local('2026-10-05T11:00').toUTC().toISO()!, endUtc: local('2026-10-05T17:00').toUTC().toISO()! },
      ],
    });
    const result = validateInterval(gappy, local('2026-10-05T09:30'), local('2026-10-05T11:30'), 1);
    expect(result).toEqual({ status: 'outside-availability' });
  });

  it('rejects an interval overlapping a blackout', () => {
    const withBlackout = day({
      blackouts: [
        { startUtc: local('2026-10-05T12:00').toUTC().toISO()!, endUtc: local('2026-10-05T13:00').toUTC().toISO()!, reason: 'Maintenance' },
      ],
    });
    const result = validateInterval(withBlackout, local('2026-10-05T12:30'), local('2026-10-05T13:30'), 1);
    expect(result).toEqual({ status: 'blackout-conflict' });
  });

  it('does not flag a blackout that ends exactly when the requested interval starts (half-open)', () => {
    const withBlackout = day({
      blackouts: [
        { startUtc: local('2026-10-05T11:00').toUTC().toISO()!, endUtc: local('2026-10-05T12:00').toUTC().toISO()!, reason: 'Maintenance' },
      ],
    });
    const result = validateInterval(withBlackout, local('2026-10-05T12:00'), local('2026-10-05T13:00'), 1);
    expect(result).toEqual({ status: 'valid' });
  });

  it('rejects an interval whose quantity would exceed remaining capacity', () => {
    const busy = day({
      capacity: 2,
      busyPeriods: [{ startUtc: local('2026-10-05T10:00').toUTC().toISO()!, endUtc: local('2026-10-05T11:00').toUTC().toISO()!, quantity: 2 }],
    });
    const result = validateInterval(busy, local('2026-10-05T10:30'), local('2026-10-05T10:45'), 1);
    expect(result).toEqual({ status: 'booking-conflict' });
  });

  it('accepts an interval when remaining capacity still covers the requested quantity', () => {
    const busy = day({
      capacity: 3,
      busyPeriods: [{ startUtc: local('2026-10-05T10:00').toUTC().toISO()!, endUtc: local('2026-10-05T11:00').toUTC().toISO()!, quantity: 2 }],
    });
    const result = validateInterval(busy, local('2026-10-05T10:30'), local('2026-10-05T10:45'), 1);
    expect(result).toEqual({ status: 'valid' });
  });

  it('does not flag an existing booking that ends exactly when the requested interval starts (half-open)', () => {
    const busy = day({
      capacity: 1,
      busyPeriods: [{ startUtc: local('2026-10-05T09:00').toUTC().toISO()!, endUtc: local('2026-10-05T10:00').toUTC().toISO()!, quantity: 1 }],
    });
    const result = validateInterval(busy, local('2026-10-05T10:00'), local('2026-10-05T11:00'), 1);
    expect(result).toEqual({ status: 'valid' });
  });

  it('does not flag an existing booking that starts exactly when the requested interval ends (half-open)', () => {
    const busy = day({
      capacity: 1,
      busyPeriods: [{ startUtc: local('2026-10-05T11:00').toUTC().toISO()!, endUtc: local('2026-10-05T12:00').toUTC().toISO()!, quantity: 1 }],
    });
    const result = validateInterval(busy, local('2026-10-05T10:00'), local('2026-10-05T11:00'), 1);
    expect(result).toEqual({ status: 'valid' });
  });

  it('treats two touching open periods as continuous coverage with no gap', () => {
    const touching = day({
      openPeriods: [
        { startUtc: local('2026-10-05T09:00').toUTC().toISO()!, endUtc: local('2026-10-05T12:00').toUTC().toISO()! },
        { startUtc: local('2026-10-05T12:00').toUTC().toISO()!, endUtc: local('2026-10-05T17:00').toUTC().toISO()! },
      ],
    });
    const result = validateInterval(touching, local('2026-10-05T11:00'), local('2026-10-05T13:00'), 1);
    expect(result).toEqual({ status: 'valid' });
  });

  it('checks end-before-start before anything else, even with no availability data at all', () => {
    const empty = day({ openPeriods: [] });
    const result = validateInterval(empty, local('2026-10-05T11:00'), local('2026-10-05T10:00'), 1);
    expect(result).toEqual({ status: 'end-before-start' });
  });
});
