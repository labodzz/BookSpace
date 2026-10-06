import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ApiError, toApiError } from '../../../core/http/api-error';
import { NotificationService } from '../../../core/notifications/notification.service';
import { TenantDetail } from '../tenant.models';
import { TenantService } from '../tenant.service';

function toApiErrorOrFallback(error: unknown): ApiError {
  return error instanceof HttpErrorResponse ? toApiError(error) : { status: 0, title: 'Something went wrong.' };
}

// SysAdmin only (route-gated - see app.routes.ts). Platform data and aggregates about ONE tenant, plus
// the lifecycle actions SysAdmin may take on it - rename, suspend/reactivate, soft-delete, and the
// limited "platform recovery" admin-contact actions (resend/revoke a pending TenantAdmin invitation,
// invite a replacement). Deliberately does NOT show this tenant's bookings, resources, or ordinary
// member list - see docs/tenant-onboarding.md, "Limited user view for SysAdmin".
@Component({
  selector: 'app-tenant-detail',
  imports: [RouterLink, FormsModule, DatePipe],
  templateUrl: './tenant-detail.html',
  styleUrl: './tenant-detail.scss',
})
export class TenantDetailComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly tenantService = inject(TenantService);
  private readonly notificationService = inject(NotificationService);

  private readonly tenantId = this.route.snapshot.paramMap.get('id')!;
  // Forwarded verbatim from wherever this page was entered from, so "Back to tenants" returns to the
  // exact same search/status/page - see tenant-list.ts's own comment and user-detail.ts's identical
  // precedent.
  protected readonly backQueryParams = this.route.snapshot.queryParams;

  protected readonly tenant = signal<TenantDetail | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<ApiError | null>(null);
  // A 404 here is permanent (deleted/unknown id) - retrying the same id can never succeed, so the error
  // panel offers a way back to the list instead of a "Try again" button that can only fail again.
  protected readonly notFound = computed(() => this.error()?.status === 404);

  // --- Rename ---
  protected readonly editingName = signal(false);
  protected readonly nameInput = signal('');
  protected readonly savingName = signal(false);
  protected readonly nameError = signal<string | null>(null);

  // --- Suspend / reactivate ---
  protected readonly updatingStatus = signal(false);
  protected readonly statusError = signal<string | null>(null);

  // --- Soft delete ---
  protected readonly deleteDialogOpen = signal(false);
  protected readonly deleteConfirmName = signal('');
  protected readonly deleting = signal(false);
  protected readonly deleteError = signal<string | null>(null);
  protected readonly deleteConfirmMatches = computed(() => this.deleteConfirmName().trim() === this.tenant()?.name);

  // --- Pending TenantAdmin invitation(s): resend / revoke ---
  protected readonly actingOnUserId = signal<string | null>(null);
  protected readonly invitationActionError = signal<string | null>(null);
  protected readonly resentToUserId = signal<string | null>(null);

  // --- Invite a replacement TenantAdmin ---
  protected readonly inviteFormOpen = signal(false);
  protected readonly inviteFirstName = signal('');
  protected readonly inviteLastName = signal('');
  protected readonly inviteEmail = signal('');
  protected readonly inviting = signal(false);
  protected readonly inviteError = signal<string | null>(null);
  protected readonly inviteFieldErrors = signal<Record<string, string[]>>({});

  constructor() {
    this.load();
  }

  protected retry(): void {
    this.load();
  }

  private load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.tenantService.getTenant(this.tenantId).subscribe({
      next: (tenant) => {
        this.loading.set(false);
        this.tenant.set(tenant);
      },
      error: (error: unknown) => {
        this.loading.set(false);
        this.error.set(toApiErrorOrFallback(error));
      },
    });
  }

  protected startEditingName(): void {
    this.nameError.set(null);
    this.nameInput.set(this.tenant()?.name ?? '');
    this.editingName.set(true);
  }

  protected cancelEditingName(): void {
    this.editingName.set(false);
  }

  protected saveName(): void {
    const name = this.nameInput().trim();
    if (!name || this.savingName()) {
      return;
    }

    this.savingName.set(true);
    this.nameError.set(null);
    this.tenantService.updateTenant(this.tenantId, { name }).subscribe({
      next: (result) => {
        this.savingName.set(false);
        this.editingName.set(false);
        this.tenant.update((tenant) => (tenant ? { ...tenant, name: result.name } : tenant));
        this.notificationService.showSuccess('Tenant renamed.');
      },
      error: (error: unknown) => {
        this.savingName.set(false);
        const apiError = toApiErrorOrFallback(error);
        this.nameError.set(apiError.detail ?? apiError.title);
      },
    });
  }

  protected suspend(): void {
    if (this.updatingStatus()) {
      return;
    }
    this.updatingStatus.set(true);
    this.statusError.set(null);
    this.tenantService.suspendTenant(this.tenantId).subscribe({
      next: (result) => {
        this.updatingStatus.set(false);
        this.tenant.update((tenant) => (tenant ? { ...tenant, status: result.status } : tenant));
        this.notificationService.showSuccess('Tenant suspended.');
      },
      error: (error: unknown) => {
        this.updatingStatus.set(false);
        const apiError = toApiErrorOrFallback(error);
        this.statusError.set(apiError.detail ?? apiError.title);
      },
    });
  }

  protected reactivate(): void {
    if (this.updatingStatus()) {
      return;
    }
    this.updatingStatus.set(true);
    this.statusError.set(null);
    this.tenantService.reactivateTenant(this.tenantId).subscribe({
      next: (result) => {
        this.updatingStatus.set(false);
        this.tenant.update((tenant) => (tenant ? { ...tenant, status: result.status } : tenant));
        this.notificationService.showSuccess('Tenant reactivated.');
      },
      error: (error: unknown) => {
        this.updatingStatus.set(false);
        const apiError = toApiErrorOrFallback(error);
        this.statusError.set(apiError.detail ?? apiError.title);
      },
    });
  }

  protected openDeleteDialog(): void {
    this.deleteError.set(null);
    this.deleteConfirmName.set('');
    this.deleteDialogOpen.set(true);
  }

  protected closeDeleteDialog(): void {
    this.deleteDialogOpen.set(false);
  }

  // Guarded by deleteConfirmMatches on the template's [disabled] too - this check is the actual
  // double-submit guard (deleting() already true blocks a second click before the first response lands).
  protected confirmDelete(): void {
    const tenant = this.tenant();
    if (!tenant || this.deleting() || !this.deleteConfirmMatches()) {
      return;
    }

    this.deleting.set(true);
    this.deleteError.set(null);
    this.tenantService.deleteTenant(this.tenantId, { confirmName: this.deleteConfirmName().trim() }).subscribe({
      next: (result) => {
        this.deleting.set(false);
        this.deleteDialogOpen.set(false);
        this.tenant.update((current) => (current ? { ...current, status: result.status } : current));
        this.notificationService.showSuccess(`${tenant.name} was deleted.`);
      },
      error: (error: unknown) => {
        this.deleting.set(false);
        const apiError = toApiErrorOrFallback(error);
        this.deleteError.set(apiError.detail ?? apiError.title);
      },
    });
  }

  protected resendInvitation(userId: string, email: string): void {
    if (this.actingOnUserId()) {
      return;
    }
    this.actingOnUserId.set(userId);
    this.invitationActionError.set(null);
    this.resentToUserId.set(null);
    this.tenantService.resendAdminInvitation(this.tenantId, userId).subscribe({
      next: () => {
        this.actingOnUserId.set(null);
        this.resentToUserId.set(userId);
        this.notificationService.showSuccess(`A new invitation was queued for ${email}.`);
      },
      error: (error: unknown) => {
        this.actingOnUserId.set(null);
        const apiError = toApiErrorOrFallback(error);
        this.invitationActionError.set(apiError.detail ?? apiError.title);
      },
    });
  }

  protected revokeInvitation(userId: string): void {
    if (this.actingOnUserId()) {
      return;
    }
    this.actingOnUserId.set(userId);
    this.invitationActionError.set(null);
    this.tenantService.revokeAdminInvitation(this.tenantId, userId).subscribe({
      next: () => {
        this.actingOnUserId.set(null);
        this.tenant.update((tenant) =>
          tenant
            ? { ...tenant, pendingTenantAdminInvitations: tenant.pendingTenantAdminInvitations.filter((c) => c.userId !== userId) }
            : tenant,
        );
        this.notificationService.showSuccess('Invitation revoked.');
      },
      error: (error: unknown) => {
        this.actingOnUserId.set(null);
        const apiError = toApiErrorOrFallback(error);
        this.invitationActionError.set(apiError.detail ?? apiError.title);
      },
    });
  }

  protected openInviteForm(): void {
    this.inviteError.set(null);
    this.inviteFieldErrors.set({});
    this.inviteFirstName.set('');
    this.inviteLastName.set('');
    this.inviteEmail.set('');
    this.inviteFormOpen.set(true);
  }

  protected closeInviteForm(): void {
    this.inviteFormOpen.set(false);
  }

  protected submitInvite(): void {
    if (this.inviting()) {
      return;
    }
    const firstName = this.inviteFirstName().trim();
    const lastName = this.inviteLastName().trim();
    const email = this.inviteEmail().trim();
    this.inviteError.set(null);
    this.inviteFieldErrors.set({});

    if (!firstName || !lastName || !email) {
      this.inviteError.set('First name, last name, and email are all required.');
      return;
    }

    this.inviting.set(true);
    this.tenantService.inviteReplacementTenantAdmin(this.tenantId, { firstName, lastName, email }).subscribe({
      next: () => {
        this.inviting.set(false);
        this.inviteFormOpen.set(false);
        this.notificationService.showSuccess(`An invitation was queued for ${email}.`);
        this.load(); // refresh aggregates/pending-invitation list to include the new invitee
      },
      error: (error: unknown) => {
        this.inviting.set(false);
        const apiError = toApiErrorOrFallback(error);
        if (apiError.fieldErrors) {
          this.inviteFieldErrors.set(apiError.fieldErrors);
          return;
        }
        this.inviteError.set(apiError.detail ?? apiError.title);
      },
    });
  }
}
