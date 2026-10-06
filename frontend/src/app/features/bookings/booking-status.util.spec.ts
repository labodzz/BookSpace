import { canCancelBooking, canCheckInBooking, statusLabel, statusPillClass } from './booking-status.util';
import { BookingStatus, OwnBooking } from './booking.models';

describe('statusLabel', () => {
  it('labels NoShow as "No-show"', () => {
    expect(statusLabel('NoShow')).toBe('No-show');
  });

  it('labels Expired as the raw status, like every other status except NoShow', () => {
    expect(statusLabel('Expired')).toBe('Expired');
  });

  it('labels every other status as the raw status', () => {
    const statuses: BookingStatus[] = ['Pending', 'Confirmed', 'Rejected', 'Cancelled', 'Completed'];
    for (const status of statuses) {
      expect(statusLabel(status)).toBe(status);
    }
  });
});

describe('statusPillClass', () => {
  it('builds a lowercased status-pill class for Expired, same pattern as every other status', () => {
    expect(statusPillClass('Expired')).toBe('status-pill status-pill--expired');
  });
});

describe('canCancelBooking', () => {
  it('never allows cancelling an Expired booking - it is already terminal', () => {
    expect(canCancelBooking('Expired')).toBe(false);
  });
});

describe('canCheckInBooking', () => {
  function confirmedBooking(overrides: Partial<OwnBooking> = {}): OwnBooking {
    return {
      id: 'b1', resourceId: 'r1', startUtc: '2026-09-02T10:00:00Z', endUtc: '2026-09-02T11:00:00Z',
      quantity: 1, status: 'Confirmed', cancelledAtUtc: null, cancelledByAdmin: false, cancellationReason: null,
      seriesId: null, timeZoneId: 'UTC', checkedInAtUtc: null,
      checkInWindowOpensAtUtc: '2026-09-02T09:45:00Z', checkInWindowClosesAtUtc: '2026-09-02T10:15:00Z',
      ...overrides,
    };
  }

  it('allows check-in exactly at the window-open boundary (inclusive)', () => {
    expect(canCheckInBooking(confirmedBooking(), new Date('2026-09-02T09:45:00Z'))).toBe(true);
  });

  it('allows check-in exactly at the window-close boundary (inclusive)', () => {
    expect(canCheckInBooking(confirmedBooking(), new Date('2026-09-02T10:15:00Z'))).toBe(true);
  });

  it('refuses check-in one second before the window opens', () => {
    expect(canCheckInBooking(confirmedBooking(), new Date('2026-09-02T09:44:59Z'))).toBe(false);
  });

  it('refuses check-in one second after the window closes', () => {
    expect(canCheckInBooking(confirmedBooking(), new Date('2026-09-02T10:15:01Z'))).toBe(false);
  });

  it('refuses check-in for a booking that is not Confirmed, even within the window', () => {
    expect(canCheckInBooking(confirmedBooking({ status: 'Pending' }), new Date('2026-09-02T10:00:00Z'))).toBe(false);
  });

  it('refuses check-in for a booking already checked in, even within the window', () => {
    expect(canCheckInBooking(confirmedBooking({ checkedInAtUtc: '2026-09-02T09:50:00Z' }), new Date('2026-09-02T10:00:00Z'))).toBe(false);
  });
});
