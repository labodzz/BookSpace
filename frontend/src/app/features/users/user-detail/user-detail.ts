import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ApiError, toApiError } from '../../../core/http/api-error';
import { AuthService } from '../../../core/auth/auth.service';
import { NotificationService } from '../../../core/notifications/notification.service';
import { DELEGABLE_ROLES, UserDetail } from '../user.models';
import { UserService } from '../user.service';

function toApiErrorOrFallback(error: unknown): ApiError {
  return error instanceof HttpErrorResponse ? toApiError(error) : { status: 0, title: 'Something went wrong.' };
}

// TenantAdmin/SysAdmin only (route-gated). One page for viewing, editing basic fields, lifecycle
// (deactivate/reactivate), and role management - the equivalent of resource-detail.html plus
// resource-approvers-manage.html combined into one screen, since a user's editable surface here is far
// smaller than a Resource's (two name fields, no separate create flow in this batch) and splitting it
// into several routes would add navigation for its own sake.
@Component({
  selector: 'app-user-detail',
  imports: [RouterLink, FormsModule, DatePipe],
  templateUrl: './user-detail.html',
  styleUrl: './user-detail.scss',
})
export class UserDetailComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly userService = inject(UserService);
  private readonly notificationService = inject(NotificationService);
  protected readonly authService = inject(AuthService);

  protected readonly delegableRoles = DELEGABLE_ROLES;

  private readonly userId = this.route.snapshot.paramMap.get('id')!;
  // Forwarded verbatim from wherever this page was entered from, so "Back to users" returns to the
  // exact same search/role/status/page the admin had applied - see user-list.ts's own comment.
  protected readonly backQueryParams = this.route.snapshot.queryParams;

  protected readonly user = signal<UserDetail | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<ApiError | null>(null);
  // A 404 here is permanent (deleted/cross-tenant target) - retrying the same id can never succeed, so
  // the error panel offers a way back to the list instead of a "Try again" button that can only fail again.
  protected readonly notFound = computed(() => this.error()?.status === 404);

  protected readonly isSelf = computed(() => this.authService.currentUser()?.userId === this.userId);

  // --- Edit form ---
  protected readonly firstName = signal('');
  protected readonly lastName = signal('');
  protected readonly submitting = signal(false);
  protected readonly formError = signal<string | null>(null);
  protected readonly fieldErrors = signal<Record<string, string[]>>({});
  protected readonly firstNameFieldErrors = computed(() => this.fieldErrors()['FirstName'] ?? []);
  protected readonly lastNameFieldErrors = computed(() => this.fieldErrors()['LastName'] ?? []);

  // --- Deactivate ---
  protected readonly deactivateDialogOpen = signal(false);
  protected readonly deactivating = signal(false);
  protected readonly deactivateError = signal<string | null>(null);

  // --- Reactivate ---
  protected readonly reactivating = signal(false);
  protected readonly reactivateError = signal<string | null>(null);

  // --- Roles ---
  protected readonly assignableRoles = computed(() => this.delegableRoles.filter((role) => !this.user()?.roles.includes(role)));
  protected readonly assigningRole = signal<string | null>(null);
  protected readonly assignRoleError = signal<string | null>(null);

  protected readonly removeRoleConfirmation = signal<string | null>(null);
  protected readonly removingRole = signal(false);
  protected readonly removeRoleError = signal<string | null>(null);

  constructor() {
    this.load();
  }

  protected retry(): void {
    this.load();
  }

  // Only TenantAdmin/SysAdmin are lockout-sensitive for the caller's OWN account - dropping your own
  // Member/Approver role is never a lockout concern, mirroring UserAdministrationGuard exactly.
  protected canRemoveOwnRole(role: string): boolean {
    return !(this.isSelf() && (role === 'TenantAdmin' || role === 'SysAdmin'));
  }

  protected submitEdit(): void {
    if (this.submitting()) {
      return;
    }

    this.formError.set(null);
    this.fieldErrors.set({});
    const firstName = this.firstName().trim();
    const lastName = this.lastName().trim();
    if (!firstName) {
      this.formError.set('First name is required.');
      return;
    }
    if (!lastName) {
      this.formError.set('Last name is required.');
      return;
    }

    this.submitting.set(true);
    this.userService.updateUser(this.userId, { firstName, lastName }).subscribe({
      next: (updated) => {
        this.submitting.set(false);
        this.user.set(updated);
        this.notificationService.showSuccess('User updated.');
      },
      error: (error: unknown) => {
        this.submitting.set(false);
        const apiError = toApiErrorOrFallback(error);
        if (apiError.fieldErrors) {
          this.fieldErrors.set(apiError.fieldErrors);
          return;
        }
        // Entered values are deliberately left untouched here (no re-prefill on failure) - the admin's
        // in-progress edit must survive a failed save, not be silently reverted to the last-saved value.
        this.formError.set(apiError.detail ?? apiError.title);
      },
    });
  }

  protected openDeactivateDialog(): void {
    this.deactivateError.set(null);
    this.deactivateDialogOpen.set(true);
  }

  protected closeDeactivateDialog(): void {
    this.deactivateDialogOpen.set(false);
  }

  protected confirmDeactivate(): void {
    if (this.deactivating()) {
      return;
    }

    this.deactivating.set(true);
    this.deactivateError.set(null);
    this.userService.deactivateUser(this.userId).subscribe({
      next: (updated) => {
        this.deactivating.set(false);
        this.deactivateDialogOpen.set(false);
        this.user.set(updated);
        this.notificationService.showSuccess(`${updated.firstName} ${updated.lastName} has been deactivated.`);
      },
      error: (error: unknown) => {
        this.deactivating.set(false);
        const apiError = toApiErrorOrFallback(error);
        this.deactivateError.set(
          apiError.errorCode === 'User.SelfLockout'
            ? 'You cannot deactivate your own account.'
            : apiError.errorCode === 'User.LastAdminRemaining'
              ? 'This tenant must always retain at least one active administrator. Assign another TenantAdmin or SysAdmin before deactivating this one.'
              : (apiError.detail ?? apiError.title),
        );
      },
    });
  }

  protected reactivate(): void {
    if (this.reactivating()) {
      return;
    }

    this.reactivating.set(true);
    this.reactivateError.set(null);
    this.userService.reactivateUser(this.userId).subscribe({
      next: (updated) => {
        this.reactivating.set(false);
        this.user.set(updated);
        this.notificationService.showSuccess(`${updated.firstName} ${updated.lastName} has been reactivated. They will need to log in again.`);
      },
      error: (error: unknown) => {
        this.reactivating.set(false);
        const apiError = toApiErrorOrFallback(error);
        this.reactivateError.set(apiError.detail ?? apiError.title);
      },
    });
  }

  protected assignRole(role: string): void {
    if (this.assigningRole()) {
      return;
    }

    this.assignRoleError.set(null);
    this.assigningRole.set(role);
    this.userService.assignRole(this.userId, role).subscribe({
      next: (response) => {
        this.assigningRole.set(null);
        this.user.update((current) => (current ? { ...current, roles: response.roles } : current));
        this.notificationService.showSuccess(
          role === 'Approver'
            ? 'Approver role added. This applies once the user\'s session next refreshes or they log in again - they must still be assigned to individual resources before they can approve anything.'
            : `${role} role added. This applies once the user's session next refreshes or they log in again.`,
        );
      },
      error: (error: unknown) => {
        this.assigningRole.set(null);
        const apiError = toApiErrorOrFallback(error);
        if (apiError.errorCode === 'User.RoleConflict') {
          this.assignRoleError.set(`${role} was already assigned - likely by another admin just now. The page has been refreshed.`);
          this.silentReload();
          return;
        }
        this.assignRoleError.set(apiError.detail ?? apiError.title);
      },
    });
  }

  protected askRemoveRole(role: string): void {
    this.removeRoleError.set(null);
    this.removeRoleConfirmation.set(role);
  }

  protected cancelRemoveRole(): void {
    this.removeRoleConfirmation.set(null);
  }

  protected confirmRemoveRole(): void {
    const role = this.removeRoleConfirmation();
    if (!role || this.removingRole()) {
      return;
    }

    this.removingRole.set(true);
    this.removeRoleError.set(null);
    this.userService.removeRole(this.userId, role).subscribe({
      next: () => {
        this.removingRole.set(false);
        this.removeRoleConfirmation.set(null);
        this.user.update((current) => (current ? { ...current, roles: current.roles.filter((r) => r !== role) } : current));
        this.notificationService.showSuccess(`${role} role removed. This applies once the user's session next refreshes or they log in again.`);
      },
      error: (error: unknown) => {
        this.removingRole.set(false);
        const apiError = toApiErrorOrFallback(error);
        // The dialog is left open (matching every other case below) rather than closed here - closing
        // it would hide this very explanation, since the error banner above only ever renders inside it.
        if (apiError.errorCode === 'User.RoleNotAssigned') {
          this.removeRoleError.set(`${role} was already removed - likely by another admin just now. The page behind this dialog has been refreshed.`);
          this.silentReload();
          return;
        }
        this.removeRoleError.set(
          apiError.errorCode === 'User.ApproverAssignmentsExist'
            ? 'The Approver role cannot be removed because this user is still assigned as an approver for one or more resources. ' +
                'Remove those resource assignments first, from each resource’s own "Manage approvers" page.'
            : apiError.errorCode === 'User.SelfLockout'
              ? 'You cannot remove your own administrative role.'
              : apiError.errorCode === 'User.LastAdminRemaining'
                ? 'This tenant must always retain at least one active administrator. Assign another TenantAdmin or SysAdmin before removing this one.'
                : (apiError.detail ?? apiError.title),
        );
      },
    });
  }

  private load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.userService.getUser(this.userId).subscribe({
      next: (user) => {
        this.user.set(user);
        this.firstName.set(user.firstName);
        this.lastName.set(user.lastName);
        this.loading.set(false);
      },
      error: (error: unknown) => {
        this.error.set(toApiErrorOrFallback(error));
        this.loading.set(false);
      },
    });
  }

  // Re-fetches in the background after a stale-state conflict (someone else already assigned/removed a
  // role) without touching `loading` - unlike load(), this must never replace the page with a loading
  // skeleton, which would hide the very conflict banner it was just called alongside. Best-effort: a
  // failure here is silently ignored, since the conflict message itself has already been shown.
  private silentReload(): void {
    this.userService.getUser(this.userId).subscribe({
      next: (user) => {
        this.user.set(user);
        this.firstName.set(user.firstName);
        this.lastName.set(user.lastName);
      },
      error: () => undefined,
    });
  }
}
