import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
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

describe('AuthService', () => {
  let service: AuthService;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    // Seeded BEFORE AuthService is injected: its `tokens` signal reads TokenStorageService.load() once,
    // in its own constructor, so the session must already be in storage by then.
    TestBed.inject(TokenStorageService).save(TOKENS, true);
    service = TestBed.inject(AuthService);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    localStorage.clear();
    sessionStorage.clear();
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
});
