import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Subject, catchError, debounceTime, distinctUntilChanged, of, startWith, switchMap } from 'rxjs';
import { ApiError, toApiError } from '../../../core/http/api-error';
import { PagedResult } from '../../../core/http/paged-result';
import { TenantStatus, TenantSummary } from '../tenant.models';
import { TenantService } from '../tenant.service';

const PAGE_SIZE = 20;
const SEARCH_DEBOUNCE_MS = 300;
const TENANT_STATUSES: readonly TenantStatus[] = ['Active', 'Suspended', 'Deleted'];

function toApiErrorOrFallback(error: unknown): ApiError {
  return error instanceof HttpErrorResponse ? toApiError(error) : { status: 0, title: 'Something went wrong.' };
}

function isTenantStatus(value: string | null): value is TenantStatus {
  return !!value && (TENANT_STATUSES as readonly string[]).includes(value);
}

// SysAdmin only (route-gated - see app.routes.ts). The platform's own tenant roster - never a tenant's
// business data (no bookings, no resources, no ordinary member list - see docs/tenant-onboarding.md,
// "Limited user view for SysAdmin"). Search/status/page live in the URL's own query params, the same
// convention UserListComponent already establishes, for the identical reason (a "Back to tenants" link
// from the detail page can restore the exact same filters).
@Component({
  selector: 'app-tenant-list',
  imports: [RouterLink, DatePipe],
  templateUrl: './tenant-list.html',
  styleUrl: './tenant-list.scss',
})
export class TenantListComponent {
  private readonly tenantService = inject(TenantService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  protected readonly allStatuses = TENANT_STATUSES;

  protected readonly tenants = signal<TenantSummary[]>([]);
  protected readonly totalCount = signal(0);
  protected readonly loading = signal(true);
  protected readonly error = signal<ApiError | null>(null);

  protected readonly page = signal(1);
  protected readonly searchTerm = signal('');
  protected readonly status = signal<TenantStatus | ''>('');

  protected readonly totalPages = computed(() => Math.max(1, Math.ceil(this.totalCount() / PAGE_SIZE)));

  protected readonly currentQueryParams = computed(() => ({
    page: this.page() === 1 ? null : this.page(),
    search: this.searchTerm() || null,
    status: this.status() || null,
  }));

  private readonly searchRequests = new Subject<string>();
  private readonly reload$ = new Subject<void>();

  constructor() {
    const initialParams = this.route.snapshot.queryParamMap;
    const initialPage = Number(initialParams.get('page'));
    this.page.set(Number.isInteger(initialPage) && initialPage > 0 ? initialPage : 1);
    this.searchTerm.set(initialParams.get('search') ?? '');
    const statusParam = initialParams.get('status');
    this.status.set(isTenantStatus(statusParam) ? statusParam : '');

    this.searchRequests.pipe(debounceTime(SEARCH_DEBOUNCE_MS), distinctUntilChanged(), takeUntilDestroyed()).subscribe(() => {
      this.page.set(1);
      this.applyFiltersAndReload();
    });

    this.reload$
      .pipe(
        startWith(undefined),
        switchMap(() => {
          this.loading.set(true);
          this.error.set(null);
          return this.tenantService
            .getTenants(this.page(), PAGE_SIZE, { search: this.searchTerm() || undefined, status: this.status() || undefined })
            .pipe(catchError((error: unknown) => of({ failed: true as const, apiError: toApiErrorOrFallback(error) })));
        }),
        takeUntilDestroyed(),
      )
      .subscribe((result: (PagedResult<TenantSummary> & { failed?: false }) | { failed: true; apiError: ApiError }) => {
        this.loading.set(false);
        if (result.failed) {
          this.error.set(result.apiError);
          return;
        }
        this.tenants.set(result.items);
        this.totalCount.set(result.totalCount);
      });
  }

  protected onSearchInput(term: string): void {
    this.searchTerm.set(term);
    this.searchRequests.next(term);
  }

  protected onStatusChange(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.status.set(isTenantStatus(value) ? value : '');
    this.page.set(1);
    this.applyFiltersAndReload();
  }

  protected goToPage(page: number): void {
    if (page < 1 || page > this.totalPages()) {
      return;
    }
    this.page.set(page);
    this.applyFiltersAndReload();
  }

  protected retry(): void {
    this.reload$.next();
  }

  private applyFiltersAndReload(): void {
    this.router.navigate([], { relativeTo: this.route, queryParams: this.currentQueryParams(), replaceUrl: true });
    this.reload$.next();
  }
}
