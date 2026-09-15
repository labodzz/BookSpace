import { HttpContextToken, HttpErrorResponse } from '@angular/common/http';

// Set on an outgoing request's HttpContext to opt it out of errorInterceptor's global toast - used
// by call sites (like the login form) that want to render the failure inline instead of as a toast.
export const SKIP_GLOBAL_ERROR_TOAST = new HttpContextToken<boolean>(() => false);

export interface ApiError {
  status: number;
  title: string;
  detail?: string;
  errorCode?: string;
  correlationId?: string;
  fieldErrors?: Record<string, string[]>;
}

// Normalizes every shape the API's error handlers can produce - ProblemDetails/ValidationProblemDetails
// from ValidationExceptionHandler/NotFoundExceptionHandler/ConflictExceptionHandler/GlobalExceptionHandler,
// the plain { message } body AuthController returns on 401, and a network-level failure with no
// response at all (status 0) - into one shape the rest of the app can rely on.
export function toApiError(error: HttpErrorResponse): ApiError {
  if (error.status === 0) {
    return { status: 0, title: 'Unable to reach the server. Check your connection and try again.' };
  }

  const body = error.error as unknown;
  if (body && typeof body === 'object') {
    const record = body as Record<string, unknown>;
    const title =
      (typeof record['title'] === 'string' && record['title']) ||
      (typeof record['message'] === 'string' && record['message']) ||
      defaultTitleFor(error.status);
    const detail = typeof record['detail'] === 'string' ? record['detail'] : undefined;
    const errorCode = typeof record['errorCode'] === 'string' ? record['errorCode'] : undefined;
    const correlationId = typeof record['correlationId'] === 'string' ? record['correlationId'] : undefined;
    const fieldErrors = isFieldErrorMap(record['errors']) ? record['errors'] : undefined;

    return { status: error.status, title, detail, errorCode, correlationId, fieldErrors };
  }

  return { status: error.status, title: defaultTitleFor(error.status) };
}

function isFieldErrorMap(value: unknown): value is Record<string, string[]> {
  return (
    typeof value === 'object' &&
    value !== null &&
    Object.values(value).every((entry) => Array.isArray(entry) && entry.every((item) => typeof item === 'string'))
  );
}

function defaultTitleFor(status: number): string {
  switch (status) {
    case 400:
      return 'The request was invalid.';
    case 401:
      return 'You need to sign in to continue.';
    case 403:
      return "You don't have permission to do that.";
    case 404:
      return 'The requested resource was not found.';
    case 409:
      return 'That action could not be completed due to a conflict.';
    case 500:
      return 'Something went wrong on our end.';
    default:
      return 'An unexpected error occurred.';
  }
}
