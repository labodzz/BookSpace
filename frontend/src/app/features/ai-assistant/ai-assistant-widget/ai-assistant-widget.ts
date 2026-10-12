import { Component, ElementRef, computed, effect, inject, signal, viewChild } from '@angular/core';
import { AiAssistantService, MAX_TEXT_LENGTH } from '../ai-assistant.service';
import { AiProposalCardComponent } from '../ai-proposal-card/ai-proposal-card';
import { ChatMessageId } from '../ai-assistant.models';

const SUGGESTIONS = ['Book a room', 'Find a lab for 3 people'];

// Distance (px) from the bottom of the scrollable message list within which a new message still
// auto-scrolls into view - see onScroll()/the scroll-to-bottom effect below for "do not force-scroll
// users who are reading earlier messages."
const NEAR_BOTTOM_THRESHOLD_PX = 48;

// The floating assistant launcher button AND its chat panel, together - one component rather than two so
// focus management (return focus to the launcher on close) and the open/closed transition are trivially
// co-located, with no cross-component coordination needed. Rendered ONCE by ShellComponent (gated by
// !isSysAdmin there, alongside the rest of its tenant-business nav - see shell.html) - the shell persists
// across every child-route navigation, so this widget is never recreated just by navigating between
// tenant pages, which is what makes "preserve conversation across navigation" and "don't create multiple
// assistant instances" both true for free.
//
// Deliberately NOT a modal: there is no backdrop, the rest of the page stays interactive while the panel
// is open, and no focus trap is implemented - see the task's own "use modal semantics and focus trapping
// only if the panel actually behaves as a modal." role="dialog" + aria-label is still used (it IS a
// distinct, named, closable UI surface), just without aria-modal.
//
// Every actual conversation message is bound with Angular's default text interpolation ({{ }}), never
// [innerHTML] - model/user text is rendered as plain text always, by construction, not by a sanitization
// step that could be gotten wrong (see the task's "render model/user text as text by default. Do not
// inject raw HTML").
@Component({
  selector: 'app-ai-assistant-widget',
  imports: [AiProposalCardComponent],
  templateUrl: './ai-assistant-widget.html',
  styleUrl: './ai-assistant-widget.scss',
})
export class AiAssistantWidgetComponent {
  protected readonly service = inject(AiAssistantService);
  protected readonly maxTextLength = MAX_TEXT_LENGTH;
  protected readonly suggestions = SUGGESTIONS;

  protected readonly draftText = signal('');
  protected readonly showNewMessagesIndicator = signal(false);

  private readonly launcherRef = viewChild<ElementRef<HTMLButtonElement>>('launcher');
  private readonly messageListRef = viewChild<ElementRef<HTMLDivElement>>('messageList');
  private readonly composerRef = viewChild<ElementRef<HTMLTextAreaElement>>('composer');

  // True IME composition (e.g. typing Bosnian diacritics, or any CJK input method) must not let an
  // Enter keystroke that merely commits a composed character also submit the message - see
  // onComposerKeydown(). Kept as a plain field, not a signal: set directly from the template's
  // (compositionstart)/(compositionend) bindings on the one textarea this component owns, and read
  // synchronously inside the keydown handler below - never reactive, so a signal would be the wrong tool.
  protected isComposing = false;

  // Whether the message list was scrolled near its bottom the last time the user (or the initial render)
  // touched its scroll position - read, never itself reactive, by the scroll-to-bottom effect below, so a
  // user who has scrolled UP to re-read earlier messages is never yanked back down by a new message.
  private isNearBottom = true;

  private isFirstFocusEffectRun = true;

  protected readonly liveAnnouncement = computed(() => {
    if (this.service.isSending()) {
      return 'Assistant is typing…';
    }
    const messages = this.service.messages();
    const last = messages.at(-1);
    if (!last || last.kind === 'user') {
      return '';
    }
    return last.kind === 'assistant-text' ? last.text : `Proposal: ${last.candidate.summary}`;
  });

  constructor() {
    // Focus management: moves focus INTO the panel's composer the moment it opens, and back to the
    // launcher the moment it closes - see the task's "appropriate focus management when opening/closing"
    // and "return focus to the launcher when closed." Skipped on the very first run (effects always run
    // once immediately): the widget is mounted closed on every page load, and must never steal focus to
    // the (not-yet-interacted-with) launcher button just because the component was created.
    effect(() => {
      const isOpen = this.service.isOpen();
      if (this.isFirstFocusEffectRun) {
        this.isFirstFocusEffectRun = false;
        return;
      }
      // No queueMicrotask/setTimeout here: Angular only runs an effect's body after change detection
      // has already synced the @if block's DOM for the CURRENT signal values, so the composer/launcher
      // element this reads for is already present by the time this runs.
      if (isOpen) {
        this.composerRef()?.nativeElement.focus();
      } else {
        this.launcherRef()?.nativeElement.focus();
      }
    });

    // Scroll-to-bottom: re-evaluated whenever the panel opens, a message is added/updated, or the
    // typing indicator appears - but only actually scrolls if the user was already near the bottom (or
    // this is the panel freshly opening onto existing history); otherwise it surfaces the "new messages"
    // indicator instead of yanking a reading user's scroll position - see the task's own scrolling rules.
    effect(() => {
      // messageCount/isSending are read only to be tracked as this effect's dependencies - a new message
      // or the typing indicator appearing/disappearing is exactly what should re-run this.
      const isOpen = this.service.isOpen();
      const messageCount = this.service.messages().length;
      const isSending = this.service.isSending();
      if (!isOpen || (messageCount === 0 && !isSending)) {
        return;
      }
      const container = this.messageListRef()?.nativeElement;
      if (!container) {
        return;
      }
      if (this.isNearBottom) {
        container.scrollTop = container.scrollHeight;
        this.showNewMessagesIndicator.set(false);
      } else {
        this.showNewMessagesIndicator.set(true);
      }
    });
  }

  protected toggle(): void {
    this.service.toggleOpen();
  }

  protected close(): void {
    this.service.close();
  }

  protected onScroll(): void {
    const container = this.messageListRef()?.nativeElement;
    if (!container) {
      return;
    }
    this.isNearBottom = container.scrollTop + container.clientHeight >= container.scrollHeight - NEAR_BOTTOM_THRESHOLD_PX;
    if (this.isNearBottom) {
      this.showNewMessagesIndicator.set(false);
    }
  }

  protected scrollToBottomNow(): void {
    const container = this.messageListRef()?.nativeElement;
    if (container) {
      container.scrollTop = container.scrollHeight;
    }
    this.isNearBottom = true;
    this.showNewMessagesIndicator.set(false);
  }

  protected useSuggestion(text: string): void {
    this.draftText.set(text);
    this.composerRef()?.nativeElement.focus();
  }

  protected onComposerInput(event: Event): void {
    this.draftText.set((event.target as HTMLTextAreaElement).value);
  }

  protected onComposerKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && !event.shiftKey && !event.isComposing && !this.isComposing) {
      event.preventDefault();
      this.send();
    }
  }

  protected send(): void {
    if (!this.draftText().trim() || this.service.isSending()) {
      return;
    }
    this.service.sendMessage(this.draftText());
    this.draftText.set('');
    this.forceScrollToBottom();
  }

  protected retry(): void {
    this.service.retryLastFailedRequest();
    this.forceScrollToBottom();
  }

  // Sending (or retrying) always lands the user back at the bottom, regardless of where they had
  // scrolled - the reactive effect above already does this for an INCOMING message when the user was
  // already near the bottom, but a real browser's layout/paint can lag one tick behind the signal update
  // it reacts to; calling this directly, synchronously AND once more on the next animation frame, is the
  // belt-and-suspenders fix for OWN sends.
  private forceScrollToBottom(): void {
    this.scrollToBottomNow();
    if (typeof requestAnimationFrame === 'function') {
      requestAnimationFrame(() => this.scrollToBottomNow());
    }
  }

  protected newConversation(): void {
    this.service.resetConversation();
    this.draftText.set('');
    this.isNearBottom = true;
  }

  protected confirm(id: ChatMessageId): void {
    this.service.confirmProposal(id);
  }

  protected discard(id: ChatMessageId): void {
    this.service.discardProposal(id);
  }
}
