import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, effect, inject, signal } from '@angular/core';
import { toApiError } from '../../core/http/api-error';
import { AuthService } from '../../core/auth/auth.service';
import { CalendarService } from '../calendar/calendar.service';
import { ConversationTurn, ProposeBookingResult } from '../bookings/ai-booking.models';
import { AiBookingService } from '../bookings/ai-booking.service';
import { BookingService } from '../bookings/booking.service';
import { ChatMessage, ChatMessageId, ProposalStatus } from './ai-assistant.models';

// Mirrors BookSpace.Application.AiBooking.ProposeBookingCommandRequestValidator exactly - the backend
// rejects a request whose PriorTurns exceed this, or whose Text/a turn's Text exceeds MAX_TEXT_LENGTH,
// with a 400 ValidationProblemDetails. buildContextTurns() below keeps every request structurally within
// this bound (a sliding window, not a hard stop) so that 400 can never happen by construction.
export const MAX_CONTEXT_TURNS = 6;
export const MAX_TEXT_LENGTH = 500;

// The same friendlier copy the old single-shot AiBookingFormComponent used for the specific conflicts
// confirming a candidate can throw - see CreateBookingCommandHandler's own eligibility re-check.
const CONFLICT_MESSAGES: Record<string, string> = {
  'Booking.ResourceUnavailable': 'This resource is no longer available for booking.',
  'Booking.OutsideAvailability': "The proposed time is no longer within this resource's open hours.",
  'Booking.BlackoutConflict': 'The proposed time now overlaps a blackout period for this resource.',
  'Booking.CapacityExceeded': 'That time slot was taken by someone else in the meantime.',
};

let nextMessageId = 0;
function createMessageId(): ChatMessageId {
  nextMessageId += 1;
  return `msg-${nextMessageId}`;
}

// The single owner of the floating assistant's conversation state - see docs/ai-assisted-booking.md
// ("Floating assistant") for the full design. Deliberately a providedIn: 'root' service, not state local
// to the panel component: the panel is conditionally rendered (mounted only while open), but the
// conversation - and, critically, any in-flight propose()/confirm() HTTP call - must survive the panel
// being closed (see "Closing the panel hides it without losing the conversation or unexpectedly
// cancelling a confirmation" in the task brief). Every subscription here is therefore rooted on THIS
// service's own lifetime (the whole app session), never a component's DestroyRef.
//
// No conversation content is ever written to localStorage/sessionStorage - closing the browser tab loses
// it, by design (see "avoid persistent storage of conversation content by default"), the same policy the
// original single-shot form already documented.
@Injectable({ providedIn: 'root' })
export class AiAssistantService {
  private readonly aiBookingService = inject(AiBookingService);
  private readonly bookingService = inject(BookingService);
  private readonly authService = inject(AuthService);
  private readonly calendarService = inject(CalendarService);

  readonly isOpen = signal(false);
  readonly messages = signal<ChatMessage[]>([]);
  readonly isSending = signal(false);
  // The ONE proposal (if any) whose Confirm/Discard actions are live - see ProposalStatus's own doc
  // comment on ai-assistant.models.ts for why only one proposal is ever actionable at a time.
  readonly activeProposalId = signal<ChatMessageId | null>(null);

  // Bumped on every new propose() call AND on a full reset (new conversation, identity change) - a
  // response is only ever applied if it's still the most recent thing asked for, the same pattern
  // AiBookingFormComponent's own requestSequence used.
  private requestSequence = 0;

  // The exact (text, contextTurns) a propose() call failed with (network/provider failure only - not a
  // validation rejection, which can never happen again unchanged) - what an inline "Retry" button on that
  // failure message resends, without appending a second, duplicate user bubble for the same text.
  private lastFailedRequest: { text: string; contextTurns: ConversationTurn[] } | null = null;

  // Tracks the identity (userId + tenantId) this conversation belongs to. The effect below re-runs
  // whenever authService.currentUser() - itself a computed signal re-derived from the single `tokens`
  // signal AuthService owns - actually changes, clearing everything on logout (becomes null) or on
  // logging in as someone else without a full page reload. This is a plain Injectable, not a component,
  // but effect() still works here: the service itself is constructed within an injection context, and
  // because it's providedIn: 'root', that context - and so this effect - lives for the whole app session,
  // not just while the floating panel happens to be open.
  private lastIdentityKey = this.identityKeyOf();

  constructor() {
    effect(() => {
      const currentKey = this.identityKeyOf();
      if (currentKey !== this.lastIdentityKey) {
        this.lastIdentityKey = currentKey;
        this.resetConversation();
        this.isOpen.set(false);
      }
    });
  }

  private identityKeyOf(): string | null {
    const user = this.authService.currentUser();
    return user ? `${user.userId}:${user.tenantId ?? ''}` : null;
  }

  toggleOpen(): void {
    this.isOpen.update((open) => !open);
  }

  open(): void {
    this.isOpen.set(true);
  }

  close(): void {
    this.isOpen.set(false);
  }

  // The explicit "New conversation" action - invalidates any in-flight request (so a late response can
  // never repopulate the fresh conversation) and clears the transcript, but does NOT close the panel.
  resetConversation(): void {
    this.requestSequence += 1;
    this.messages.set([]);
    this.activeProposalId.set(null);
    this.isSending.set(false);
    this.lastFailedRequest = null;
  }

  sendMessage(text: string): void {
    const trimmed = text.trim();
    if (!trimmed || this.isSending()) {
      return;
    }

    const contextTurns = this.buildContextTurns();
    this.messages.update((messages) => [...messages, { kind: 'user', id: createMessageId(), text: trimmed }]);
    this.dispatchPropose(trimmed, contextTurns);
  }

  retryLastFailedRequest(): void {
    if (!this.lastFailedRequest || this.isSending()) {
      return;
    }
    const { text, contextTurns } = this.lastFailedRequest;
    this.dispatchPropose(text, contextTurns);
  }

  private dispatchPropose(text: string, contextTurns: ConversationTurn[]): void {
    this.isSending.set(true);
    this.lastFailedRequest = null;
    const mySequence = ++this.requestSequence;

    this.aiBookingService.propose({ text, priorTurns: contextTurns.length > 0 ? contextTurns : undefined }).subscribe({
      next: (result) => {
        if (mySequence !== this.requestSequence) {
          return; // superseded by a newer send(), a reset, or an identity change
        }
        this.isSending.set(false);
        this.applyProposeResult(result);
      },
      error: (error: unknown) => {
        if (mySequence !== this.requestSequence) {
          return;
        }
        this.isSending.set(false);
        this.lastFailedRequest = { text, contextTurns };
        const message = error instanceof HttpErrorResponse ? (toApiError(error).detail ?? toApiError(error).title) : 'Something went wrong.';
        this.messages.update((messages) => [...messages, { kind: 'assistant-text', id: createMessageId(), text: message, tone: 'error', retryable: true }]);
      },
    });
  }

  private applyProposeResult(result: ProposeBookingResult): void {
    switch (result.outcome) {
      case 'Proposed': {
        this.supersedeActiveProposal();
        const id = createMessageId();
        this.messages.update((messages) => [...messages, { kind: 'proposal', id, candidate: result.candidate!, status: 'pending' }]);
        this.activeProposalId.set(id);
        return;
      }
      case 'NeedsClarification':
        this.messages.update((messages) => [
          ...messages,
          { kind: 'assistant-text', id: createMessageId(), text: result.message ?? '', tone: 'clarification', retryable: false },
        ]);
        return;
      case 'NoSuitableResource':
      case 'Disabled':
      case 'ProviderUnavailable':
        this.messages.update((messages) => [
          ...messages,
          { kind: 'assistant-text', id: createMessageId(), text: result.message ?? '', tone: 'info', retryable: false },
        ]);
        return;
    }
  }

  confirmProposal(id: ChatMessageId): void {
    if (this.activeProposalId() !== id) {
      return; // not the active/actionable proposal (superseded, already resolved, or unknown)
    }
    const message = this.messages().find((m): m is Extract<ChatMessage, { kind: 'proposal' }> => m.kind === 'proposal' && m.id === id);
    if (!message || message.status !== 'pending') {
      return;
    }

    this.setProposalStatus(id, 'confirming');
    const candidate = message.candidate;
    const mySequence = this.requestSequence; // confirmation belongs to the CURRENT conversation generation

    this.bookingService
      .createBooking({ resourceId: candidate.resourceId, startUtc: candidate.startUtc, endUtc: candidate.endUtc, quantity: candidate.quantity })
      .subscribe({
        next: (response) => {
          if (mySequence !== this.requestSequence) {
            return; // the conversation was reset/identity changed while this confirmation was in flight
          }
          this.calendarService.invalidate();
          this.updateProposal(id, { status: response.status === 'Pending' ? 'pendingApproval' : 'confirmed', bookingId: response.id });
          if (this.activeProposalId() === id) {
            this.activeProposalId.set(null);
          }
        },
        error: (error: unknown) => {
          if (mySequence !== this.requestSequence) {
            return;
          }
          const apiError = error instanceof HttpErrorResponse ? toApiError(error) : null;
          const failureMessage = (apiError?.errorCode && CONFLICT_MESSAGES[apiError.errorCode]) ?? apiError?.detail ?? apiError?.title ?? 'Something went wrong. Please try again.';
          this.updateProposal(id, { status: 'failed', failureMessage });
          if (this.activeProposalId() === id) {
            this.activeProposalId.set(null);
          }
        },
      });
  }

  discardProposal(id: ChatMessageId): void {
    if (this.activeProposalId() !== id) {
      return;
    }
    this.setProposalStatus(id, 'discarded');
    this.activeProposalId.set(null);
  }

  private supersedeActiveProposal(): void {
    const activeId = this.activeProposalId();
    if (activeId === null) {
      return;
    }
    this.setProposalStatus(activeId, 'superseded');
    this.activeProposalId.set(null);
  }

  private setProposalStatus(id: ChatMessageId, status: ProposalStatus): void {
    this.updateProposal(id, { status });
  }

  private updateProposal(id: ChatMessageId, patch: Partial<Extract<ChatMessage, { kind: 'proposal' }>>): void {
    this.messages.update((messages) =>
      messages.map((message) => (message.kind === 'proposal' && message.id === id ? { ...message, ...patch } : message)),
    );
  }

  // Reduces the rich chat transcript down to the bounded, wire-level ConversationTurn[] the backend
  // contract expects - a sliding window of at most MAX_CONTEXT_TURNS, always the MOST RECENT turns (never
  // an unbounded transcript - see the task's own "keep provider context bounded" requirement). A
  // proposal's candidate.summary (not its live status/confirm-outcome, which the interpretation model has
  // no use for) stands in for what the assistant "said" in that turn, so a follow-up like "actually make
  // it 3 people" still has the prior candidate to refer back to.
  private buildContextTurns(): ConversationTurn[] {
    const turns: ConversationTurn[] = [];
    for (const message of this.messages()) {
      if (message.kind === 'user') {
        turns.push({ role: 'user', text: message.text });
      } else if (message.kind === 'assistant-text') {
        if (message.tone !== 'error') {
          // A transport/provider failure was never a real answer from the model - it carries no
          // interpretable content and must not be replayed back to it as if it had said something.
          turns.push({ role: 'assistant', text: message.text });
        }
      } else {
        turns.push({ role: 'assistant', text: message.candidate.summary });
      }
    }
    return turns.slice(-MAX_CONTEXT_TURNS);
  }
}
