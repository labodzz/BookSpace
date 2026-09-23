import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Subject, catchError, debounceTime, distinctUntilChanged, of, startWith, switchMap } from 'rxjs';
import { ApiError, toApiError } from '../../../core/http/api-error';
import { AuthService } from '../../../core/auth/auth.service';
import { PagedResult } from '../../../core/http/paged-result';
import { ALL_GLOBAL_ROLES, UserStatus, UserSummary, isUserStatus } from '../user.models';
import { UserService } from '../user.service';

const PAGE_SIZE = 20;
const SEARCH_DEBOUNCE_MS = 300;

function toApiErrorOrFallback(error: unknown): ApiError {
  return error instanceof HttpErrorResponse ? toApiError(error) : { status: 0, title: 'Something went wrong.' };
}

// TenantAdmin/SysAdmin only (route-gated). Search/role/status/page all live in the URL's own query
// params, not just component signals - specifically so the "Back to users" link on the detail page (see
// user-detail.ts) can return here with the exact same filters and page still applied, instead of a reset
// list, satisfying "preserve filter state when editing a user" without any shared service between the
// two routes.
@Component({
  selector: 'app-user-list',
  imports: [RouterLink],
  templateUrl: './user-list.html',
  styleUrl: './user-list.scss',
})
export class UserListComponent {
  private readonly userService = inject(UserService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  protected readonly authService = inject(AuthService);

  protected readonly allRoles = ALL_GLOBAL_ROLES;

  protected readonly users = signal<UserSummary[]>([]);
  protected readonly totalCount = signal(0);
  protected readonly loading = signal(true);
  protected readonly error = signal<ApiError | null>(null);

  protected readonly page = signal(1);
  // searchTerm is the raw, immediate value bound to the input box so typing always feels responsive;
  // the actual HTTP request only fires once the debounce below settles - the same split
  // resource-approvers-manage.ts already uses for its own approver search.
  protected readonly searchTerm = signal('');
  protected readonly role = signal('');
  protected readonly status = signal<UserStatus | ''>('');

  protected readonly totalPages = computed(() => Math.max(1, Math.ceil(this.totalCount() / PAGE_SIZE)));

  // Forwarded verbatim to the detail page's "Back to users" link so it can navigate back here with
  // these exact params, without that page needing to know anything about this one beyond the URL.
  protected readonly currentQueryParams = computed(() => ({
    page: this.page() === 1 ? null : this.page(),
    search: this.searchTerm() || null,
    role: this.role() || null,
    status: this.status() || null,
  }));

  private readonly searchRequests = new Subject<string>();
  private readonly reload$ = new Subject<void>();

  constructor() {
    const initialParams = this.route.snapshot.queryParamMap;
    const initialPage = Number(initialParams.get('page'));
    this.page.set(Number.isInteger(initialPage) && initialPage > 0 ? initialPage : 1);
    this.searchTerm.set(initialParams.get('search') ?? '');
    this.role.set(initialParams.get('role') ?? '');
    const statusParam = initialParams.get('status');
    this.status.set(isUserStatus(statusParam) ? statusParam : '');

    // Debounced text search, cancelling any still-in-flight request the moment a newer one is due - see
    // the switchMap below, which is what actually performs the cancellation for every trigger (search,
    // role, status, and page), not just this one.
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
          return this.userService
            .getUsers(this.page(), PAGE_SIZE, {
              search: this.searchTerm() || undefined,
              role: this.role() || undefined,
              status: this.status() || undefined,
            })
            .pipe(
              catchError((error: unknown) => of({ failed: true as const, apiError: toApiErrorOrFallback(error) })),
              // switchMap unsubscribes any previous, still-pending request the instant a new trigger
              // fires - an old, slower response for a filter the admin has already moved on from can
              // never arrive after a newer one and overwrite it.
            );
        }),
        takeUntilDestroyed(),
      )
      .subscribe((result: (PagedResult<UserSummary> & { failed?: false }) | { failed: true; apiError: ApiError }) => {
        this.loading.set(false);
        if (result.failed) {
          this.error.set(result.apiError);
          return;
        }
        this.users.set(result.items);
        this.totalCount.set(result.totalCount);
      });
  }

  protected onSearchInput(term: string): void {
    this.searchTerm.set(term);
    this.searchRequests.next(term);
  }

  protected onRoleChange(event: Event): void {
    this.role.set((event.target as HTMLSelectElement).value);
    this.page.set(1);
    this.applyFiltersAndReload();
  }

  protected onStatusChange(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.status.set(isUserStatus(value) ? value : '');
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

  protected isYou(user: UserSummary): boolean {
    return this.authService.currentUser()?.userId === user.id;
  }

  private applyFiltersAndReload(): void {
    this.router.navigate([], { relativeTo: this.route, queryParams: this.currentQueryParams(), replaceUrl: true });
    this.reload$.next();
  }
}
