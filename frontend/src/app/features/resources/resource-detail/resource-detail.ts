import { LowerCasePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ApiError, toApiError } from '../../../core/http/api-error';
import { AuthService } from '../../../core/auth/auth.service';
import { NotificationService } from '../../../core/notifications/notification.service';
import { ResourceService } from '../resource.service';
import { ResourceSummary } from '../resource.models';

@Component({
  selector: 'app-resource-detail',
  imports: [RouterLink, LowerCasePipe],
  templateUrl: './resource-detail.html',
  styleUrl: './resource-detail.scss',
})
export class ResourceDetailComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly resourceService = inject(ResourceService);
  private readonly notificationService = inject(NotificationService);
  protected readonly authService = inject(AuthService);

  protected readonly resource = signal<ResourceSummary | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<ApiError | null>(null);
  // A 404 here is permanent - retrying the same id will never succeed, so the error panel offers a way
  // back to browsing instead of a "Try again" button that can only ever fail again.
  protected readonly notFound = computed(() => this.error()?.status === 404);

  protected readonly archiveDialogOpen = signal(false);
  protected readonly archiving = signal(false);
  protected readonly archiveError = signal<string | null>(null);

  private readonly resourceId = this.route.snapshot.paramMap.get('id')!;

  constructor() {
    this.resourceService.getResourceTypes().subscribe({ error: () => undefined });
    this.load();
  }

  protected typeName(resourceTypeId: string): string {
    return this.resourceService.resourceTypeName(resourceTypeId);
  }

  protected retry(): void {
    this.load();
  }

  protected openArchiveDialog(): void {
    this.archiveError.set(null);
    this.archiveDialogOpen.set(true);
  }

  protected closeArchiveDialog(): void {
    this.archiveDialogOpen.set(false);
  }

  protected confirmArchive(): void {
    this.archiving.set(true);
    this.archiveError.set(null);
    this.resourceService.archiveResource(this.resourceId).subscribe({
      next: (resource) => {
        this.archiving.set(false);
        this.archiveDialogOpen.set(false);
        this.resource.set(resource);
        this.notificationService.showSuccess('Resource archived.');
      },
      error: (error: unknown) => {
        this.archiving.set(false);
        const apiError = error instanceof HttpErrorResponse ? toApiError(error) : { status: 0, title: 'Something went wrong.' };
        this.archiveError.set(apiError.detail ?? apiError.title);
      },
    });
  }

  private load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.resourceService.getResource(this.resourceId).subscribe({
      next: (resource) => {
        this.resource.set(resource);
        this.loading.set(false);
      },
      error: (error: unknown) => {
        this.error.set(error instanceof HttpErrorResponse ? toApiError(error) : { status: 0, title: 'Something went wrong.' });
        this.loading.set(false);
      },
    });
  }
}
