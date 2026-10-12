import { ProposeBookingOutcome, ProposeBookingResult } from './ai-booking.models';

// BookSpace serializes every enum in a JSON body as its raw underlying number (System.Text.Json's
// default - no JsonStringEnumConverter is registered anywhere in the pipeline, same as
// BookSpace.Domain.Enums.BookingStatus - see booking.mappers.ts's own identical comment). Index = the
// C# enum's underlying int, matching BookSpace.Application.AiBooking.ProposeBookingOutcome exactly.
const PROPOSE_BOOKING_OUTCOME_BY_CODE: readonly ProposeBookingOutcome[] = [
  'Proposed',
  'NeedsClarification',
  'NoSuitableResource',
  'Disabled',
  'ProviderUnavailable',
];

export function toProposeBookingOutcome(code: number): ProposeBookingOutcome {
  return PROPOSE_BOOKING_OUTCOME_BY_CODE[code] ?? 'ProviderUnavailable';
}

export interface ProposeBookingResultWire extends Omit<ProposeBookingResult, 'outcome'> {
  outcome: number;
}

export function mapProposeBookingResult(wire: ProposeBookingResultWire): ProposeBookingResult {
  return { ...wire, outcome: toProposeBookingOutcome(wire.outcome) };
}
