import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { ApiError, toApiError } from '../../../core/http/api-error';
import { AuthService } from '../../../core/auth/auth.service';

@Component({
  selector: 'app-login',
  imports: [ReactiveFormsModule],
  templateUrl: './login.html',
  styleUrl: './login.scss',
})
export class LoginComponent {
  private readonly formBuilder = inject(FormBuilder);
  private readonly authService = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected readonly form = this.formBuilder.nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', Validators.required],
    rememberMe: [true],
  });

  protected readonly isSubmitting = signal(false);
  protected readonly errorMessage = signal<string | null>(null);
  protected readonly showPassword = signal(false);
  protected readonly sessionExpired = this.route.snapshot.queryParamMap.get('sessionExpired') === 'true';

  submit(): void {
    if (this.form.invalid || this.isSubmitting()) {
      this.form.markAllAsTouched();
      return;
    }

    this.isSubmitting.set(true);
    this.errorMessage.set(null);
    const { email, password, rememberMe } = this.form.getRawValue();

    this.authService.login(email, password, rememberMe).subscribe({
      next: () => {
        // Reset BEFORE navigating, not left for navigation to implicitly clean up by destroying this
        // component - the same pattern every other submit-then-navigate flow in this app already follows
        // (see ResourceFormComponent.submit/BookingFormComponent.submit). This was the one place that
        // didn't: if navigateByUrl doesn't cleanly complete (most plausibly a lazy route chunk failing to
        // load - every route here is loadComponent()-lazy - but also any guard/resolver rejection), the
        // component is never destroyed, and isSubmitting stayed true forever - the login button
        // permanently stuck disabled on "Logging in…" with no way to retry, indistinguishable from a
        // frozen page until the whole app is reloaded.
        this.isSubmitting.set(false);
        const returnUrl = this.route.snapshot.queryParamMap.get('returnUrl') ?? '/';
        this.router.navigateByUrl(returnUrl);
      },
      error: (error: unknown) => {
        this.isSubmitting.set(false);
        this.errorMessage.set(error instanceof HttpErrorResponse ? this.messageFor(toApiError(error)) : 'Something went wrong. Please try again.');
      },
    });
  }

  // errorInterceptor deliberately skips its global toast whenever a response carries field-level
  // errors, on the assumption the calling form renders them inline (see api-error.ts) - login has no
  // per-field error UI, so falling back to just the generic title would silently drop the one piece of
  // information (which field, what's wrong with it) the interceptor is trusting this form to show.
  private messageFor(apiError: ApiError): string {
    if (!apiError.fieldErrors) {
      return apiError.title;
    }

    return Object.values(apiError.fieldErrors).flat().join(' ');
  }
}
