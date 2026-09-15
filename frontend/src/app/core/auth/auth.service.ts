import { HttpClient, HttpContext } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, finalize, map, shareReplay, tap, throwError } from 'rxjs';
import { environment } from '../../../environments/environment';
import { SKIP_GLOBAL_ERROR_TOAST } from '../http/api-error';
import { AuthTokens, CurrentUser } from './auth.models';
import { decodeAccessToken } from './jwt.util';
import { TokenStorageService } from './token-storage.service';

// The single owner of client-side auth state, kept in one signal and mirrored to localStorage so a
// page reload survives without forcing a fresh login. This - plain injectable services holding
// signals - is BookSpace's state-management approach for the Angular app: no NgRx, no separate store
// library, just signals scoped to the service that owns each concern.
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly tokenStorage = inject(TokenStorageService);

  private readonly tokens = signal<AuthTokens | null>(this.tokenStorage.load());

  // Deduplicates concurrent refreshes: several requests can all fail with 401 at once (e.g. a page
  // that fires multiple calls on load with an access token that expired while the tab was idle), and
  // without this every one of them would race to rotate the same refresh token - only the first
  // would win, and RefreshCommandHandler's rowversion check would fail the rest as a lost race.
  private refreshInFlight$: Observable<AuthTokens> | null = null;

  readonly currentUser = computed<CurrentUser | null>(() => {
    const tokens = this.tokens();
    return tokens ? decodeAccessToken(tokens.accessToken) : null;
  });

  // Deliberately keyed on the refresh token's expiry, not the access token's: an access token that
  // expired minutes ago but whose refresh token is still valid is still a valid session - the
  // interceptor refreshes it transparently on the next request. Route guards need to know "can this
  // session still be revived," not "is the current access token still valid right now."
  readonly isAuthenticated = computed(() => {
    const tokens = this.tokens();
    return tokens !== null && new Date(tokens.refreshTokenExpiresAtUtc).getTime() > Date.now();
  });

  login(email: string, password: string, rememberMe: boolean): Observable<void> {
    const context = new HttpContext().set(SKIP_GLOBAL_ERROR_TOAST, true);
    return this.http
      .post<AuthTokens>(`${environment.apiUrl}/auth/login`, { email, password }, { context })
      .pipe(
        tap((tokens) => {
          this.tokens.set(tokens);
          this.tokenStorage.save(tokens, rememberMe);
        }),
        map(() => undefined),
      );
  }

  logout(): void {
    this.tokens.set(null);
    this.tokenStorage.clear();
    this.refreshInFlight$ = null;
  }

  currentAccessToken(): string | null {
    return this.tokens()?.accessToken ?? null;
  }

  refreshAccessToken(): Observable<AuthTokens> {
    if (this.refreshInFlight$) {
      return this.refreshInFlight$;
    }

    const refreshToken = this.tokens()?.refreshToken;
    if (!refreshToken) {
      return throwError(() => new Error('No refresh token available.'));
    }

    const context = new HttpContext().set(SKIP_GLOBAL_ERROR_TOAST, true);
    const request$ = this.http
      .post<AuthTokens>(`${environment.apiUrl}/auth/refresh`, { refreshToken }, { context })
      .pipe(
        tap((tokens) => {
          this.tokens.set(tokens);
          this.tokenStorage.saveToActiveStorage(tokens);
        }),
        shareReplay(1),
        finalize(() => {
          this.refreshInFlight$ = null;
        }),
      );

    this.refreshInFlight$ = request$;
    return request$;
  }
}
