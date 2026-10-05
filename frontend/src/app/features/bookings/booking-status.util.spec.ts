import { canCancelBooking, statusLabel, statusPillClass } from './booking-status.util';
import { BookingStatus } from './booking.models';

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
