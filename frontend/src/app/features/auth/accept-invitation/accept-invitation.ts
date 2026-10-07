import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { AbstractControl, FormBuilder, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ApiError, toApiError } from '../../../core/http/api-error';
import { AuthService } from '../../../core/auth/auth.service';
import { ThemeToggleComponent } from '../../../core/theme/theme-toggle/theme-toggle';

// Two fields must match, checked at the FORM level (not a single control) since the mismatch isn't an
// error of either field alone - mirrors how Angular's own docs recommend a cross-field validator.
function passwordsMatchValidator(control: AbstractControl): ValidationErrors | null {
  const password = control.get('password')?.value;
  const confirmPassword = control.get('confirmPassword')?.value;
  return password && confirmPassword && password !== confirmPassword ? { passwordMismatch: true } : null;
}

// Public (guestGuard-gated, same as /login - see app.routes.ts): reachable without any prior session, and
// an already-signed-in browser is redirected away BEFORE this page ever reads the token, so accepting an
// invitation can never silently swap an existing session's identity or tenant context (see
// docs/tenant-onboarding.md, "Already logged in"). Reuses AuthenticationService.AcceptInvitationAsync's
// existing, single generic failure message for every invalid/expired/revoked/already-used token - this
// page never tries to distinguish them itself, nor does it introduce any new auth pattern: on success it
// shows a confirmation and links to the existing /login page, exactly as docs/user-administration.md's
// "no auto-login" behavior already establishes for every other invitation acceptance.
@Component({
  selector: 'app-accept-invitation',
  imports: [ReactiveFormsModule, RouterLink, ThemeToggleComponent],
  templateUrl: './accept-invitation.html',
  styleUrl: './accept-invitation.scss',
})
export class AcceptInvitationComponent {
  private readonly formBuilder = inject(FormBuilder);
  private readonly authService = inject(AuthService);
  private readonly route = inject(ActivatedRoute);

  protected readonly token = this.route.snapshot.queryParamMap.get('token');

  protected readonly form = this.formBuilder.nonNullable.group(
    {
      password: ['', [Validators.required, Validators.minLength(8), Validators.maxLength(200)]],
      confirmPassword: ['', Validators.required],
    },
    { validators: passwordsMatchValidator },
  );

  protected readonly isSubmitting = signal(false);
  protected readonly errorMessage = signal<string | null>(null);
  protected readonly accepted = signal(false);
  protected readonly showPassword = signal(false);

  submit(): void {
    if (!this.token || this.form.invalid || this.isSubmitting()) {
      this.form.markAllAsTouched();
      return;
    }

    this.isSubmitting.set(true);
    this.errorMessage.set(null);
    const { password } = this.form.getRawValue();

    this.authService.acceptInvitation(this.token, password).subscribe({
      next: () => {
        this.isSubmitting.set(false);
        this.accepted.set(true);
      },
      error: (error: unknown) => {
        this.isSubmitting.set(false);
        this.errorMessage.set(this.messageFor(error));
      },
    });
  }

  // Same generic message the backend already returns for every failure cause (unknown/expired/revoked/
  // already-accepted token) - never narrowed further here, so this page cannot leak which case applies.
  private messageFor(error: unknown): string {
    if (!(error instanceof HttpErrorResponse)) {
      return 'Something went wrong. Please try again.';
    }

    const apiError: ApiError = toApiError(error);
    return apiError.detail ?? apiError.title;
  }
}
