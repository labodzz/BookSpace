import { HttpClient, HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { Subject, TimeoutError, of, throwError } from 'rxjs';
import { authInterceptor, isApiRequest } from './auth.interceptor';
import { AuthService } from '../auth/auth.service';
import { environment } from '../../../environments/environment';

describe('isApiRequest', () => {
  it('matches a URL that starts with a configured, non-empty apiUrl', () => {
    expect(isApiRequest('http://localhost:5185/bookings', 'http://localhost:5185')).toBe(true);
  });

  it('does not match a relative URL when apiUrl is configured and non-empty', () => {
    // A relative URL never "starts with" an absolute apiUrl - only an exact-prefix match counts as
    // the API once apiUrl is a real host.
    expect(isApiRequest('/bookings', 'http://localhost:5185')).toBe(false);
  });

  it('does not match a URL for a different host than the configured apiUrl', () => {
    expect(isApiRequest('http://localhost:9999/bookings', 'http://localhost:5185')).toBe(false);
  });

  // apiUrl: '' is the production shape (API served from the same origin as the app, not a separate
  // host) - this is the exact branch that regressed: `''.startsWith('')` being vacuously true for
  // every string used to make every request look like an API request.
  it('matches a relative URL when apiUrl is empty (same-origin deployment)', () => {
    expect(isApiRequest('/bookings', '')).toBe(true);
  });

  it('matches an absolute same-origin URL when apiUrl is empty', () => {
    expect(isApiRequest(`${location.origin}/bookings`, '')).toBe(true);
  });

  it('does NOT match an absolute cross-origin URL when apiUrl is empty', () => {
    expect(isApiRequest('https://analytics.example.test/track', '')).toBe(false);
  });

  // A raw `url.startsWith(apiUrl)` check would wrongly treat this as belonging to the real API, since
  // one string literally starts with the other - only a real origin comparison catches it.
  it('does not match a lookalike hostname that merely starts with the configured API origin', () => {
    expect(isApiRequest('https://real-api.example.com.attacker.example/path', 'https://real-api.example.com')).toBe(false);
  });

  it('matches a request under the configured API origin AND base path', () => {
    expect(isApiRequest('http://localhost:5185/api/bookings', 'http://localhost:5185/api')).toBe(true);
  });

  // Same origin as the configured API, but the path only shares a string prefix with the base path
  // ('/api') rather than actually falling under it - must not match.
  it('does not match the correct origin with a base path that only looks like a prefix match', () => {
    expect(isApiRequest('http://localhost:5185/apiextra/bookings', 'http://localhost:5185/api')).toBe(false);
  });

  it('does not treat a protocol-relative URL as a safe relative same-origin path', () => {
    expect(isApiRequest('//attacker.example/path', '')).toBe(false);
  });

  it('does not treat a protocol-relative URL as matching a configured API origin', () => {
    expect(isApiRequest('//real-api.example.com/path', 'https://real-api.example.com')).toBe(false);
  });

  // Some test/embedded environments report no reliable document origin at all (an absent `location`,
  // or the opaque "null" origin of a sandboxed/non-http(s) document) - classification must fail closed
  // rather than throw or guess.
  it('fails closed on an absolute same-origin-shaped URL when the document origin is unavailable', () => {
    expect(isApiRequest('http://localhost:5185/bookings', '', null)).toBe(false);
  });

  it('still classifies a genuinely relative URL correctly even when the document origin is unavailable', () => {
    expect(isApiRequest('/bookings', '', null)).toBe(true);
  });

  // apiUrl: '/api' is the same-origin-with-prefix production shape (see environment.ts) - a relative
  // request path must fall under that base path, not merely be any same-origin request.
  it('matches a relative URL under a bare-path apiUrl', () => {
    expect(isApiRequest('/api/bookings', '/api')).toBe(true);
  });

  it('does not match a relative URL outside a bare-path apiUrl', () => {
    expect(isApiRequest('/bookings', '/api')).toBe(false);
  });

  // Same reasoning as the absolute-URL lookalike-prefix case above: '/apiextra' merely shares a string
  // prefix with '/api', it is not a "/"-bounded descendant of it.
  it('does not match a relative URL that only looks like a prefix match against a bare-path apiUrl', () => {
    expect(isApiRequest('/apiextra/bookings', '/api')).toBe(false);
  });

  it('matches an absolute same-origin URL under a bare-path apiUrl', () => {
    expect(isApiRequest(`${location.origin}/api/bookings`, '/api')).toBe(true);
  });

  it('does not match a relative URL under a bare-path apiUrl when the document origin is unavailable, even though it still matches by path', () => {
    // The relative branch never needs documentOrigin at all - a relative URL always resolves against
    // whatever origin is actually serving the page, so matching by base path alone is still correct.
    expect(isApiRequest('/api/bookings', '/api', null)).toBe(true);
  });

  it('fails closed on an absolute URL against a bare-path apiUrl when the document origin is unavailable', () => {
    expect(isApiRequest('http://localhost:5185/api/bookings', '/api', null)).toBe(false);
  });
});

// Runs against whatever environment `ng test` actually builds with (environment.development.ts, a
// non-empty apiUrl) - the interceptor's own HTTP-pipeline behavior (token attachment, login/refresh
// exclusion, transparent 401 retry), independent of which isApiRequest branch is taken.
describe('authInterceptor', () => {
  let httpClient: HttpClient;
  let httpTesting: HttpTestingController;
  let authService: { currentAccessToken: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    authService = { currentAccessToken: vi.fn().mockReturnValue('the-access-token') };

    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'login', children: [] }]),
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        { provide: AuthService, useValue: authService },
      ],
    });

    httpClient = TestBed.inject(HttpClient);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpTesting.verify());

  it('attaches the bearer token to a request against the API', () => {
    httpClient.get(`${environment.apiUrl}/bookings`).subscribe();

    const req = httpTesting.expectOne(`${environment.apiUrl}/bookings`);
    expect(req.request.headers.get('Authorization')).toBe('Bearer the-access-token');
    req.flush({});
  });

  it('does not attach the bearer token to a request outside the API', () => {
    httpClient.get('/some-local-asset.json').subscribe();

    const req = httpTesting.expectOne('/some-local-asset.json');
    expect(req.request.headers.has('Authorization')).toBe(false);
    req.flush({});
  });

  it('does not attach the bearer token to the login/refresh endpoints themselves', () => {
    httpClient.post(`${environment.apiUrl}/auth/login`, {}).subscribe();

    const req = httpTesting.expectOne(`${environment.apiUrl}/auth/login`);
    expect(req.request.headers.has('Authorization')).toBe(false);
    req.flush({});
  });

  it('transparently refreshes and retries once on a 401 from the API, then succeeds', () => {
    const refreshAccessToken = vi.fn().mockReturnValue(of({}));
    (authService as unknown as { refreshAccessToken: typeof refreshAccessToken }).refreshAccessToken = refreshAccessToken;
    (authService as unknown as { logout: () => void }).logout = vi.fn();

    let result: unknown;
    httpClient.get(`${environment.apiUrl}/bookings`).subscribe((value) => (result = value));

    const firstAttempt = httpTesting.expectOne(`${environment.apiUrl}/bookings`);
    firstAttempt.flush('unauthorized', { status: 401, statusText: 'Unauthorized' });

    expect(refreshAccessToken).toHaveBeenCalledOnce();

    const retry = httpTesting.expectOne(`${environment.apiUrl}/bookings`);
    retry.flush({ ok: true });

    expect(result).toEqual({ ok: true });
  });

  // AuthService.refreshAccessToken() now tears its own state down internally on a confirmed 401 (see
  // auth.service.spec.ts) - the interceptor's own job here is purely the UI reaction, navigating away.
  it('redirects to /login when the backend confirms the refresh token is invalid (401)', () => {
    const refreshAccessToken = vi
      .fn()
      .mockReturnValue(throwError(() => new HttpErrorResponse({ status: 401, statusText: 'Unauthorized' })));
    (authService as unknown as { refreshAccessToken: typeof refreshAccessToken }).refreshAccessToken = refreshAccessToken;
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate');

    let errored = false;
    httpClient.get(`${environment.apiUrl}/bookings`).subscribe({ error: () => (errored = true) });

    const firstAttempt = httpTesting.expectOne(`${environment.apiUrl}/bookings`);
    firstAttempt.flush('unauthorized', { status: 401, statusText: 'Unauthorized' });

    expect(refreshAccessToken).toHaveBeenCalledOnce();
    expect(navigate).toHaveBeenCalledWith(['/login'], { queryParams: { sessionExpired: true } });
    expect(errored).toBe(true);
    httpTesting.expectNone(`${environment.apiUrl}/bookings`);
  });

  // The scenario this guards against: a refresh can fail for reasons that say nothing about whether the
  // session itself is still good - a network error, a 5xx, or (see AuthService.refreshAccessToken) this
  // call being torn down after losing a cross-tab race. None of those are "the backend rejected this
  // token," so none of them should redirect the user away.
  it('does not redirect on a non-401 refresh failure (network error, 5xx, or a lost race)', () => {
    const refreshAccessToken = vi
      .fn()
      .mockReturnValue(throwError(() => new HttpErrorResponse({ status: 0, statusText: 'Unknown Error' })));
    (authService as unknown as { refreshAccessToken: typeof refreshAccessToken }).refreshAccessToken = refreshAccessToken;
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate');

    let errored = false;
    httpClient.get(`${environment.apiUrl}/bookings`).subscribe({ error: () => (errored = true) });

    const firstAttempt = httpTesting.expectOne(`${environment.apiUrl}/bookings`);
    firstAttempt.flush('unauthorized', { status: 401, statusText: 'Unauthorized' });

    expect(refreshAccessToken).toHaveBeenCalledOnce();
    expect(navigate).not.toHaveBeenCalled();
    expect(errored).toBe(true);
  });

  // AuthService.refreshAccessToken() bounds the underlying /auth/refresh call with timeout(REFRESH_TIMEOUT_MS)
  // (see auth.service.ts) specifically because that call runs inside a cross-tab lock shared by every
  // caller that needs a token refresh - a refresh that never settles would otherwise hold the lock and
  // every waiting request forever, with no error for any of them to react to. A RxJS TimeoutError is
  // NOT an HttpErrorResponse, so it must be treated the same as the network-error/5xx case above: fail
  // the waiting request(s) cleanly, never redirect (a timeout says nothing about whether the backend
  // actually rejected the session), and never leave anything hanging.
  describe('a stalled /auth/refresh that times out', () => {
    it('fails the waiting request without redirecting, once the shared refresh times out', () => {
      const refreshAccessToken = vi.fn().mockReturnValue(throwError(() => new TimeoutError()));
      (authService as unknown as { refreshAccessToken: typeof refreshAccessToken }).refreshAccessToken = refreshAccessToken;
      const navigate = vi.spyOn(TestBed.inject(Router), 'navigate');

      let errored = false;
      httpClient.get(`${environment.apiUrl}/bookings`).subscribe({ error: () => (errored = true) });

      const firstAttempt = httpTesting.expectOne(`${environment.apiUrl}/bookings`);
      firstAttempt.flush('unauthorized', { status: 401, statusText: 'Unauthorized' });

      expect(refreshAccessToken).toHaveBeenCalledOnce();
      expect(errored).toBe(true);
      expect(navigate).not.toHaveBeenCalled();
      httpTesting.expectNone(`${environment.apiUrl}/bookings`);
    });

    // Two independent requests both fail with 401 while the token is expired - both must eventually
    // fail once the shared refresh times out (neither is left permanently pending), proving the
    // timeout's failure genuinely reaches every caller waiting on the same in-flight refresh, not just
    // the one that happened to trigger it.
    it('fails every request sharing the same stalled refresh once it times out, none left hanging', () => {
      const refreshFailure$ = new Subject<never>();
      const refreshAccessToken = vi.fn().mockReturnValue(refreshFailure$);
      (authService as unknown as { refreshAccessToken: typeof refreshAccessToken }).refreshAccessToken = refreshAccessToken;

      let firstErrored = false;
      let secondErrored = false;
      httpClient.get(`${environment.apiUrl}/bookings`).subscribe({ error: () => (firstErrored = true) });
      httpClient.get(`${environment.apiUrl}/resources`).subscribe({ error: () => (secondErrored = true) });

      httpTesting.expectOne(`${environment.apiUrl}/bookings`).flush('unauthorized', { status: 401, statusText: 'Unauthorized' });
      httpTesting.expectOne(`${environment.apiUrl}/resources`).flush('unauthorized', { status: 401, statusText: 'Unauthorized' });

      // Neither request has failed yet - the shared refresh is still "in flight" from each caller's
      // point of view, exactly the hazard REFRESH_TIMEOUT_MS exists to bound.
      expect(firstErrored).toBe(false);
      expect(secondErrored).toBe(false);

      // The real AuthService's timeout() operator firing is simulated here by erroring the shared
      // Subject directly - both interceptor invocations were subscribed to this same mock return value.
      refreshFailure$.error(new TimeoutError());

      expect(firstErrored).toBe(true);
      expect(secondErrored).toBe(true);
    });
  });
});
