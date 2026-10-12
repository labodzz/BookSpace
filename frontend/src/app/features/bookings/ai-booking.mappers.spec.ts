import { mapProposeBookingResult, toProposeBookingOutcome } from './ai-booking.mappers';

describe('toProposeBookingOutcome', () => {
  it('maps every BookSpace.Application.AiBooking.ProposeBookingOutcome code to its matching string outcome', () => {
    expect(toProposeBookingOutcome(0)).toBe('Proposed');
    expect(toProposeBookingOutcome(1)).toBe('NeedsClarification');
    expect(toProposeBookingOutcome(2)).toBe('NoSuitableResource');
    expect(toProposeBookingOutcome(3)).toBe('Disabled');
    expect(toProposeBookingOutcome(4)).toBe('ProviderUnavailable');
  });

  it('falls back to ProviderUnavailable for a genuinely unrecognized code', () => {
    expect(toProposeBookingOutcome(99)).toBe('ProviderUnavailable');
  });
});

describe('mapProposeBookingResult', () => {
  it('maps the numeric outcome and passes every other field through unchanged', () => {
    const result = mapProposeBookingResult({
      outcome: 0,
      language: 'en',
      message: null,
      candidate: {
        resourceId: 'r1', resourceName: 'Lab A', timeZoneId: 'UTC', startUtc: '2026-11-10T14:00:00Z',
        endUtc: '2026-11-10T16:00:00Z', quantity: 1, resourceCapacity: 8, requiresApproval: false, summary: 'Lab A summary',
      },
    });

    expect(result.outcome).toBe('Proposed');
    expect(result.candidate?.resourceName).toBe('Lab A');
  });
});
