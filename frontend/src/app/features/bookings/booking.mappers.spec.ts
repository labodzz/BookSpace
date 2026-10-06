import { mapCheckInBookingResponse, toBookingStatus } from './booking.mappers';

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

describe('mapCheckInBookingResponse', () => {
  it('maps the numeric status code and passes checkedInAtUtc/alreadyCheckedIn through unchanged', () => {
    const result = mapCheckInBookingResponse({
      id: 'b1', resourceId: 'r1', startUtc: '2026-09-02T10:00:00Z', endUtc: '2026-09-02T11:00:00Z',
      quantity: 1, status: 1, checkedInAtUtc: '2026-09-02T09:50:00Z', alreadyCheckedIn: true,
    });

    expect(result.status).toBe('Confirmed');
    expect(result.checkedInAtUtc).toBe('2026-09-02T09:50:00Z');
    expect(result.alreadyCheckedIn).toBe(true);
  });
});
