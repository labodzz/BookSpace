import { HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, switchMap, throwError } from 'rxjs';
import { environment } from '../../../environments/environment';
import { AuthService } from '../auth/auth.service';

const AUTH_ENDPOINTS = ['/auth/login', '/auth/refresh', '/auth/logout'];

const ABSOLUTE_URL_SCHEME = /^[a-z][a-z\d+.-]*:\/\//i;
const PROTOCOL_RELATIVE_URL = /^\/\//;

// The real origin of the document hosting this app, or null when that can't be trusted - either
// `location` doesn't exist at all (some non-browser test runners) or it reports the opaque "null"
// origin a sandboxed/non-http(s) document gets (e.g. jsdom's default "about:blank" page in unit
// tests). Callers treat null as "cannot confirm same-origin," never as "assume same-origin."
function getDocumentOrigin(): string | null {
  try {
    const origin = typeof location === 'undefined' ? null : location.origin;
    return origin && origin !== 'null' ? origin : null;
  } catch {
    return null;
  }
}

function normalizeBasePath(pathname: string): string {
  return pathname.replace(/\/+$/, '');
}

// A base path of '' matches anything (no restriction). Otherwise the target path must be the base
// path itself or a "/"-bounded descendant of it - '/api' must match '/api/bookings' but not
// '/apiextra/bookings', which merely shares the same string prefix.
function pathMatchesBase(pathname: string, basePath: string): boolean {
  return !basePath || pathname === basePath || pathname.startsWith(`${basePath}/`);
}

// What "the API" means for classification purposes: apiUrl === '' means "this page's own origin, no
// path restriction" (the production, same-origin-deployment shape); a non-empty apiUrl is parsed for
// its own origin and, if it has one, a base path that a request's path must fall under.
function resolveApiOrigin(apiUrl: string, documentOrigin: string | null): { origin: string | null; basePath: string } {
  if (!apiUrl) {
    return { origin: documentOrigin, basePath: '' };
  }
  try {
    const parsed = new URL(apiUrl);
    return { origin: parsed.origin, basePath: normalizeBasePath(parsed.pathname) };
  } catch {
    return { origin: null, basePath: '' };
  }
}

// Classifies whether `url` targets our own API, by normalized origin (and base path, if apiUrl has
// one) rather than a raw string prefix. A plain `url.startsWith(apiUrl)` check would treat a lookalike
// host such as `https://real-api.example.com.attacker.example/path` as belonging to
// `https://real-api.example.com` merely because one string starts with the other - normalizing both
// sides through the URL parser and comparing actual origins closes that gap.
//
// Takes apiUrl (and, for tests, documentOrigin) as parameters rather than reading `environment`/
// `location` internally so it stays a plain, directly unit-testable function - Angular's unit-test
// builder disallows vi.mock-ing a relative import like `environment` to exercise both branches, so the
// branch has to be reachable a different way instead.
export function isApiRequest(url: string, apiUrl: string, documentOrigin: string | null = getDocumentOrigin()): boolean {
  if (PROTOCOL_RELATIVE_URL.test(url)) {
    // "//host/path" resolves against the page's current protocol but an explicit, possibly different,
    // host - it must never be treated as a same-origin relative path just because it lacks an
    // "http(s)://" prefix.
    return false;
  }

  if (!ABSOLUTE_URL_SCHEME.test(url)) {
    // A genuinely relative URL always resolves against the CURRENT page's origin, so it can only be
    // "the API" when apiUrl itself means same-origin (apiUrl === '') - a configured, separate API
    // origin is never reachable through a bare relative path.
    return apiUrl === '';
  }

  let target: URL;
  try {
    target = new URL(url);
  } catch {
    return false;
  }

  const base = resolveApiOrigin(apiUrl, documentOrigin);
  return base.origin !== null && target.origin === base.origin && pathMatchesBase(target.pathname, base.basePath);
}

// Attaches the current access token to every request against our own API, and on a 401 transparently
// refreshes it once and replays the request - the login/refresh calls themselves are excluded so a
// failed login can never trigger a refresh loop against its own response.
export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const authService = inject(AuthService);
  const router = inject(Router);

  const requestIsApiRequest = isApiRequest(request.url, environment.apiUrl);
  const isAuthEndpoint = AUTH_ENDPOINTS.some((endpoint) => request.url.endsWith(endpoint));

  const attachToken = (req: HttpRequest<unknown>) => {
    const accessToken = authService.currentAccessToken();
    return requestIsApiRequest && !isAuthEndpoint && accessToken
      ? req.clone({ setHeaders: { Authorization: `Bearer ${accessToken}` } })
      : req;
  };

  return next(attachToken(request)).pipe(
    catchError((error: unknown) => {
      if (!(error instanceof HttpErrorResponse) || error.status !== 401 || !requestIsApiRequest || isAuthEndpoint) {
        return throwError(() => error);
      }

      return authService.refreshAccessToken().pipe(
        switchMap(() => next(attachToken(request))),
        catchError((refreshError: unknown) => {
          // AuthService.refreshAccessToken() already tears its own state down on a confirmed 401 (see
          // its internal clearExpiredSession call) - this is purely the UI reaction, navigating away.
          // Anything else - a network error, a 5xx, or this call being torn down because it lost a
          // cross-tab race or a logout ran concurrently - says nothing about whether the session is
          // still good, so nothing happens here and a later request gets to try again.
          if (refreshError instanceof HttpErrorResponse && refreshError.status === 401) {
            router.navigate(['/login'], { queryParams: { sessionExpired: true } });
          }
          return throwError(() => refreshError);
        }),
      );
    }),
  );
};
