import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { ApiError, toApiError } from '../../../core/http/api-error';
import { ResourceService } from '../resource.service';
import { ResourceStatus, ResourceSummary } from '../resource.models';

const PAGE_SIZE = 12;

// Browse screen: any authenticated tenant member can see every resource, filtered by type/status using
// the filters GetResources actually supports (no others are invented) and paginated using the API's own
// PagedResult, not a client-side slice of an unbounded fetch.
@Component({
  selector: 'app-resource-list',
  imports: [],
  templateUrl: './resource-list.html',
  styleUrl: './resource-list.scss',
})
export class ResourceListComponent {
  private readonly resourceService = inject(ResourceService);
  private readonly router = inject(Router);

  protected readonly resourceTypes = this.resourceService.resourceTypes;
  protected readonly resources = signal<ResourceSummary[]>([]);
  protected readonly totalCount = signal(0);
  protected readonly page = signal(1);
  protected readonly loading = signal(true);
  protected readonly error = signal<ApiError | null>(null);
  protected readonly resourceTypeId = signal('');
  // Defaults to Active because that's what a member browsing to book a resource almost always wants -
  // Archived/Inactive/Maintenance resources are still reachable via the status filter, just not the
  // first thing shown.
  protected readonly status = signal<ResourceStatus | ''>('Active');

  protected readonly totalPages = computed(() => Math.max(1, Math.ceil(this.totalCount() / PAGE_SIZE)));

  constructor() {
    this.resourceService.getResourceTypes().subscribe({ error: () => undefined });
    this.load();
  }

  protected onTypeChange(event: Event): void {
    this.resourceTypeId.set((event.target as HTMLSelectElement).value);
    this.page.set(1);
    this.load();
  }

  protected onStatusChange(event: Event): void {
    this.status.set((event.target as HTMLSelectElement).value as ResourceStatus | '');
    this.page.set(1);
    this.load();
  }

  protected goToPage(page: number): void {
    if (page < 1 || page > this.totalPages()) {
      return;
    }
    this.page.set(page);
    this.load();
  }

  protected retry(): void {
    this.load();
  }

  protected openResource(id: string): void {
    this.router.navigate(['/resources', id]);
  }

  protected typeName(resourceTypeId: string): string {
    return this.resourceService.resourceTypeName(resourceTypeId);
  }

  private load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.resourceService
      .getResources(this.page(), PAGE_SIZE, this.resourceTypeId() || undefined, this.status() || undefined)
      .subscribe({
        next: (result) => {
          this.resources.set(result.items);
          this.totalCount.set(result.totalCount);
          this.loading.set(false);
        },
        error: (error: unknown) => {
          this.error.set(error instanceof HttpErrorResponse ? toApiError(error) : { status: 0, title: 'Something went wrong.' });
          this.loading.set(false);
        },
      });
  }
}
