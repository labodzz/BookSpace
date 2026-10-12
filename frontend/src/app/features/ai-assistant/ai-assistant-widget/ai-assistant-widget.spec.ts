import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { environment } from '../../../../environments/environment';
import { AiAssistantWidgetComponent } from './ai-assistant-widget';

const PROPOSE_URL = `${environment.apiUrl}/ai-bookings/propose`;
const BOOKINGS_URL = `${environment.apiUrl}/bookings`;

const CANDIDATE = {
  resourceId: 'resource-1',
  resourceName: 'Lab A',
  timeZoneId: 'UTC',
  startUtc: '2026-11-10T14:00:00Z',
  endUtc: '2026-11-10T16:00:00Z',
  quantity: 1,
  resourceCapacity: 8,
  requiresApproval: false,
  summary: 'Lab A, 2026-11-10, 14:00-16:00 (UTC), quantity 1.',
};

describe('AiAssistantWidgetComponent', () => {
  let fixture: ComponentFixture<AiAssistantWidgetComponent>;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [AiAssistantWidgetComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([{ path: 'bookings', children: [] }])],
    });
    httpTesting = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(AiAssistantWidgetComponent);
    fixture.detectChanges();
  });

  afterEach(() => httpTesting.verify());

  function root(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function launcher(): HTMLButtonElement {
    return root().querySelector('.ai-assistant-launcher') as HTMLButtonElement;
  }

  function panel(): HTMLElement | null {
    return root().querySelector('.ai-assistant-panel');
  }

  function composer(): HTMLTextAreaElement {
    return root().querySelector('.ai-assistant-panel__composer textarea') as HTMLTextAreaElement;
  }

  function setComposerValue(value: string): void {
    const textarea = composer();
    textarea.value = value;
    textarea.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  function pressEnter(options: Partial<KeyboardEventInit> = {}): void {
    composer().dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true, ...options }));
    fixture.detectChanges();
  }

  function open(): void {
    launcher().click();
    fixture.detectChanges();
  }

  it('is closed by default and announces itself as a collapsed dialog launcher', () => {
    expect(panel()).toBeNull();
    expect(launcher().getAttribute('aria-expanded')).toBe('false');
    expect(launcher().getAttribute('aria-label')).toBe('AI booking assistant');
  });

  it('opens the panel on click, exposes aria-expanded/aria-controls, and shows the welcome message with suggestion chips', () => {
    open();

    expect(panel()).not.toBeNull();
    expect(launcher().getAttribute('aria-expanded')).toBe('true');
    expect(root().querySelector('[aria-controls]')?.getAttribute('aria-controls')).toBe('ai-assistant-panel');
    expect(panel()!.textContent).toContain('Hi! I can help you book a resource');
    const chips = [...root().querySelectorAll('.ai-assistant-panel__chip')].map((chip) => chip.textContent?.trim());
    expect(chips).toEqual(['Book a room', 'Find a lab for 3 people']);
  });

  it('clicking a suggestion chip fills the composer but does not send anything by itself', () => {
    open();

    (root().querySelector('.ai-assistant-panel__chip') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(composer().value).toBe('Book a room');
    httpTesting.expectNone(PROPOSE_URL);
  });

  it('moves focus into the composer on open, and back to the launcher on close', () => {
    open();
    expect(document.activeElement).toBe(composer());

    (root().querySelector('.ai-assistant-panel__close') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(panel()).toBeNull();
    expect(document.activeElement).toBe(launcher());
  });

  it('Escape closes the panel', () => {
    open();
    panel()!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));
    fixture.detectChanges();

    expect(panel()).toBeNull();
  });

  it('sends on Enter, shows the user bubble immediately, and shows a typing indicator while the request is in flight', () => {
    open();
    setComposerValue('Book a lab for 3 people next Tuesday from 2 to 4 PM.');

    pressEnter();

    expect(composer().value).toBe('');
    expect(panel()!.textContent).toContain('Book a lab for 3 people next Tuesday from 2 to 4 PM.');
    expect(root().querySelector('.ai-assistant-panel__typing')).not.toBeNull();

    httpTesting.expectOne(PROPOSE_URL).flush({ outcome: 0, language: 'en', message: null, candidate: CANDIDATE });
    fixture.detectChanges();

    expect(root().querySelector('.ai-assistant-panel__typing')).toBeNull();
    expect(root().querySelector('app-ai-proposal-card')).not.toBeNull();
  });

  it('Shift+Enter inserts a newline instead of sending', () => {
    open();
    setComposerValue('line one');

    pressEnter({ shiftKey: true });

    httpTesting.expectNone(PROPOSE_URL);
  });

  it('does not send while an IME composition is in progress, even on a plain Enter keydown', () => {
    open();
    setComposerValue('ćevapi');

    composer().dispatchEvent(new Event('compositionstart'));
    pressEnter();
    httpTesting.expectNone(PROPOSE_URL);

    composer().dispatchEvent(new Event('compositionend'));
    pressEnter();
    httpTesting.expectOne(PROPOSE_URL).flush({ outcome: 1, language: 'bs', message: 'Koji datum?', candidate: null });
  });

  it('disables the send button while a request is already in flight, preventing a duplicate submission', () => {
    open();
    setComposerValue('Book a lab tomorrow.');
    pressEnter();

    const sendButton = root().querySelector('.ai-assistant-panel__send') as HTMLButtonElement;
    expect(sendButton.disabled).toBe(true);

    httpTesting.expectOne(PROPOSE_URL).flush({ outcome: 1, language: 'en', message: 'What time?', candidate: null });
  });

  it('preserves the conversation when the panel is closed and reopened', () => {
    open();
    setComposerValue('Book a lab tomorrow.');
    pressEnter();
    httpTesting.expectOne(PROPOSE_URL).flush({ outcome: 1, language: 'en', message: 'What time would you like?', candidate: null });
    fixture.detectChanges();

    (root().querySelector('.ai-assistant-panel__close') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(panel()).toBeNull();

    open();
    expect(panel()!.textContent).toContain('Book a lab tomorrow.');
    expect(panel()!.textContent).toContain('What time would you like?');
  });

  it('shows a proposal card, confirms it via the EXISTING POST /api/bookings, and marks it booked afterward', () => {
    open();
    setComposerValue('Book a lab tomorrow at 2pm for 2 hours.');
    pressEnter();
    httpTesting.expectOne(PROPOSE_URL).flush({ outcome: 0, language: 'en', message: null, candidate: CANDIDATE });
    fixture.detectChanges();

    httpTesting.expectNone(BOOKINGS_URL); // nothing booked merely because the card appeared

    const confirmButton = [...root().querySelectorAll('button')].find((b) => b.textContent?.includes('Confirm booking')) as HTMLButtonElement;
    confirmButton.click();
    fixture.detectChanges();

    const req = httpTesting.expectOne(BOOKINGS_URL);
    expect(req.request.body).toEqual({ resourceId: 'resource-1', startUtc: CANDIDATE.startUtc, endUtc: CANDIDATE.endUtc, quantity: 1 });
    req.flush({ id: 'booking-1', resourceId: 'resource-1', startUtc: CANDIDATE.startUtc, endUtc: CANDIDATE.endUtc, quantity: 1, status: 1 /* Confirmed */, timeZoneId: 'UTC' });
    fixture.detectChanges();

    expect(panel()!.textContent).toContain('Booking confirmed.');
    expect(panel()!.textContent).toContain('View in My Bookings');
    expect([...root().querySelectorAll('button')].some((b) => b.textContent?.includes('Confirm booking'))).toBe(false);
  });

  it('discarding a proposal dismisses it without booking anything, and keeps the conversation open for another message', () => {
    open();
    setComposerValue('Book a lab tomorrow.');
    pressEnter();
    httpTesting.expectOne(PROPOSE_URL).flush({ outcome: 0, language: 'en', message: null, candidate: CANDIDATE });
    fixture.detectChanges();

    const discardButton = [...root().querySelectorAll('button')].find((b) => b.textContent?.includes('Discard')) as HTMLButtonElement;
    discardButton.click();
    fixture.detectChanges();

    expect(panel()!.textContent).toContain('Discarded.');
    httpTesting.expectNone(BOOKINGS_URL);
  });

  it('shows a retryable error bubble on a propose() failure, and Retry resends the same text', () => {
    open();
    setComposerValue('Book a lab tomorrow.');
    pressEnter();
    httpTesting.expectOne(PROPOSE_URL).error(new ProgressEvent('error'), { status: 0, statusText: 'Unknown Error' });
    fixture.detectChanges();

    const retryButton = [...root().querySelectorAll('button')].find((b) => b.textContent?.includes('Retry')) as HTMLButtonElement;
    retryButton.click();
    fixture.detectChanges();

    const retryReq = httpTesting.expectOne(PROPOSE_URL);
    expect(retryReq.request.body.text).toBe('Book a lab tomorrow.');
    retryReq.flush({ outcome: 0, language: 'en', message: null, candidate: CANDIDATE });
  });

  it('"New conversation" clears the transcript back to the welcome state', () => {
    open();
    setComposerValue('Book a lab tomorrow.');
    pressEnter();
    httpTesting.expectOne(PROPOSE_URL).flush({ outcome: 1, language: 'en', message: 'What time?', candidate: null });
    fixture.detectChanges();

    (root().querySelector('.ai-assistant-panel__new-conversation') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(panel()!.textContent).toContain('Hi! I can help you book a resource');
    expect(panel()!.textContent).not.toContain('What time?');
  });
});
