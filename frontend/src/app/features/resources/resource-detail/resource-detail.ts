import { LowerCasePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ApiError, toApiError } from '../../../core/http/api-error';
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

  protected readonly resource = signal<ResourceSummary | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<ApiError | null>(null);
  // A 404 here is permanent - retrying the same id will never succeed, so the error panel offers a way
  // back to browsing instead of a "Try again" button that can only ever fail again.
  protected readonly notFound = computed(() => this.error()?.status === 404);

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
