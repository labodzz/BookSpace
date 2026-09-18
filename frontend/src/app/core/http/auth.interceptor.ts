import { HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, switchMap, throwError } from 'rxjs';
import { environment } from '../../../environments/environment';
import { AuthService } from '../auth/auth.service';

const AUTH_ENDPOINTS = ['/auth/login', '/auth/refresh', '/auth/logout'];

const ABSOLUTE_URL_SCHEME = /^[a-z][a-z\d+.-]*:\/\//i;

// apiUrl is '' in production (the API is served from the same origin as the app, not a separate
// host) - request.url.startsWith('') is true for every string, so a plain startsWith check would
// treat EVERY HTTP call the app ever makes as an API call once a same-origin deployment goes live,
// attaching the bearer token to (and force-logging-out on a 401 from) any future third-party call
// too. Empty apiUrl means "same origin as this page" instead of "starts with this literal": a
// relative URL is always same-origin by construction, and an absolute one only if it starts with
// this page's own origin. Deliberately a plain string check, not `new URL(url, location.origin)` -
// location.origin is an opaque "null" outside a real http(s) document (e.g. in a unit test's DOM),
// which the URL constructor rejects as an invalid base.
//
// Takes apiUrl as a parameter (rather than reading the `environment` import internally) so it's a
// plain, directly unit-testable function - Angular's unit-test builder disallows vi.mock-ing a
// relative import like `environment` to exercise both branches, so the branch has to be reachable a
// different way instead.
export function isApiRequest(url: string, apiUrl: string): boolean {
  if (apiUrl) {
    return url.startsWith(apiUrl);
  }

  return !ABSOLUTE_URL_SCHEME.test(url) || url.startsWith(location.origin);
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
          // Only a definitive "the backend rejected this refresh token" (401) means the session is
          // actually over. Anything else - a network error, a 5xx, or this call being torn down because
          // it lost a cross-tab race or a logout ran concurrently (see AuthService.refreshAccessToken) -
          // says nothing about whether the session is still good, so local state is left untouched and
          // a later request gets to try again.
          if (refreshError instanceof HttpErrorResponse && refreshError.status === 401) {
            authService.clearExpiredSession();
            router.navigate(['/login'], { queryParams: { sessionExpired: true } });
          }
          return throwError(() => refreshError);
        }),
      );
    }),
  );
};
