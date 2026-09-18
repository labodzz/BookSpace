import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { EnvironmentInjector, createEnvironmentInjector, runInInjectionContext } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';
import { AuthTokens } from './auth.models';
import { AuthService } from './auth.service';
import { TokenStorageService } from './token-storage.service';

const TOKENS: AuthTokens = {
  accessToken: 'access-1',
  accessTokenExpiresAtUtc: new Date(Date.now() + 60_000).toISOString(),
  refreshToken: 'refresh-1',
  refreshTokenExpiresAtUtc: new Date(Date.now() + 86_400_000).toISOString(),
};

// Exposes the private fields the cross-tab tests need to reach into (the fake navigator.locks
// integration point, and the BroadcastChannel this service listens on) without making them public API.
type AuthServiceInternals = {
  broadcastChannel: BroadcastChannel | null;
};

describe('AuthService', () => {
  let service: AuthService;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([{ path: 'login', children: [] }])],
    });
    // Seeded BEFORE AuthService is injected: its `tokens` signal reads TokenStorageService.load() once,
    // in its own constructor, so the session must already be in storage by then.
    TestBed.inject(TokenStorageService).save(TOKENS, true);
    service = TestBed.inject(AuthService);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    localStorage.clear();
    sessionStorage.clear();
    delete (navigator as unknown as { locks?: unknown }).locks;
    httpTesting.verify();
  });

  it('sends the current refresh token to /auth/logout and clears local state', () => {
    service.logout();

    const req = httpTesting.expectOne(`${environment.apiUrl}/auth/logout`);
    expect(req.request.body).toEqual({ refreshToken: 'refresh-1' });
    req.flush(null, { status: 204, statusText: 'No Content' });

    expect(service.isAuthenticated()).toBe(false);
    expect(localStorage.getItem('bookspace.accessToken')).toBeNull();
  });

  it('does not call the API when there is no session to revoke', () => {
    service.logout();
    httpTesting.expectOne(`${environment.apiUrl}/auth/logout`).flush(null, { status: 204, statusText: 'No Content' });

    service.logout();

    httpTesting.expectNone(`${environment.apiUrl}/auth/logout`);
  });

  // The scenario this guards against: a refresh already in flight when logout() runs must not be
  // allowed to complete later and silently write fresh tokens back into storage, resurrecting a
  // session the user just explicitly ended.
  it('cancels an in-flight refresh on logout so its response cannot resurrect the cleared session', () => {
    service.refreshAccessToken().subscribe();
    const refreshRequest = httpTesting.expectOne(`${environment.apiUrl}/auth/refresh`);

    service.logout();
    httpTesting.expectOne(`${environment.apiUrl}/auth/logout`).flush(null, { status: 204, statusText: 'No Content' });

    expect(refreshRequest.cancelled).toBe(true);
    expect(service.isAuthenticated()).toBe(false);
  });

  // Three requests failing with 401 at once (e.g. a page firing several calls on load with an access
  // token that expired while the tab was idle) must not race to rotate the same refresh token three
  // times - httpTesting.expectOne below fails the test outright if more than one request was made.
  it('deduplicates concurrent refreshAccessToken calls within the same tab into a single HTTP request', async () => {
    const call1 = firstValueFrom(service.refreshAccessToken());
    const call2 = firstValueFrom(service.refreshAccessToken());
    const call3 = firstValueFrom(service.refreshAccessToken());

    const rotated: AuthTokens = {
      accessToken: 'access-2',
      accessTokenExpiresAtUtc: new Date(Date.now() + 60_000).toISOString(),
      refreshToken: 'refresh-2',
      refreshTokenExpiresAtUtc: new Date(Date.now() + 86_400_000).toISOString(),
    };
    // expectOne itself is the assertion that only one request was made - three redundant ones would
    // fail it outright.
    httpTesting.expectOne(`${environment.apiUrl}/auth/refresh`).flush(rotated);

    await expect(call1).resolves.toEqual(rotated);
    await expect(call2).resolves.toEqual(rotated);
    await expect(call3).resolves.toEqual(rotated);
  });

  // Simulates a sibling tab winning a cross-tab refresh race and writing its result to the shared
  // storage before this tab's own (possibly lock-queued) refresh attempt gets to run: this tab must
  // notice its in-memory token is already stale and adopt the newer one instead of firing a redundant,
  // now-doomed-to-lose request of its own.
  it('adopts tokens another tab already rotated in storage instead of firing a redundant refresh', async () => {
    const rotatedByOtherTab: AuthTokens = {
      accessToken: 'access-from-other-tab',
      accessTokenExpiresAtUtc: new Date(Date.now() + 60_000).toISOString(),
      refreshToken: 'refresh-from-other-tab',
      refreshTokenExpiresAtUtc: new Date(Date.now() + 86_400_000).toISOString(),
    };
    TestBed.inject(TokenStorageService).save(rotatedByOtherTab, true);

    const result = await firstValueFrom(service.refreshAccessToken());

    httpTesting.expectNone(`${environment.apiUrl}/auth/refresh`);
    expect(result).toEqual(rotatedByOtherTab);
    expect(service.currentAccessToken()).toBe(rotatedByOtherTab.accessToken);
  });

  it('clears the session when the backend confirms the attempted refresh token is invalid (401)', async () => {
    const attempt = firstValueFrom(service.refreshAccessToken());
    httpTesting.expectOne(`${environment.apiUrl}/auth/refresh`).flush('unauthorized', { status: 401, statusText: 'Unauthorized' });

    await expect(attempt).rejects.toBeTruthy();
    expect(service.isAuthenticated()).toBe(false);
    expect(localStorage.getItem('bookspace.accessToken')).toBeNull();
  });

  // The scenario this guards against (see AuthService.clearExpiredSession): a sibling tab's concurrent,
  // successful refresh gets broadcast and adopted here WHILE this tab's own now-doomed request (using
  // the OLD, pre-race token) is still in flight. When that old request's rejection finally arrives, it
  // must be recognized as stale - not treated as "the current session is invalid" - and must not tear
  // down the newer session that already replaced it, in memory or in storage.
  it('does not clear the session if a sibling tab already broadcast a newer one before this refresh was rejected', async () => {
    const attempt = firstValueFrom(service.refreshAccessToken());
    const refreshRequest = httpTesting.expectOne(`${environment.apiUrl}/auth/refresh`);

    const rotatedByOtherTab: AuthTokens = {
      accessToken: 'access-from-other-tab',
      accessTokenExpiresAtUtc: new Date(Date.now() + 60_000).toISOString(),
      refreshToken: 'refresh-from-other-tab',
      refreshTokenExpiresAtUtc: new Date(Date.now() + 86_400_000).toISOString(),
    };
    const channel = (service as unknown as AuthServiceInternals).broadcastChannel!;
    channel.onmessage!({ data: { type: 'tokens-updated', tokens: rotatedByOtherTab } } as MessageEvent);

    refreshRequest.flush('unauthorized', { status: 401, statusText: 'Unauthorized' });

    await expect(attempt).rejects.toBeTruthy();
    expect(service.currentAccessToken()).toBe(rotatedByOtherTab.accessToken);
    expect(localStorage.getItem('bookspace.accessToken')).toBe(rotatedByOtherTab.accessToken);
  });

  // jsdom (this test's environment) doesn't implement navigator.locks, so refreshAccessToken normally
  // falls back to running without one - this test stubs it in to prove the integration point is wired
  // correctly: the real lock name is requested, and the callback that does the actual work is the one
  // handed to it (not bypassed).
  it('runs the refresh through navigator.locks when the Web Locks API is available', () => {
    const lockRequest = vi.fn((_name: string, callback: () => Promise<unknown>) => callback());
    (navigator as unknown as { locks: { request: typeof lockRequest } }).locks = { request: lockRequest };

    service.refreshAccessToken().subscribe();

    expect(lockRequest).toHaveBeenCalledWith('bookspace-auth-refresh', expect.any(Function));
    httpTesting.expectOne(`${environment.apiUrl}/auth/refresh`).flush(TOKENS);
  });

  // If the lock callback returned a bare Observable instead of awaiting it into a real Promise (e.g. via
  // firstValueFrom), navigator.locks would treat that Observable object itself as an already-available
  // value and release the lock immediately - before the HTTP call even resolves - giving zero actual
  // mutual exclusion. This proves the lock is genuinely held until the refresh settles.
  it('keeps the cross-tab lock held until the HTTP refresh actually settles', async () => {
    let lockPromise!: Promise<unknown>;
    const lockRequest = vi.fn((_name: string, callback: () => Promise<unknown>) => {
      lockPromise = callback();
      return lockPromise;
    });
    (navigator as unknown as { locks: { request: typeof lockRequest } }).locks = { request: lockRequest };

    let settled = false;
    service.refreshAccessToken().subscribe();
    lockPromise.then(
      () => (settled = true),
      () => (settled = true),
    );

    // Let any pending microtasks run before the HTTP response is flushed - if the callback's promise
    // had already resolved (the bug this guards against), `settled` would be true here.
    await Promise.resolve();
    await Promise.resolve();
    expect(settled).toBe(false);

    httpTesting.expectOne(`${environment.apiUrl}/auth/refresh`).flush(TOKENS);
    await lockPromise;
    expect(settled).toBe(true);
  });

  // The scenario this guards against: without navigator.locks (older browsers, or this test simulating
  // that), two tabs sharing the same refresh token can both attempt to refresh at once. One wins at the
  // backend's RowVersion check; the other gets a 401 and internally tears down only its own state (see
  // AuthService.clearExpiredSession) - it never calls the server-hitting logout(), so nothing is
  // broadcast - meaning the winning tab's session must be completely unaffected by the loser's failure.
  it('does not let a losing tab without navigator.locks log out the winning tab', () => {
    delete (navigator as unknown as { locks?: unknown }).locks;

    const tabBInjector = createEnvironmentInjector(
      [provideHttpClient(), provideHttpClientTesting(), provideRouter([{ path: 'login', children: [] }])],
      TestBed.inject(EnvironmentInjector),
    );
    const tabB = runInInjectionContext(tabBInjector, () => new AuthService());
    const tabBHttpTesting = tabBInjector.get(HttpTestingController);

    // Both tabs still hold the same pre-race token and fire their refresh before either resolves - this
    // is the actual race: if Tab A's response landed in storage first, Tab B would just adopt it via the
    // re-sync check instead of ever reaching the network, which is correct behavior but would not
    // exercise the "backend already rejected it" path this test is specifically about.
    service.refreshAccessToken().subscribe();
    tabB.refreshAccessToken().subscribe({ error: () => undefined });

    // Tab A wins the race.
    const winningTokens: AuthTokens = {
      accessToken: 'access-winner',
      accessTokenExpiresAtUtc: new Date(Date.now() + 60_000).toISOString(),
      refreshToken: 'refresh-winner',
      refreshTokenExpiresAtUtc: new Date(Date.now() + 86_400_000).toISOString(),
    };
    httpTesting.expectOne(`${environment.apiUrl}/auth/refresh`).flush(winningTokens);

    // Tab B loses: the backend sees its token already rotated and returns 401, which internally clears
    // only Tab B's own state (see AuthService.clearExpiredSession) - never the server-hitting logout().
    tabBHttpTesting.expectOne(`${environment.apiUrl}/auth/refresh`).flush('unauthorized', { status: 401, statusText: 'Unauthorized' });

    expect(service.isAuthenticated()).toBe(true);
    expect(service.currentAccessToken()).toBe(winningTokens.accessToken);
    // Tab B's internal cleanup must not have wiped the shared storage Tab A just wrote its (currently
    // valid) session into - only Tab B's own stale copy of the old token is gone.
    expect(localStorage.getItem('bookspace.accessToken')).toBe(winningTokens.accessToken);

    tabBHttpTesting.verify();
  });

  it('broadcasts a logged-out message to other tabs when logout() runs', () => {
    const channel = (service as unknown as AuthServiceInternals).broadcastChannel!;
    const postMessage = vi.spyOn(channel, 'postMessage');

    service.logout();
    httpTesting.expectOne(`${environment.apiUrl}/auth/logout`).flush(null, { status: 204, statusText: 'No Content' });

    expect(postMessage).toHaveBeenCalledWith({ type: 'logged-out' });
  });

  // This is the mechanism behind "logout in one tab logs out the others": each tab's AuthService reacts
  // to a 'logged-out' broadcast from any tab (including one it didn't send itself) by tearing its own
  // session down, the same way it would for its own logout() call.
  it('clears its own session on receiving a logged-out broadcast from another tab', () => {
    const channel = (service as unknown as AuthServiceInternals).broadcastChannel!;

    channel.onmessage!({ data: { type: 'logged-out' } } as MessageEvent);

    expect(service.isAuthenticated()).toBe(false);
    expect(localStorage.getItem('bookspace.accessToken')).toBeNull();
  });

  // sessionStorage is per-tab, not shared like localStorage - if this tab were on a session-only login,
  // a broadcast that updated only the in-memory signal (not storage) would leave a stale token sitting
  // in this tab's own storage, which performRefresh's re-sync check could later mistake for "someone
  // else's newer" value and wrongly revert to. Persisting on receipt closes that gap.
  it('adopts tokens broadcast by another tab and persists them to this tab’s own storage', () => {
    const rotatedByOtherTab: AuthTokens = {
      accessToken: 'access-from-other-tab',
      accessTokenExpiresAtUtc: new Date(Date.now() + 60_000).toISOString(),
      refreshToken: 'refresh-from-other-tab',
      refreshTokenExpiresAtUtc: new Date(Date.now() + 86_400_000).toISOString(),
    };
    const channel = (service as unknown as AuthServiceInternals).broadcastChannel!;

    channel.onmessage!({ data: { type: 'tokens-updated', tokens: rotatedByOtherTab } } as MessageEvent);

    expect(service.currentAccessToken()).toBe(rotatedByOtherTab.accessToken);
    expect(service.isAuthenticated()).toBe(true);
    expect(localStorage.getItem('bookspace.accessToken')).toBe(rotatedByOtherTab.accessToken);
  });
});
