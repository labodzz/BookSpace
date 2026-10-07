import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { AbstractControl, FormBuilder, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
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

// Public token route. A current session must be explicitly ended before accepting an invitation.
// Invalid/expired/used tokens retain the backend's generic error; acceptance never signs the user in.
@Component({
  selector: 'app-accept-invitation',
  imports: [ReactiveFormsModule, RouterLink, ThemeToggleComponent],
  templateUrl: './accept-invitation.html',
  styleUrl: './accept-invitation.scss',
})
export class AcceptInvitationComponent {
  private readonly formBuilder = inject(FormBuilder);
  protected readonly authService = inject(AuthService);
  private readonly router = inject(Router);
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

  logoutAndContinue(): void {
    // logout clears local state synchronously and does not navigate. Keep this URL and its token
    // intact, even if backend revocation fails; no token storage or second navigation is needed.
    this.authService.logout();
  }

  submit(): void {
    if (this.authService.isAuthenticated() || !this.token || this.form.invalid || this.isSubmitting()) {
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
        this.router.navigate(['/login'], { queryParams: { invitationAccepted: true } });
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
