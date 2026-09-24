import { OwnBooking } from '../booking.models';
import { groupBookingsForDisplay } from './booking-grouping.util';

function booking(overrides: Partial<OwnBooking> & Pick<OwnBooking, 'id'>): OwnBooking {
  return {
    resourceId: 'resource-1',
    startUtc: '2026-09-02T10:00:00Z',
    endUtc: '2026-09-02T11:00:00Z',
    quantity: 1,
    status: 'Confirmed',
    cancelledAtUtc: null,
    cancelledByAdmin: false,
    cancellationReason: null,
    seriesId: null,
    timeZoneId: 'UTC',
    ...overrides,
  };
}

describe('groupBookingsForDisplay', () => {
  it('keeps a one-off booking as its own single entry', () => {
    const entries = groupBookingsForDisplay([booking({ id: 'b1' })]);

    expect(entries).toEqual([{ kind: 'single', booking: booking({ id: 'b1' }) }]);
  });

  it('collapses consecutive occurrences of the same series into one group entry', () => {
    const occurrences = [
      booking({ id: 'b1', seriesId: 'series-1' }),
      booking({ id: 'b2', seriesId: 'series-1' }),
      booking({ id: 'b3', seriesId: 'series-1' }),
    ];

    const entries = groupBookingsForDisplay(occurrences);

    expect(entries).toEqual([{ kind: 'series', seriesId: 'series-1', resourceId: 'resource-1', occurrences }]);
  });

  it('keeps a lone occurrence of a series as its own (single-item) series group, not a plain single entry', () => {
    const entries = groupBookingsForDisplay([booking({ id: 'b1', seriesId: 'series-1' })]);

    expect(entries).toEqual([
      { kind: 'series', seriesId: 'series-1', resourceId: 'resource-1', occurrences: [booking({ id: 'b1', seriesId: 'series-1' })] },
    ]);
  });

  it('starts a new group when a non-adjacent booking shares the same seriesId, rather than reordering the list', () => {
    const first = booking({ id: 'b1', seriesId: 'series-1' });
    const other = booking({ id: 'b2', seriesId: 'series-2' });
    const second = booking({ id: 'b3', seriesId: 'series-1' });

    const entries = groupBookingsForDisplay([first, other, second]);

    expect(entries).toEqual([
      { kind: 'series', seriesId: 'series-1', resourceId: 'resource-1', occurrences: [first] },
      { kind: 'series', seriesId: 'series-2', resourceId: 'resource-1', occurrences: [other] },
      { kind: 'series', seriesId: 'series-1', resourceId: 'resource-1', occurrences: [second] },
    ]);
  });

  it('interleaves single and series entries in the original order', () => {
    const oneOff = booking({ id: 'b1' });
    const occurrenceA = booking({ id: 'b2', seriesId: 'series-1' });
    const occurrenceB = booking({ id: 'b3', seriesId: 'series-1' });
    const otherOneOff = booking({ id: 'b4' });

    const entries = groupBookingsForDisplay([oneOff, occurrenceA, occurrenceB, otherOneOff]);

    expect(entries).toEqual([
      { kind: 'single', booking: oneOff },
      { kind: 'series', seriesId: 'series-1', resourceId: 'resource-1', occurrences: [occurrenceA, occurrenceB] },
      { kind: 'single', booking: otherOneOff },
    ]);
  });

  it('returns an empty list for an empty input', () => {
    expect(groupBookingsForDisplay([])).toEqual([]);
  });
});
