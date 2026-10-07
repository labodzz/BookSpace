import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ApiError, toApiError } from '../../../core/http/api-error';
import { TimezoneSelectComponent } from '../../resources/timezone-select/timezone-select';
import { CreateTenantResult } from '../tenant.models';
import { TenantService } from '../tenant.service';

// Friendlier copy than the raw ProblemDetails.detail for the specific conflicts CreateTenantCommandHandler
// can throw - see docs/tenant-onboarding.md ("Concurrent create"). Falls back to apiError.detail/title for
// anything else (e.g. a race lost at the database, reported the same generic way).
const CONFLICT_MESSAGES: Record<string, string> = {
  'Tenant.NameConflict': 'A tenant with this name already exists.',
  'User.EmailConflict': 'A user with this email already exists.',
};

// SysAdmin only (route-gated - see app.routes.ts). Creating a tenant+first admin is a one-shot action,
// so this is a pure "create, then show what happened" form - unlike ResourceFormComponent, it never
// navigates away on success: the confirmation panel IS the destination, and it doubles as a quick way to
// resend this specific admin's invitation right after creation without a detour through the tenant list.
// The tenant detail page (see tenant-detail.ts) offers the same resend action later, plus revoke/invite-
// replacement, for a tenant found through the list instead of just-created here.
@Component({
  selector: 'app-tenant-form',
  imports: [RouterLink, FormsModule, TimezoneSelectComponent],
  templateUrl: './tenant-form.html',
  styleUrl: './tenant-form.scss',
})
export class TenantFormComponent {
  private readonly tenantService = inject(TenantService);

  protected readonly name = signal('');
  protected readonly defaultTimeZoneId = signal('');
  protected readonly adminFirstName = signal('');
  protected readonly adminLastName = signal('');
  protected readonly adminEmail = signal('');

  protected readonly submitting = signal(false);
  protected readonly formError = signal<string | null>(null);
  protected readonly fieldErrors = signal<Record<string, string[]>>({});
  protected readonly nameFieldErrors = (): string[] => this.fieldErrors()['Name'] ?? [];
  protected readonly timeZoneFieldErrors = (): string[] => this.fieldErrors()['DefaultTimeZoneId'] ?? [];
  protected readonly adminFirstNameFieldErrors = (): string[] => this.fieldErrors()['AdminFirstName'] ?? [];
  protected readonly adminLastNameFieldErrors = (): string[] => this.fieldErrors()['AdminLastName'] ?? [];
  protected readonly adminEmailFieldErrors = (): string[] => this.fieldErrors()['AdminEmail'] ?? [];

  // Set only after a successful create - its presence is what switches the template to the confirmation
  // panel. Never cleared by a later failed resend attempt below: a failed resend doesn't undo the fact
  // that the tenant/account were created.
  protected readonly created = signal<CreateTenantResult | null>(null);

  protected readonly resending = signal(false);
  protected readonly resendError = signal<string | null>(null);
  // Tracks only "was the resend request accepted," never "was the email actually delivered" - see
  // tenant-form.html's own wording and docs/tenant-onboarding.md ("What 'created' actually means").
  protected readonly resent = signal(false);

  protected submit(): void {
    this.formError.set(null);
    this.fieldErrors.set({});

    const name = this.name().trim();
    const defaultTimeZoneId = this.defaultTimeZoneId().trim();
    const adminFirstName = this.adminFirstName().trim();
    const adminLastName = this.adminLastName().trim();
    const adminEmail = this.adminEmail().trim();

    if (!name) {
      this.formError.set('Tenant name is required.');
      return;
    }
    if (!defaultTimeZoneId) {
      this.formError.set('Time zone is required.');
      return;
    }
    if (!adminFirstName || !adminLastName) {
      this.formError.set("The administrator's first and last name are required.");
      return;
    }
    if (!adminEmail) {
      this.formError.set("The administrator's email is required.");
      return;
    }

    this.submitting.set(true);
    this.tenantService.createTenant({ name, defaultTimeZoneId, adminFirstName, adminLastName, adminEmail }).subscribe({
      next: (result) => {
        this.submitting.set(false);
        this.created.set(result);
      },
      error: (error: unknown) => {
        this.submitting.set(false);
        this.handleError(error);
      },
    });
  }

  protected resendInvitation(): void {
    const created = this.created();
    if (!created || this.resending()) {
      return;
    }

    this.resending.set(true);
    this.resendError.set(null);
    this.resent.set(false);
    this.tenantService.resendAdminInvitation(created.tenantId, created.adminUserId).subscribe({
      next: () => {
        this.resending.set(false);
        this.resent.set(true);
      },
      error: (error: unknown) => {
        this.resending.set(false);
        const apiError = error instanceof HttpErrorResponse ? toApiError(error) : { status: 0, title: 'Something went wrong.' };
        this.resendError.set(apiError.detail ?? apiError.title);
      },
    });
  }

  protected startAnotherTenant(): void {
    this.created.set(null);
    this.resent.set(false);
    this.resendError.set(null);
    this.name.set('');
    this.defaultTimeZoneId.set('');
    this.adminFirstName.set('');
    this.adminLastName.set('');
    this.adminEmail.set('');
  }

  private handleError(error: unknown): void {
    if (!(error instanceof HttpErrorResponse)) {
      this.formError.set('Something went wrong. Please try again.');
      return;
    }

    const apiError: ApiError = toApiError(error);
    if (apiError.fieldErrors) {
      this.fieldErrors.set(apiError.fieldErrors);
      return;
    }
    if (apiError.errorCode && CONFLICT_MESSAGES[apiError.errorCode]) {
      this.formError.set(CONFLICT_MESSAGES[apiError.errorCode]);
      return;
    }
    this.formError.set(apiError.detail ?? apiError.title);
  }
}
