import { HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, switchMap, throwError } from 'rxjs';
import { environment } from '../../../environments/environment';
import { AuthService } from '../auth/auth.service';

const AUTH_ENDPOINTS = ['/auth/login', '/auth/refresh'];

// Attaches the current access token to every request against our own API, and on a 401 transparently
// refreshes it once and replays the request - the login/refresh calls themselves are excluded so a
// failed login can never trigger a refresh loop against its own response.
export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const authService = inject(AuthService);
  const router = inject(Router);

  const isApiRequest = request.url.startsWith(environment.apiUrl);
  const isAuthEndpoint = AUTH_ENDPOINTS.some((endpoint) => request.url.includes(endpoint));

  const attachToken = (req: HttpRequest<unknown>) => {
    const accessToken = authService.currentAccessToken();
    return isApiRequest && !isAuthEndpoint && accessToken
      ? req.clone({ setHeaders: { Authorization: `Bearer ${accessToken}` } })
      : req;
  };

  return next(attachToken(request)).pipe(
    catchError((error: unknown) => {
      if (!(error instanceof HttpErrorResponse) || error.status !== 401 || !isApiRequest || isAuthEndpoint) {
        return throwError(() => error);
      }

      return authService.refreshAccessToken().pipe(
        switchMap(() => next(attachToken(request))),
        catchError((refreshError: unknown) => {
          authService.logout();
          router.navigate(['/login'], { queryParams: { sessionExpired: true } });
          return throwError(() => refreshError);
        }),
      );
    }),
  );
};
