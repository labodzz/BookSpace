import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiError, toApiError } from '../../../core/http/api-error';
import { NotificationService } from '../../../core/notifications/notification.service';
import { ResourceType } from '../../resources/resource.models';
import { ResourceService } from '../../resources/resource.service';

// Friendlier copy than the raw ProblemDetails.detail for the two conflicts Create/Update/Delete can
// throw - same pattern as resource-form.ts's own CONFLICT_MESSAGES.
const CONFLICT_MESSAGES: Record<string, string> = {
  'ResourceType.NameConflict': 'A resource type with this name already exists.',
  'ResourceType.InUse': 'This resource type is still used by at least one resource (active or archived) and cannot be deleted.',
};

type TypeDialogState = { mode: 'create' } | { mode: 'edit'; type: ResourceType };

// TenantAdmin/SysAdmin only (route-gated, not just button-hidden - see app.routes.ts). GET
// /resource-types itself is open to any authenticated user (needed to pick a type when creating a
// resource), but this management page - and the "Resource types" nav link that leads to it - is admin
// only, matching ResourceTypesController's own [Authorize(Roles = "TenantAdmin,SysAdmin")] on every
// POST/PUT/DELETE.
@Component({
  selector: 'app-resource-type-list',
  imports: [FormsModule],
  templateUrl: './resource-type-list.html',
  styleUrl: './resource-type-list.scss',
})
export class ResourceTypeListComponent {
  private readonly resourceService = inject(ResourceService);
  private readonly notificationService = inject(NotificationService);

  protected readonly resourceTypes = this.resourceService.resourceTypes;
  protected readonly loading = signal(true);
  protected readonly error = signal<ApiError | null>(null);

  protected readonly dialog = signal<TypeDialogState | null>(null);
  protected readonly dialogName = signal('');
  protected readonly saving = signal(false);
  protected readonly dialogFormError = signal<string | null>(null);
  protected readonly dialogNameFieldErrors = signal<string[]>([]);

  protected readonly deleteTarget = signal<ResourceType | null>(null);
  protected readonly deleting = signal(false);
  protected readonly deleteError = signal<string | null>(null);

  constructor() {
    this.load();
  }

  protected retry(): void {
    this.load();
  }

  protected openCreateDialog(): void {
    this.dialogName.set('');
    this.dialogFormError.set(null);
    this.dialogNameFieldErrors.set([]);
    this.dialog.set({ mode: 'create' });
  }

  protected openEditDialog(type: ResourceType): void {
    this.dialogName.set(type.name);
    this.dialogFormError.set(null);
    this.dialogNameFieldErrors.set([]);
    this.dialog.set({ mode: 'edit', type });
  }

  protected closeDialog(): void {
    this.dialog.set(null);
  }

  protected submitDialog(): void {
    const dialog = this.dialog();
    if (!dialog || this.saving()) {
      return;
    }

    this.dialogFormError.set(null);
    this.dialogNameFieldErrors.set([]);
    const name = this.dialogName().trim();
    if (!name) {
      this.dialogFormError.set('Name is required.');
      return;
    }

    this.saving.set(true);
    const result$ =
      dialog.mode === 'create' ? this.resourceService.createResourceType({ name }) : this.resourceService.updateResourceType(dialog.type.id, { name });

    result$.subscribe({
      next: () => {
        this.saving.set(false);
        this.dialog.set(null);
        this.notificationService.showSuccess(dialog.mode === 'create' ? 'Resource type added.' : 'Resource type updated.');
      },
      error: (error: unknown) => {
        this.saving.set(false);
        this.handleDialogError(error);
      },
    });
  }

  protected askDelete(type: ResourceType): void {
    this.deleteError.set(null);
    this.deleteTarget.set(type);
  }

  protected cancelDelete(): void {
    this.deleteTarget.set(null);
  }

  protected confirmDelete(): void {
    const target = this.deleteTarget();
    if (!target || this.deleting()) {
      return;
    }

    this.deleting.set(true);
    this.deleteError.set(null);
    this.resourceService.deleteResourceType(target.id).subscribe({
      next: () => {
        this.deleting.set(false);
        this.deleteTarget.set(null);
        this.notificationService.showSuccess('Resource type deleted.');
      },
      error: (error: unknown) => {
        this.deleting.set(false);
        const apiError = error instanceof HttpErrorResponse ? toApiError(error) : { status: 0, title: 'Something went wrong.' };
        this.deleteError.set((apiError.errorCode && CONFLICT_MESSAGES[apiError.errorCode]) ?? apiError.detail ?? apiError.title);
      },
    });
  }

  private load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.resourceService.reloadResourceTypes().subscribe({
      next: () => this.loading.set(false),
      error: (error: unknown) => {
        this.error.set(error instanceof HttpErrorResponse ? toApiError(error) : { status: 0, title: 'Something went wrong.' });
        this.loading.set(false);
      },
    });
  }

  private handleDialogError(error: unknown): void {
    if (!(error instanceof HttpErrorResponse)) {
      this.dialogFormError.set('Something went wrong. Please try again.');
      return;
    }

    const apiError = toApiError(error);
    if (apiError.fieldErrors?.['Name']) {
      this.dialogNameFieldErrors.set(apiError.fieldErrors['Name']);
      return;
    }
    if (apiError.errorCode && CONFLICT_MESSAGES[apiError.errorCode]) {
      this.dialogFormError.set(CONFLICT_MESSAGES[apiError.errorCode]);
      return;
    }
    this.dialogFormError.set(apiError.detail ?? apiError.title);
  }
}
