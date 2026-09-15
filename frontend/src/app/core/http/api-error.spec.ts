import { HttpErrorResponse } from '@angular/common/http';
import { toApiError } from './api-error';

describe('toApiError', () => {
  it('reads the plain { message } shape AuthController returns on invalid credentials', () => {
    const error = new HttpErrorResponse({
      status: 401,
      error: { message: 'Invalid email or password.' },
    });

    expect(toApiError(error).title).toBe('Invalid email or password.');
  });

  it('reads title, detail, errorCode and correlationId from a ProblemDetails body', () => {
    const error = new HttpErrorResponse({
      status: 409,
      error: {
        title: 'The request could not be completed due to a conflict.',
        detail: 'The resource is already booked for this time slot.',
        errorCode: 'booking.overlap',
        correlationId: 'abc-123',
      },
    });

    const apiError = toApiError(error);
    expect(apiError).toEqual({
      status: 409,
      title: 'The request could not be completed due to a conflict.',
      detail: 'The resource is already booked for this time slot.',
      errorCode: 'booking.overlap',
      correlationId: 'abc-123',
      fieldErrors: undefined,
    });
  });

  it('reads field-level errors from a ValidationProblemDetails body', () => {
    const error = new HttpErrorResponse({
      status: 400,
      error: {
        title: 'One or more validation errors occurred.',
        errors: { Email: ['Email is required.'], Password: ['Password is required.'] },
      },
    });

    expect(toApiError(error).fieldErrors).toEqual({
      Email: ['Email is required.'],
      Password: ['Password is required.'],
    });
  });

  it('falls back to a status-specific title when the body has none of the known shapes', () => {
    const error = new HttpErrorResponse({ status: 500, error: null });

    expect(toApiError(error).title).toBe('Something went wrong on our end.');
  });

  it('reports a network-level failure distinctly from a server error', () => {
    const error = new HttpErrorResponse({ status: 0, error: null });

    const apiError = toApiError(error);
    expect(apiError.status).toBe(0);
    expect(apiError.title).toContain('Unable to reach the server');
  });
});
