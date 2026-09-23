import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { ApiError, toApiError } from '../../../core/http/api-error';
import { NotificationService } from '../../../core/notifications/notification.service';
import { ResourceStatus, ResourceSummary } from '../resource.models';
import { ResourceService } from '../resource.service';

// Friendlier copy than the raw ProblemDetails.detail for the specific conflicts Create/UpdateResource
// can throw - falls back to apiError.detail/title for anything not in this map (e.g. the "archived and
// cannot be modified" conflict, whose own message is already clear enough as-is).
const CONFLICT_MESSAGES: Record<string, string> = {
  'Resource.NameConflict': 'A resource with this name already exists.',
  'Resource.ResourceTypeNotFound': 'The selected resource type no longer exists.',
};

// One component for both routes - resources/new (create) and resources/:id/edit (edit) - since the
// form itself, its validation, and its submit handling are identical; only which request gets sent
// (and whether a resource is loaded to prefill from first) differs. TenantAdmin/SysAdmin only, both
// routes role-guarded - see app.routes.ts.
@Component({
  selector: 'app-resource-form',
  imports: [RouterLink, FormsModule],
  templateUrl: './resource-form.html',
  styleUrl: './resource-form.scss',
})
export class ResourceFormComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly resourceService = inject(ResourceService);
  private readonly notificationService = inject(NotificationService);

  protected readonly resourceId = this.route.snapshot.paramMap.get('id');
  protected readonly isEditMode = computed(() => this.resourceId !== null);
  protected readonly backLink = computed(() => (this.isEditMode() ? ['/resources', this.resourceId!] : ['/resources']));

  protected readonly loadingResource = signal(this.isEditMode());
  protected readonly resourceError = signal<ApiError | null>(null);
  // A 404 here is permanent - retrying the same id will never succeed, so the error panel offers a way
  // back to browsing instead of a "Try again" button that can only ever fail again.
  protected readonly resourceNotFound = computed(() => this.resourceError()?.status === 404);

  protected readonly resourceTypes = this.resourceService.resourceTypes;
  protected readonly resourceTypeId = signal('');
  protected readonly name = signal('');
  protected readonly description = signal('');
  protected readonly capacity = signal(1);
  protected readonly requiresApproval = signal(false);
  protected readonly timeZoneId = signal('UTC');
  // Never 'Archived' - the backend rejects that Status value on Update outright (archiving is its own
  // action, via the "Archive" button on Resource Detail, not a status picked here).
  protected readonly status = signal<Exclude<ResourceStatus, 'Archived'>>('Active');

  protected readonly submitting = signal(false);
  protected readonly formError = signal<string | null>(null);
  protected readonly fieldErrors = signal<Record<string, string[]>>({});
  protected readonly resourceTypeFieldErrors = computed(() => this.fieldErrors()['ResourceTypeId'] ?? []);
  protected readonly nameFieldErrors = computed(() => this.fieldErrors()['Name'] ?? []);
  protected readonly descriptionFieldErrors = computed(() => this.fieldErrors()['Description'] ?? []);
  protected readonly capacityFieldErrors = computed(() => this.fieldErrors()['Capacity'] ?? []);
  protected readonly timeZoneFieldErrors = computed(() => this.fieldErrors()['TimeZoneId'] ?? []);

  constructor() {
    this.resourceService.getResourceTypes().subscribe({ error: () => undefined });
    if (this.resourceId) {
      this.loadResource(this.resourceId);
    }
  }

  protected retryLoadResource(): void {
    if (this.resourceId) {
      this.loadResource(this.resourceId);
    }
  }

  protected submit(): void {
    this.formError.set(null);
    this.fieldErrors.set({});

    const name = this.name().trim();
    const timeZoneId = this.timeZoneId().trim();
    if (!this.resourceTypeId()) {
      this.formError.set('Please select a resource type.');
      return;
    }
    if (!name) {
      this.formError.set('Name is required.');
      return;
    }
    if (this.capacity() < 1) {
      this.formError.set('Capacity must be at least 1.');
      return;
    }
    if (!timeZoneId) {
      this.formError.set('Time zone is required.');
      return;
    }

    this.submitting.set(true);
    const request = {
      resourceTypeId: this.resourceTypeId(),
      name,
      description: this.description().trim() || null,
      capacity: this.capacity(),
      requiresApproval: this.requiresApproval(),
      timeZoneId,
    };

    const result = this.isEditMode()
      ? this.resourceService.updateResource(this.resourceId!, { ...request, status: this.status() })
      : this.resourceService.createResource(request);

    result.subscribe({
      next: (resource) => {
        this.submitting.set(false);
        this.notificationService.showSuccess(this.isEditMode() ? 'Resource updated.' : 'Resource created.');
        this.router.navigate(['/resources', resource.id]);
      },
      error: (error: unknown) => {
        this.submitting.set(false);
        this.handleError(error);
      },
    });
  }

  private loadResource(id: string): void {
    this.loadingResource.set(true);
    this.resourceError.set(null);
    this.resourceService.getResource(id).subscribe({
      next: (resource) => {
        this.prefill(resource);
        this.loadingResource.set(false);
      },
      error: (error: unknown) => {
        this.resourceError.set(error instanceof HttpErrorResponse ? toApiError(error) : { status: 0, title: 'Something went wrong.' });
        this.loadingResource.set(false);
      },
    });
  }

  private prefill(resource: ResourceSummary): void {
    this.resourceTypeId.set(resource.resourceTypeId);
    this.name.set(resource.name);
    this.description.set(resource.description ?? '');
    this.capacity.set(resource.capacity);
    this.requiresApproval.set(resource.requiresApproval);
    this.timeZoneId.set(resource.timeZoneId);
    if (resource.status !== 'Archived') {
      this.status.set(resource.status);
    }
  }

  private handleError(error: unknown): void {
    if (!(error instanceof HttpErrorResponse)) {
      this.formError.set('Something went wrong. Please try again.');
      return;
    }

    const apiError = toApiError(error);
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
