import { DatePipe } from '@angular/common';
import { Component, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ProposalChatMessage } from '../ai-assistant.models';

// Purely presentational - renders one proposal message and reports Confirm/Discard back to the parent
// (AiAssistantWidgetComponent, backed by AiAssistantService, which owns every actual state transition).
// Every value shown here is already resolved by the backend (resource NAME, not id; local wall-clock
// times via the `date` pipe, not raw UTC strings) - see the task's own "must be understandable without
// reading raw UTC values or resource IDs."
@Component({
  selector: 'app-ai-proposal-card',
  imports: [DatePipe, RouterLink],
  templateUrl: './ai-proposal-card.html',
  styleUrl: './ai-proposal-card.scss',
})
export class AiProposalCardComponent {
  readonly message = input.required<ProposalChatMessage>();
  // Whether THIS card is the one actionable proposal in the conversation right now - a superseded/
  // resolved card never renders Confirm/Discard regardless of its own stored `status`, so a message that
  // arrives (or state that changes) after this one can never leave two cards simultaneously actionable.
  readonly isActive = input.required<boolean>();

  readonly confirmRequested = output<void>();
  readonly discardRequested = output<void>();
}
