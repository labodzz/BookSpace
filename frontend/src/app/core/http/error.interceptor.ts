import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { NotificationService } from '../notifications/notification.service';
import { SKIP_GLOBAL_ERROR_TOAST, toApiError } from './api-error';

// The outer half of the HTTP pipeline (see provideHttpClient in app.config.ts) - it only ever sees an
// error after authInterceptor's own refresh-and-retry has already run its course, so a 401 that gets
// silently fixed by a token refresh never reaches here. Field-level validation errors are left for the
// calling form to render next to the relevant inputs, so those are skipped too - everything else
// becomes a toast, because a failed request that never tells the user anything is a silent failure.
export const errorInterceptor: HttpInterceptorFn = (request, next) => {
  const notificationService = inject(NotificationService);

  return next(request).pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse && !request.context.get(SKIP_GLOBAL_ERROR_TOAST)) {
        const apiError = toApiError(error);
        if (!apiError.fieldErrors) {
          notificationService.showError(apiError.detail ?? apiError.title);
        }
      }

      return throwError(() => error);
    }),
  );
};
