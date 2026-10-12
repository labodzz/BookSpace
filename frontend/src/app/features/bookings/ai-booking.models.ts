// Mirrors BookSpace.Application.AiBooking.ProposeBookingOutcome exactly - do not add values the API
// doesn't have. See ai-booking.mappers.ts for the wire (numeric) <-> string mapping.
export type ProposeBookingOutcome = 'Proposed' | 'NeedsClarification' | 'NoSuitableResource' | 'Disabled' | 'ProviderUnavailable';

// Mirrors ProposeBookingCandidateResponse. Every field here is shown to the user BEFORE they confirm -
// see the floating assistant's ai-proposal-card review step - never used to silently fill in a confirm
// request the user hasn't seen.
export interface ProposeBookingCandidate {
  resourceId: string;
  resourceName: string;
  timeZoneId: string;
  startUtc: string;
  endUtc: string;
  quantity: number;
  resourceCapacity: number;
  requiresApproval: boolean;
  summary: string;
}

export interface ProposeBookingResult {
  outcome: ProposeBookingOutcome;
  language: 'bs' | 'en';
  message: string | null;
  candidate: ProposeBookingCandidate | null;
}

export type ConversationTurnRole = 'user' | 'assistant';

// One turn of the bounded, client-held transcript sent back on a clarification round-trip - see
// AiAssistantService.buildContextTurns() and the backend's own ProposeBookingCommandRequest doc comment
// for why this is never persisted server-side.
export interface ConversationTurn {
  role: ConversationTurnRole;
  text: string;
}

export interface ProposeBookingRequest {
  text: string;
  priorTurns?: ConversationTurn[];
}
