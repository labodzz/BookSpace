import { HttpErrorResponse, HttpEventType, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, finalize, tap, throwError } from 'rxjs';
import { DiagnosticsService } from './diagnostics.service';

// TEMPORARY, development-only. Only ever wired into the HTTP pipeline when !environment.production (see
// app.config.ts) - in a production build this function is simply never added to withInterceptors(...),
// so it has zero effect on production behavior or bundle-time inclusion risk. Records only method, path
// (no query string - see DiagnosticsService.pathOnly), status, duration and outcome - never headers,
// never request/response bodies, so an Authorization header or a token-bearing response body can never
// end up in the diagnostic buffer through this path.
export const diagnosticsInterceptor: HttpInterceptorFn = (request, next) => {
  const diagnostics = inject(DiagnosticsService);
  const requestId = diagnostics.recordHttpStart(request.method, request.url);
  const startedAt = performance.now();

  return next(request).pipe(
    tap((event) => {
      if (event.type === HttpEventType.Response) {
        diagnostics.recordHttpOutcome(requestId, 'http-success', event.status, performance.now() - startedAt);
      }
    }),
    catchError((error: unknown) => {
      const status = error instanceof HttpErrorResponse ? error.status : undefined;
      diagnostics.recordHttpOutcome(requestId, 'http-error', status, performance.now() - startedAt);
      return throwError(() => error);
    }),
    // Only actually records a cancellation when neither branch above already settled this request (see
    // DiagnosticsService.recordHttpFinalize) - e.g. a component was destroyed, tearing down its
    // subscription, while the request was still in flight.
    finalize(() => diagnostics.recordHttpFinalize(requestId, performance.now() - startedAt)),
  );
};
