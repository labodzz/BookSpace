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
