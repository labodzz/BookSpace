import { toBookingStatus } from './booking.mappers';

describe('toBookingStatus', () => {
  it('maps every BookSpace.Domain.Enums.BookingStatus code to its matching string status', () => {
    expect(toBookingStatus(0)).toBe('Pending');
    expect(toBookingStatus(1)).toBe('Confirmed');
    expect(toBookingStatus(2)).toBe('Rejected');
    expect(toBookingStatus(3)).toBe('Cancelled');
    expect(toBookingStatus(4)).toBe('Completed');
    expect(toBookingStatus(5)).toBe('NoShow');
    // Expired (WP-8 stale-approval job) is code 6 - added as the LAST enum member so it never renumbers
    // an existing status. Before this was added here, code 6 silently fell back to 'Pending' (the
    // function's own ?? fallback), which would have shown an auto-expired booking as still Pending.
    expect(toBookingStatus(6)).toBe('Expired');
  });

  it('falls back to Pending only for a genuinely unrecognized code', () => {
    expect(toBookingStatus(99)).toBe('Pending');
  });
});
