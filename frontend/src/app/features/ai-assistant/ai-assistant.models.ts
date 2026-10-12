import { ProposeBookingCandidate } from '../bookings/ai-booking.models';

// The floating assistant's own chat transcript - a strict superset of the wire-level ConversationTurn
// (role/text) the backend contract uses, so this can carry rich UI state (a proposal's live status) while
// still being reducible to that bounded role/text shape for the NEXT request's context - see
// ai-assistant.service.ts's buildContextTurns().
export type ChatMessageId = string;

export interface UserChatMessage {
  kind: 'user';
  id: ChatMessageId;
  text: string;
}

// Covers every assistant-generated outcome that is plain text, not a structured proposal: a clarification
// question, "no suitable resource", "disabled", "provider unavailable", and a propose-call transport/
// network failure. `tone` only affects presentation (bubble styling) - every tone is still ordinary,
// expected conversation content, never literal HTML (see AiAssistantWidgetComponent's own doc comment on
// why message text is always rendered as plain text).
export type AssistantTextTone = 'clarification' | 'info' | 'error';

export interface AssistantTextChatMessage {
  kind: 'assistant-text';
  id: ChatMessageId;
  text: string;
  tone: AssistantTextTone;
  // Only ever true for a propose() CALL failure (network/provider) - the one case the task's "contextual
  // errors and retry behavior" means offering a retry inline on this exact message. Never set for an
  // ordinary NeedsClarification/NoSuitableResource/Disabled outcome, which is a normal answer, not a
  // failure to retry.
  retryable: boolean;
}

// pending: awaiting the user's Confirm/Discard, and - see AiAssistantService.activeProposalId - the ONE
//   proposal in the whole conversation that is currently actionable, per the task's "only one proposal
//   actionable at a time."
// confirming: POST /api/bookings is in flight for this exact candidate - card stays visible, both actions
//   disabled, a progress label shown (see the task's "while confirming: keep the card visible, show
//   progress, prevent duplicate submissions").
// confirmed / pendingApproval: the real booking now exists (Booking.Confirmed / Booking.Pending) -
//   terminal, Confirm permanently disabled, a link to My Bookings offered.
// failed: confirmation was rejected (409 - the slot was taken, cancelled, etc.) - terminal for THIS
//   candidate (it was a snapshot that is now known stale); the user must ask again for a fresh one, not
//   retry the same stale candidate.
// discarded: the user explicitly dismissed this proposal without confirming - terminal, conversation
//   continues.
// superseded: a NEWER proposal (or a new conversation) made this one no longer the active one - its own
//   actions are disabled, but the message itself stays visible in the transcript (see the task's "keep
//   the old message visible but clearly mark its proposal as superseded").
export type ProposalStatus = 'pending' | 'confirming' | 'confirmed' | 'pendingApproval' | 'failed' | 'discarded' | 'superseded';

export interface ProposalChatMessage {
  kind: 'proposal';
  id: ChatMessageId;
  candidate: ProposeBookingCandidate;
  status: ProposalStatus;
  // Set only when status === 'failed' - the specific, user-facing reason shown inline on the card (the
  // same CONFLICT_MESSAGES mapping the old single-shot form used).
  failureMessage?: string;
  // Set only when status is 'confirmed'/'pendingApproval' - the real booking id, for the "View in My
  // Bookings" action.
  bookingId?: string;
}

export type ChatMessage = UserChatMessage | AssistantTextChatMessage | ProposalChatMessage;
