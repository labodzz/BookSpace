import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { environment } from '../../../environments/environment';
import { AuthTokens } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { TokenStorageService } from '../../core/auth/token-storage.service';
import { AiAssistantService, MAX_CONTEXT_TURNS } from './ai-assistant.service';

const PROPOSE_URL = `${environment.apiUrl}/ai-bookings/propose`;
const BOOKINGS_URL = `${environment.apiUrl}/bookings`;

const ROLE_CLAIM_TYPE = 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role';

function tokenWithPayload(payload: Record<string, unknown>): string {
  const base64Url = (value: string) => btoa(value).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  const header = base64Url(JSON.stringify({ alg: 'HS256', typ: 'JWT' }));
  const body = base64Url(JSON.stringify(payload));
  return `${header}.${body}.signature-not-verified-client-side`;
}

function tokensFor(userId: string, tenantId: string | null, roles: string[] = ['Member']): AuthTokens {
  return {
    accessToken: tokenWithPayload({ sub: userId, email: `${userId}@bookspace.test`, tenant_id: tenantId, [ROLE_CLAIM_TYPE]: roles }),
    accessTokenExpiresAtUtc: new Date(Date.now() + 60_000).toISOString(),
    refreshToken: `refresh-${userId}`,
    refreshTokenExpiresAtUtc: new Date(Date.now() + 86_400_000).toISOString(),
  };
}

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

describe('AiAssistantService', () => {
  let service: AiAssistantService;
  let httpTesting: HttpTestingController;
  let tokenStorage: TokenStorageService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    tokenStorage = TestBed.inject(TokenStorageService);
    tokenStorage.save(tokensFor('user-1', 'tenant-1'), true);
    service = TestBed.inject(AiAssistantService);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    localStorage.clear();
    sessionStorage.clear();
    httpTesting.verify();
  });

  it('sends a message, appends the user bubble immediately, and applies a Proposed outcome', () => {
    service.sendMessage('Book a lab for 3 people next Tuesday from 2 to 4 PM.');

    expect(service.messages()).toEqual([{ kind: 'user', id: expect.any(String), text: 'Book a lab for 3 people next Tuesday from 2 to 4 PM.' }]);

    const req = httpTesting.expectOne(PROPOSE_URL);
    req.flush({ outcome: 0, language: 'en', message: null, candidate: CANDIDATE });

    expect(service.messages()).toHaveLength(2);
    const proposal = service.messages()[1];
    expect(proposal.kind).toBe('proposal');
    expect(service.activeProposalId()).toBe(proposal.id);
  });

  it('never calls POST /api/bookings merely because a proposal was shown', () => {
    service.sendMessage('Book a lab tomorrow at 2pm for 2 hours.');
    httpTesting.expectOne(PROPOSE_URL).flush({ outcome: 0, language: 'en', message: null, candidate: CANDIDATE });

    httpTesting.expectNone(BOOKINGS_URL);
  });

  it('confirming a proposal calls the EXISTING POST /api/bookings with exactly the candidate fields, only after explicit confirmation', () => {
    service.sendMessage('Book a lab tomorrow at 2pm for 2 hours.');
    httpTesting.expectOne(PROPOSE_URL).flush({ outcome: 0, language: 'en', message: null, candidate: CANDIDATE });
    const proposalId = service.messages()[1].id;

    service.confirmProposal(proposalId);

    const req = httpTesting.expectOne(BOOKINGS_URL);
    expect(req.request.body).toEqual({ resourceId: 'resource-1', startUtc: CANDIDATE.startUtc, endUtc: CANDIDATE.endUtc, quantity: 1 });
    req.flush({ id: 'booking-1', resourceId: 'resource-1', startUtc: CANDIDATE.startUtc, endUtc: CANDIDATE.endUtc, quantity: 1, status: 1 /* Confirmed */, timeZoneId: 'UTC' });

    const proposal = service.messages()[1];
    expect(proposal.kind === 'proposal' && proposal.status).toBe('confirmed');
    expect(service.activeProposalId()).toBeNull();
  });

  it('shows a Pending approval status when the booking requires approval, without a second confirm being possible', () => {
    service.sendMessage('Book the approval room tomorrow.');
    httpTesting.expectOne(PROPOSE_URL).flush({ outcome: 0, language: 'en', message: null, candidate: { ...CANDIDATE, requiresApproval: true } });
    const proposalId = service.messages()[1].id;

    service.confirmProposal(proposalId);
    httpTesting.expectOne(BOOKINGS_URL).flush({ id: 'booking-1', resourceId: 'resource-1', startUtc: CANDIDATE.startUtc, endUtc: CANDIDATE.endUtc, quantity: 1, status: 0 /* Pending */, timeZoneId: 'UTC' });

    const proposal = service.messages()[1];
    expect(proposal.kind === 'proposal' && proposal.status).toBe('pendingApproval');

    // Permanently disabled - confirming again must not be possible even if something tried.
    service.confirmProposal(proposalId);
    httpTesting.expectNone(BOOKINGS_URL);
  });

  it('prevents a duplicate confirm while one is already in flight for the same proposal', () => {
    service.sendMessage('Book a lab tomorrow at 2pm.');
    httpTesting.expectOne(PROPOSE_URL).flush({ outcome: 0, language: 'en', message: null, candidate: CANDIDATE });
    const proposalId = service.messages()[1].id;

    service.confirmProposal(proposalId);
    service.confirmProposal(proposalId); // a second click while the first is still in flight

    httpTesting.expectOne(BOOKINGS_URL).flush({ id: 'booking-1', resourceId: 'resource-1', startUtc: CANDIDATE.startUtc, endUtc: CANDIDATE.endUtc, quantity: 1, status: 1 /* Confirmed */, timeZoneId: 'UTC' });
  });

  it('marks a proposal failed (not success) when the slot becomes unavailable on confirm, and allows asking again', () => {
    service.sendMessage('Book a lab tomorrow at 2pm.');
    httpTesting.expectOne(PROPOSE_URL).flush({ outcome: 0, language: 'en', message: null, candidate: CANDIDATE });
    const firstProposalId = service.messages()[1].id;

    service.confirmProposal(firstProposalId);
    httpTesting.expectOne(BOOKINGS_URL).flush(
      { title: 'Conflict', status: 409, errorCode: 'Booking.CapacityExceeded' },
      { status: 409, statusText: 'Conflict' },
    );

    const failed = service.messages()[1];
    expect(failed.kind === 'proposal' && failed.status).toBe('failed');
    expect(failed.kind === 'proposal' && failed.failureMessage).toContain('taken by someone else');
    expect(service.activeProposalId()).toBeNull();

    // Asking again produces a brand new, independently actionable proposal.
    service.sendMessage('Try a different lab instead.');
    httpTesting.expectOne(PROPOSE_URL).flush({ outcome: 0, language: 'en', message: null, candidate: { ...CANDIDATE, resourceId: 'resource-2' } });
    const secondProposalId = service.messages()[3].id;
    expect(service.activeProposalId()).toBe(secondProposalId);
  });

  it('marks an earlier pending proposal superseded, and no longer confirmable, once a newer one is proposed', () => {
    service.sendMessage('Book a lab tomorrow.');
    httpTesting.expectOne(PROPOSE_URL).flush({ outcome: 0, language: 'en', message: null, candidate: CANDIDATE });
    const firstProposalId = service.messages()[1].id;

    service.sendMessage('Actually, make it Wednesday instead.');
    httpTesting.expectOne(PROPOSE_URL).flush({ outcome: 0, language: 'en', message: null, candidate: { ...CANDIDATE, resourceId: 'resource-2' } });

    const supersededFirst = service.messages()[1];
    expect(supersededFirst.kind === 'proposal' && supersededFirst.status).toBe('superseded');
    expect(service.activeProposalId()).not.toBe(firstProposalId);

    // The now-superseded card can never be confirmed, even if something still tried.
    service.confirmProposal(firstProposalId);
    httpTesting.expectNone(BOOKINGS_URL);
  });

  it('discarding a proposal dismisses only that card and keeps the conversation going', () => {
    service.sendMessage('Book a lab tomorrow.');
    httpTesting.expectOne(PROPOSE_URL).flush({ outcome: 0, language: 'en', message: null, candidate: CANDIDATE });
    const proposalId = service.messages()[1].id;

    service.discardProposal(proposalId);

    const discarded = service.messages()[1];
    expect(discarded.kind === 'proposal' && discarded.status).toBe('discarded');
    expect(service.activeProposalId()).toBeNull();
    expect(service.messages()).toHaveLength(2); // conversation itself is untouched, not cleared
  });

  it('passes a Bosnian clarification question through as assistant context for the next request', () => {
    service.sendMessage('Treba mi laboratorija sutra.');
    httpTesting.expectOne(PROPOSE_URL).flush({ outcome: 1, language: 'bs', message: 'U koliko sati i koliko dugo?', candidate: null });

    service.sendMessage('Od 14 do 16, za 3 osobe.');
    const secondRequest = httpTesting.expectOne(PROPOSE_URL);
    expect(secondRequest.request.body.priorTurns).toEqual([
      { role: 'user', text: 'Treba mi laboratorija sutra.' },
      { role: 'assistant', text: 'U koliko sati i koliko dugo?' },
    ]);
    secondRequest.flush({ outcome: 0, language: 'bs', message: null, candidate: CANDIDATE });
  });

  it('keeps the context sent to the backend within MAX_CONTEXT_TURNS, as a sliding window of the most recent turns', () => {
    for (let round = 1; round <= MAX_CONTEXT_TURNS; round += 1) {
      service.sendMessage(`message ${round}`);
      httpTesting.expectOne(PROPOSE_URL).flush({ outcome: 1, language: 'en', message: `question ${round}`, candidate: null });
    }

    service.sendMessage('final message');
    const finalRequest = httpTesting.expectOne(PROPOSE_URL);
    expect(finalRequest.request.body.priorTurns).toHaveLength(MAX_CONTEXT_TURNS);
    // The oldest turn (the very first user message) has fallen out of the window.
    expect(finalRequest.request.body.priorTurns).not.toContainEqual({ role: 'user', text: 'message 1' });
    expect(finalRequest.request.body.priorTurns[MAX_CONTEXT_TURNS - 1]).toEqual({ role: 'assistant', text: `question ${MAX_CONTEXT_TURNS}` });
    finalRequest.flush({ outcome: 1, language: 'en', message: 'one more question', candidate: null });
  });

  it('shows a retryable error message on a propose() network/provider failure, and retry resends the same text', () => {
    service.sendMessage('Book a lab tomorrow.');
    httpTesting.expectOne(PROPOSE_URL).error(new ProgressEvent('error'), { status: 0, statusText: 'Unknown Error' });

    const errorMessage = service.messages()[1];
    expect(errorMessage.kind === 'assistant-text' && errorMessage.tone).toBe('error');
    expect(errorMessage.kind === 'assistant-text' && errorMessage.retryable).toBe(true);

    service.retryLastFailedRequest();
    const retryReq = httpTesting.expectOne(PROPOSE_URL);
    expect(retryReq.request.body.text).toBe('Book a lab tomorrow.');
    retryReq.flush({ outcome: 0, language: 'en', message: null, candidate: CANDIDATE });
  });

  it('ignores a late propose() response from a superseded request after a new conversation was started', () => {
    service.sendMessage('first request');
    const firstRequest = httpTesting.expectOne(PROPOSE_URL);

    service.resetConversation();
    service.sendMessage('second request, new conversation');
    const secondRequest = httpTesting.expectOne(PROPOSE_URL);
    secondRequest.flush({ outcome: 0, language: 'en', message: null, candidate: CANDIDATE });

    // The first conversation's late response must never repopulate the new one.
    firstRequest.flush({ outcome: 0, language: 'en', message: null, candidate: { ...CANDIDATE, resourceId: 'stale-resource' } });

    expect(service.messages().some((m) => m.kind === 'proposal' && m.candidate.resourceId === 'stale-resource')).toBe(false);
  });

  it('ignores a late confirm() response after the conversation was reset in the meantime', () => {
    service.sendMessage('Book a lab tomorrow.');
    httpTesting.expectOne(PROPOSE_URL).flush({ outcome: 0, language: 'en', message: null, candidate: CANDIDATE });
    const proposalId = service.messages()[1].id;

    service.confirmProposal(proposalId);
    const confirmRequest = httpTesting.expectOne(BOOKINGS_URL);

    service.resetConversation();
    confirmRequest.flush({ id: 'booking-1', resourceId: 'resource-1', startUtc: CANDIDATE.startUtc, endUtc: CANDIDATE.endUtc, quantity: 1, status: 1 /* Confirmed */, timeZoneId: 'UTC' });

    expect(service.messages()).toEqual([]); // the reset conversation must stay empty, not be repopulated
  });

  it('clears the conversation and closes the panel on logout', () => {
    service.sendMessage('Book a lab tomorrow.');
    httpTesting.expectOne(PROPOSE_URL).flush({ outcome: 1, language: 'en', message: 'What date?', candidate: null });
    service.open();
    expect(service.messages()).toHaveLength(2);

    TestBed.inject(AuthService).logout();
    httpTesting.expectOne(`${environment.apiUrl}/auth/logout`).flush(null, { status: 204, statusText: 'No Content' });
    TestBed.tick();

    expect(service.messages()).toEqual([]);
    expect(service.isOpen()).toBe(false);
  });

  it('clears the conversation when a different user logs in without a full page reload', () => {
    service.sendMessage('Book a lab tomorrow.');
    httpTesting.expectOne(PROPOSE_URL).flush({ outcome: 1, language: 'en', message: 'What date?', candidate: null });
    expect(service.messages()).toHaveLength(2);

    const authService = TestBed.inject(AuthService);
    authService.login('user2@bookspace.test', 'Password1!', true).subscribe();
    httpTesting.expectOne(`${environment.apiUrl}/auth/login`).flush(tokensFor('user-2', 'tenant-1'));
    TestBed.tick();

    expect(service.messages()).toEqual([]);
    expect(authService.currentUser()?.userId).toBe('user-2');
  });

  it('never persists conversation text to localStorage', () => {
    service.sendMessage('Book a lab tomorrow for a very specific confidential project.');
    httpTesting.expectOne(PROPOSE_URL).flush({ outcome: 1, language: 'en', message: 'What date?', candidate: null });

    const stored = Object.values(localStorage).join(' ') + Object.values(sessionStorage).join(' ');
    expect(stored).not.toContain('confidential project');
  });
});
