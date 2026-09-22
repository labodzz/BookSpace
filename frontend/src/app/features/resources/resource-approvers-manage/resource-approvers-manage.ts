import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Subject, catchError, debounceTime, distinctUntilChanged, of, switchMap } from 'rxjs';
import { ApiError, toApiError } from '../../../core/http/api-error';
import { NotificationService } from '../../../core/notifications/notification.service';
import { PagedResult } from '../../../core/http/paged-result';
import { UserSummary } from '../../users/user.models';
import { UserService } from '../../users/user.service';
import { AssignedApprover, ResourceSummary } from '../resource.models';
import { mapAssignedApprover } from '../resource.mappers';
import { ResourceService } from '../resource.service';

const SEARCH_DEBOUNCE_MS = 300;
const SEARCH_PAGE_SIZE = 20;

function toApiErrorOrFallback(error: unknown): ApiError {
  return error instanceof HttpErrorResponse ? toApiError(error) : { status: 0, title: 'Something went wrong.' };
}

// Assigning/removing a ResourceApprover here has an immediate, live effect on who can approve bookings
// for this resource (see ApprovalAuthorization.EnsureCallerCanDecideAsync) - there is no caching layer
// to invalidate anywhere in the stack, backend or frontend, so a successful response IS the new truth.
@Component({
  selector: 'app-resource-approvers-manage',
  imports: [RouterLink],
  templateUrl: './resource-approvers-manage.html',
  styleUrl: './resource-approvers-manage.scss',
})
export class ResourceApproversManageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly resourceService = inject(ResourceService);
  private readonly userService = inject(UserService);
  private readonly notificationService = inject(NotificationService);

  private readonly resourceId = this.route.snapshot.paramMap.get('id')!;

  protected readonly resource = signal<ResourceSummary | null>(null);
  protected readonly resourceLoading = signal(true);
  protected readonly resourceError = signal<ApiError | null>(null);
  protected readonly resourceNotFound = computed(() => this.resourceError()?.status === 404);
  protected readonly isArchived = computed(() => this.resource()?.status === 'Archived');

  protected readonly approvers = signal<AssignedApprover[]>([]);
  protected readonly approversLoading = signal(true);
  protected readonly approversError = signal<ApiError | null>(null);

  // Only meaningful once loading has actually finished - an empty list mid-load is not the same thing
  // as a confirmed empty list.
  protected readonly noApproverConfigured = computed(
    () => (this.resource()?.requiresApproval ?? false) && !this.approversLoading() && !this.approversError() && this.approvers().length === 0,
  );
  protected readonly approversHaveNoWork = computed(
    () => this.resource() !== null && !this.resource()!.requiresApproval && this.approvers().length > 0,
  );

  protected readonly searchTerm = signal('');
  protected readonly searchResults = signal<UserSummary[]>([]);
  protected readonly searching = signal(false);
  protected readonly searchError = signal<ApiError | null>(null);
  // Recomputes purely from already-fetched state whenever an assignment is added/removed - no extra
  // request needed for a user who becomes eligible/ineligible again mid-session.
  protected readonly eligibleSearchResults = computed(() => {
    const assignedUserIds = new Set(this.approvers().map((approver) => approver.userId));
    return this.searchResults().filter((user) => !assignedUserIds.has(user.id));
  });

  private readonly searchRequests = new Subject<string>();

  protected readonly assigningUserId = signal<string | null>(null);
  protected readonly assignError = signal<string | null>(null);

  protected readonly removeConfirmation = signal<AssignedApprover | null>(null);
  protected readonly removing = signal(false);
  protected readonly removeError = signal<string | null>(null);
  // A best-effort hint only, computed from whatever this page currently has loaded - a concurrent
  // change by another admin can make it stale in either direction, which is exactly why the backend
  // (RemoveResourceApproverCommandHandler, under IResourceBookingLock) remains the real enforcement.
  // See confirmRemove's ResourceApprover.LastRemaining handling for what happens when this hint was wrong.
  protected readonly removalWouldLeaveNoApprover = computed(
    () => (this.resource()?.requiresApproval ?? false) && this.approvers().length <= 1,
  );

  constructor() {
    this.searchRequests
      .pipe(
        debounceTime(SEARCH_DEBOUNCE_MS),
        distinctUntilChanged(),
        switchMap((term) => {
          this.searching.set(true);
          this.searchError.set(null);
          return this.userService.getUsers(1, SEARCH_PAGE_SIZE, { search: term || undefined, role: 'Approver' }).pipe(
            catchError((error: unknown) => {
              this.searchError.set(toApiErrorOrFallback(error));
              return of<PagedResult<UserSummary>>({ items: [], page: 1, pageSize: SEARCH_PAGE_SIZE, totalCount: 0 });
            }),
          );
        }),
        takeUntilDestroyed(),
      )
      .subscribe((result) => {
        this.searching.set(false);
        this.searchResults.set(result.items);
      });

    this.loadResource();
    this.searchRequests.next('');
  }

  protected retryLoadResource(): void {
    this.loadResource();
  }

  protected retryLoadApprovers(): void {
    this.loadApprovers();
  }

  protected onSearchInput(term: string): void {
    this.searchTerm.set(term);
    this.searchRequests.next(term);
  }

  protected displayName(approver: AssignedApprover): string {
    if (approver.resolution === 'unresolved') {
      return 'Unknown user';
    }
    return `${approver.firstName} ${approver.lastName}`;
  }

  protected assignCandidate(user: UserSummary): void {
    if (this.assigningUserId()) {
      return;
    }

    this.assignError.set(null);
    this.assigningUserId.set(user.id);
    this.resourceService.assignResourceApprover(this.resourceId, { userId: user.id }).subscribe({
      next: (assignment) => {
        this.assigningUserId.set(null);
        this.approvers.update((approvers) => [...approvers, mapAssignedApprover(assignment, user)]);
        this.notificationService.showSuccess(`${user.firstName} ${user.lastName} added as an approver.`);
      },
      error: (error: unknown) => {
        this.assigningUserId.set(null);
        this.handleAssignError(error, user);
      },
    });
  }

  protected askRemoveApprover(approver: AssignedApprover): void {
    this.removeError.set(null);
    this.removeConfirmation.set(approver);
  }

  protected cancelRemove(): void {
    this.removeConfirmation.set(null);
  }

  protected confirmRemove(): void {
    const target = this.removeConfirmation();
    if (!target || this.removing()) {
      return;
    }

    this.removing.set(true);
    this.removeError.set(null);
    this.resourceService.removeResourceApprover(this.resourceId, target.userId).subscribe({
      next: () => {
        this.removing.set(false);
        this.removeConfirmation.set(null);
        this.approvers.update((approvers) => approvers.filter((approver) => approver.userId !== target.userId));
        this.notificationService.showSuccess('Approver removed.');
      },
      error: (error: unknown) => {
        this.removing.set(false);
        const apiError = toApiErrorOrFallback(error);
        this.removeError.set(
          apiError.errorCode === 'ResourceApprover.LastRemaining'
            ? 'This is the only approver assigned to a resource that still requires approval. Assign a replacement first, ' +
                'or turn off "Requires approval" for this resource before removing them.'
            : apiError.detail ?? apiError.title,
        );
      },
    });
  }

  private handleAssignError(error: unknown, user: UserSummary): void {
    const apiError = toApiErrorOrFallback(error);
    if (apiError.errorCode === 'ResourceApprover.Conflict') {
      this.assignError.set(`${user.firstName} ${user.lastName} was already assigned - likely by another admin just now. The list below has been refreshed.`);
      this.loadApprovers();
      return;
    }
    this.assignError.set(apiError.detail ?? apiError.title);
  }

  private loadResource(): void {
    this.resourceLoading.set(true);
    this.resourceError.set(null);
    this.resourceService.getResource(this.resourceId).subscribe({
      next: (resource) => {
        this.resource.set(resource);
        this.resourceLoading.set(false);
        this.loadApprovers();
      },
      error: (error: unknown) => {
        this.resourceError.set(toApiErrorOrFallback(error));
        this.resourceLoading.set(false);
      },
    });
  }

  private loadApprovers(): void {
    this.approversLoading.set(true);
    this.approversError.set(null);
    this.resourceService.getAssignedApprovers(this.resourceId).subscribe({
      next: (approvers) => {
        this.approvers.set(approvers);
        this.approversLoading.set(false);
      },
      error: (error: unknown) => {
        this.approversError.set(toApiErrorOrFallback(error));
        this.approversLoading.set(false);
      },
    });
  }
}
