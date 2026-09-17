import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
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

  it('logs out and redirects to /login on a failed refresh, without retrying indefinitely', () => {
    const refreshAccessToken = vi.fn().mockReturnValue(throwError(() => new Error('refresh failed')));
    const logout = vi.fn();
    (authService as unknown as { refreshAccessToken: typeof refreshAccessToken }).refreshAccessToken = refreshAccessToken;
    (authService as unknown as { logout: typeof logout }).logout = logout;

    let errored = false;
    httpClient.get(`${environment.apiUrl}/bookings`).subscribe({ error: () => (errored = true) });

    const firstAttempt = httpTesting.expectOne(`${environment.apiUrl}/bookings`);
    firstAttempt.flush('unauthorized', { status: 401, statusText: 'Unauthorized' });

    expect(refreshAccessToken).toHaveBeenCalledOnce();
    expect(logout).toHaveBeenCalledOnce();
    expect(errored).toBe(true);
    httpTesting.expectNone(`${environment.apiUrl}/bookings`);
  });
});
