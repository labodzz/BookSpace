import { HttpClient, HttpContext } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, Subject, finalize, firstValueFrom, from, map, shareReplay, takeUntil, tap } from 'rxjs';
import { environment } from '../../../environments/environment';
import { SKIP_GLOBAL_ERROR_TOAST } from '../http/api-error';
import { AuthTokens, CurrentUser } from './auth.models';
import { decodeAccessToken } from './jwt.util';
import { TokenStorageService } from './token-storage.service';

const REFRESH_LOCK_NAME = 'bookspace-auth-refresh';
const BROADCAST_CHANNEL_NAME = 'bookspace-auth';

type AuthBroadcastMessage = { type: 'tokens-updated'; tokens: AuthTokens } | { type: 'logged-out' };

// The single owner of client-side auth state, kept in one signal and mirrored to localStorage so a
// page reload survives without forcing a fresh login. This - plain injectable services holding
// signals - is BookSpace's state-management approach for the Angular app: no NgRx, no separate store
// library, just signals scoped to the service that owns each concern.
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly tokenStorage = inject(TokenStorageService);
  private readonly router = inject(Router);

  private readonly tokens = signal<AuthTokens | null>(this.tokenStorage.load());

  // Deduplicates concurrent refreshes WITHIN this tab: several requests can all fail with 401 at once
  // (e.g. a page that fires multiple calls on load with an access token that expired while the tab was
  // idle), and without this every one of them would try to rotate the same refresh token.
  private refreshInFlight$: Observable<AuthTokens> | null = null;

  // Emits when the session ends (explicit logout, or the backend confirming a refresh token is no
  // longer valid), so a refresh that was already in flight at that moment gets torn down instead of
  // completing later and silently restoring the tokens the session-end just cleared - HttpClient's
  // underlying request is genuinely aborted on unsubscribe, not just ignored.
  private readonly loggedOut$ = new Subject<void>();

  // Same-origin channel every tab of this browser listens on, so a token rotation or a logout in one
  // tab reaches the others immediately instead of each tab only finding out the hard way - by trying to
  // use a now-stale token and getting a 401. Absent in environments without BroadcastChannel (older
  // browsers, some test runners); those tabs simply don't get the instant nudge and fall back to
  // discovering changes themselves on their next request.
  private readonly broadcastChannel =
    typeof BroadcastChannel !== 'undefined' ? new BroadcastChannel(BROADCAST_CHANNEL_NAME) : null;

  constructor() {
    if (this.broadcastChannel) {
      this.broadcastChannel.onmessage = ({ data }: MessageEvent<AuthBroadcastMessage>) => {
        if (data.type === 'tokens-updated') {
          this.tokens.set(data.tokens);
        } else {
          // Another tab's logout() already told the server to revoke the whole family - unlike
          // clearExpiredSession(), there is no "maybe a newer session already landed" case to protect
          // here, since nothing in this lineage is valid anymore regardless of what storage holds.
          this.clearLocalSessionUnconditionally();
          this.router.navigate(['/login']);
        }
      };
    }
  }

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

  // Client-side only - purely for showing/hiding UI (nav links, action buttons, route guards). The API
  // re-enforces every one of these roles server-side on the actual endpoints, so this can never become
  // the real authorization boundary, only a reflection of it.
  hasAnyRole(...roles: string[]): boolean {
    const user = this.currentUser();
    return !!user && roles.some((role) => user.roles.includes(role));
  }

  // Neither the access token nor GET /users/me carries a first/last name today (confirmed against
  // JwtTokenGenerator and CurrentUserResponse - only sub/email/tenant_id/roles exist), so a friendly
  // greeting is derived from the one real piece of identity every user has: their own email. This is
  // never sent anywhere, purely a display nicety.
  readonly displayName = computed(() => {
    const email = this.currentUser()?.email;
    if (!email) {
      return '';
    }
    const localPart = email.split('@')[0] ?? email;
    const firstToken = localPart.split(/[._-]+/).find((part) => part.length > 0) ?? localPart;
    return firstToken.charAt(0).toUpperCase() + firstToken.slice(1);
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

  // The one path that means "the user actually asked to end this session" - notifies the other tabs of
  // this browser and, best-effort, the backend (which revokes the whole token family). Server-side
  // revocation is best-effort: local state is cleared first and always, whether or not the /auth/logout
  // call ever reaches the server or succeeds - the user must never appear "stuck logged in" just
  // because that request failed offline.
  logout(): void {
    const refreshToken = this.tokens()?.refreshToken;

    this.clearLocalSessionUnconditionally();
    this.broadcastChannel?.postMessage({ type: 'logged-out' } satisfies AuthBroadcastMessage);

    if (refreshToken) {
      const context = new HttpContext().set(SKIP_GLOBAL_ERROR_TOAST, true);
      this.http
        .post(`${environment.apiUrl}/auth/logout`, { refreshToken }, { context })
        .subscribe({ error: () => undefined });
    }
  }

  private clearLocalSessionUnconditionally(): void {
    this.tokens.set(null);
    this.tokenStorage.clear();
    this.refreshInFlight$ = null;
    this.loggedOut$.next();
  }

  // Called when the interceptor gets a definitive "this refresh token is no longer valid" response (a
  // 401 from /auth/refresh) - the session really is over for THIS tab, but nothing is sent to the
  // server: a token the backend just rejected has nothing left to revoke, and this path isn't the
  // user's own request to end the session (see docs/authentication.md).
  clearExpiredSession(): void {
    const rejectedRefreshToken = this.tokens()?.refreshToken;
    this.tokens.set(null);
    this.refreshInFlight$ = null;
    this.loggedOut$.next();

    // Only wipe storage if nothing newer has landed there since: without navigator.locks (or in the
    // narrow window before a sibling tab's BroadcastChannel message arrives), this tab's refresh can be
    // rejected at the very moment a sibling tab's concurrent refresh just won and wrote a fresh, valid
    // session. Clearing storage unconditionally here would silently destroy that still-good session the
    // next time it's read from storage (a reload, a new tab) - even though this tab's own failure says
    // nothing bad about that OTHER, currently-active session.
    const stillStored = this.tokenStorage.load();
    if (!stillStored || stillStored.refreshToken === rejectedRefreshToken) {
      this.tokenStorage.clear();
    }
  }

  currentAccessToken(): string | null {
    return this.tokens()?.accessToken ?? null;
  }

  refreshAccessToken(): Observable<AuthTokens> {
    if (this.refreshInFlight$) {
      return this.refreshInFlight$;
    }

    const request$ = from(this.refreshWithCrossTabLock()).pipe(
      takeUntil(this.loggedOut$),
      shareReplay(1),
      finalize(() => {
        this.refreshInFlight$ = null;
      }),
    );

    this.refreshInFlight$ = request$;
    return request$;
  }

  // Runs the actual rotation behind a cross-tab lock, so only one tab of this browser ever has a
  // refresh in flight against the backend at a time - other tabs queue for the SAME lock instead of
  // racing the backend's RowVersion check, which stays in place purely as a last-resort safety net (see
  // "Actual reuse vs. a concurrent legitimate race" in docs/authentication.md), not the primary defense.
  // Environments without the Web Locks API (older browsers, some test runners) fall back to running the
  // refresh directly - no cross-tab dedup, but everything still works, backed by the same RowVersion net.
  private refreshWithCrossTabLock(): Promise<AuthTokens> {
    const run = () => this.performRefresh();
    return typeof navigator !== 'undefined' && navigator.locks ? navigator.locks.request(REFRESH_LOCK_NAME, run) : run();
  }

  private performRefresh(): Promise<AuthTokens> {
    // Everything above this point in the call chain is synchronous (no `await` has happened yet), so a
    // logout that ran while this call was queued for the lock is already reflected here - either as
    // `current` being null (logout cleared it) or the stored refresh token having moved on (another
    // tab's rotation already landed, via saveToActiveStorage, while this call waited its turn).
    const current = this.tokens();
    if (!current) {
      return Promise.reject(new Error('Session ended before this refresh could run.'));
    }

    const stored = this.tokenStorage.load();
    if (stored && stored.refreshToken !== current.refreshToken) {
      this.tokens.set(stored);
      return Promise.resolve(stored);
    }

    const context = new HttpContext().set(SKIP_GLOBAL_ERROR_TOAST, true);
    return firstValueFrom(
      this.http.post<AuthTokens>(`${environment.apiUrl}/auth/refresh`, { refreshToken: current.refreshToken }, { context }).pipe(
        tap((tokens) => {
          this.tokens.set(tokens);
          this.tokenStorage.saveToActiveStorage(tokens);
          this.broadcastChannel?.postMessage({ type: 'tokens-updated', tokens } satisfies AuthBroadcastMessage);
        }),
        // If the session ends (logout, or a sibling call already failing this one via loggedOut$) while
        // this HTTP call is still in flight, tear the subscription down - HttpClient aborts the
        // underlying request, and the tap above never runs for a response that arrives afterward.
        takeUntil(this.loggedOut$),
      ),
    );
  }
}
