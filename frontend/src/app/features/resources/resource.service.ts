import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { Observable, catchError, map, of, shareReplay, switchMap, tap, throwError } from 'rxjs';
import { environment } from '../../../environments/environment';
import { PagedResult } from '../../core/http/paged-result';
import { UserService } from '../users/user.service';
import {
  AssignedApprover,
  AssignResourceApproverRequest,
  AvailabilityRule,
  BlackoutPeriod,
  BlackoutPeriodWithConflicts,
  CreateAvailabilityRuleRequest,
  CreateBlackoutPeriodRequest,
  CreateResourceRequest,
  CreateResourceTypeRequest,
  ResourceApproverAssignment,
  ResourceAvailability,
  ResourceStatus,
  ResourceSummary,
  ResourceType,
  UpdateBlackoutPeriodRequest,
  UpdateResourceRequest,
  UpdateResourceTypeRequest,
} from './resource.models';
import {
  AvailabilityRuleWire,
  ResourceSummaryWire,
  fromDayOfWeek,
  fromResourceStatus,
  mapAssignedApprover,
  mapAvailabilityRule,
  mapResourceSummary,
} from './resource.mappers';

@Injectable({ providedIn: 'root' })
export class ResourceService {
  private readonly http = inject(HttpClient);
  private readonly userService = inject(UserService);

  // Resource types rarely change and are needed on every screen that shows or filters resources, so
  // they're fetched once and cached for the app's lifetime rather than re-requested per screen.
  private resourceTypesRequest$: Observable<ResourceType[]> | null = null;
  readonly resourceTypes = signal<ResourceType[]>([]);

  // The backend's canonical, IANA-only, currently-resolvable time zone list - static for the process
  // lifetime of the SERVER, so a single client-side fetch cached for this app's own lifetime is safe;
  // never re-requested per Create/Edit form visit.
  private supportedTimeZonesRequest$: Observable<string[]> | null = null;

  getResources(page: number, pageSize: number, resourceTypeId?: string, status?: ResourceStatus): Observable<PagedResult<ResourceSummary>> {
    let params = new HttpParams().set('page', page).set('pageSize', pageSize);
    if (resourceTypeId) {
      params = params.set('resourceTypeId', resourceTypeId);
    }
    if (status) {
      params = params.set('status', status);
    }

    return this.http
      .get<PagedResult<ResourceSummaryWire>>(`${environment.apiUrl}/resources`, { params })
      .pipe(map((result) => ({ ...result, items: result.items.map(mapResourceSummary) })));
  }

  getResource(id: string): Observable<ResourceSummary> {
    return this.http.get<ResourceSummaryWire>(`${environment.apiUrl}/resources/${id}`).pipe(map(mapResourceSummary));
  }

  createResource(request: CreateResourceRequest): Observable<ResourceSummary> {
    return this.http
      .post<ResourceSummaryWire>(`${environment.apiUrl}/resources`, request)
      .pipe(map(mapResourceSummary), tap((resource) => this.resourceCache.set(resource.id, resource)));
  }

  updateResource(id: string, request: UpdateResourceRequest): Observable<ResourceSummary> {
    const wireRequest = { ...request, status: fromResourceStatus(request.status) };
    return this.http
      .put<ResourceSummaryWire>(`${environment.apiUrl}/resources/${id}`, wireRequest)
      .pipe(map(mapResourceSummary), tap((resource) => this.resourceCache.set(resource.id, resource)));
  }

  // Soft-delete (archive) - the backend flips Status to Archived rather than removing the row, so the
  // response is still a full resource, not an empty body.
  archiveResource(id: string): Observable<ResourceSummary> {
    return this.http
      .delete<ResourceSummaryWire>(`${environment.apiUrl}/resources/${id}`)
      .pipe(map(mapResourceSummary), tap((resource) => this.resourceCache.set(resource.id, resource)));
  }

  // Booking lists (My Bookings, Calendar, Approval queue) only ever get a ResourceId back from the API,
  // never a resource name - this caches one GET per distinct resource for the session instead of every
  // list re-fetching (or every row separately fetching) the same handful of resources its bookings
  // reference.
  private readonly resourceCache = new Map<string, ResourceSummary>();
  private readonly resourceRequests = new Map<string, Observable<ResourceSummary>>();

  getResourceCached(id: string): Observable<ResourceSummary> {
    const cached = this.resourceCache.get(id);
    if (cached) {
      return of(cached);
    }

    let request$ = this.resourceRequests.get(id);
    if (!request$) {
      request$ = this.getResource(id).pipe(
        tap((resource) => this.resourceCache.set(id, resource)),
        shareReplay(1),
      );
      this.resourceRequests.set(id, request$);
    }

    return request$;
  }

  // fromDate/toDate are plain yyyy-MM-dd calendar dates (no time, no timezone offset) - the API
  // interprets the range relative to the resource's own TimeZoneId, so sending a wall-clock date
  // avoids any client-side UTC conversion that could shift it by a day.
  getAvailability(resourceId: string, fromDate: string, toDate: string): Observable<ResourceAvailability> {
    const params = new HttpParams().set('from', fromDate).set('to', toDate);
    return this.http.get<ResourceAvailability>(`${environment.apiUrl}/resources/${resourceId}/availability`, { params });
  }

  // Plain arrays, not paged - GetAvailabilityRulesQueryRequest/GetBlackoutPeriodsQueryRequest never
  // paginate (a resource's own rules/blackouts are a small, human-curated set). Never cached: the
  // member-facing availability page always re-fetches on its own, so nothing here needs invalidating
  // for it to see a change made through this management page.
  getAvailabilityRules(resourceId: string): Observable<AvailabilityRule[]> {
    return this.http
      .get<AvailabilityRuleWire[]>(`${environment.apiUrl}/resources/${resourceId}/availability-rules`)
      .pipe(map((rules) => rules.map(mapAvailabilityRule)));
  }

  createAvailabilityRule(resourceId: string, request: CreateAvailabilityRuleRequest): Observable<AvailabilityRule> {
    const wireRequest = { ...request, dayOfWeek: fromDayOfWeek(request.dayOfWeek) };
    return this.http
      .post<AvailabilityRuleWire>(`${environment.apiUrl}/resources/${resourceId}/availability-rules`, wireRequest)
      .pipe(map(mapAvailabilityRule));
  }

  deleteAvailabilityRule(resourceId: string, ruleId: string): Observable<void> {
    return this.http.delete<void>(`${environment.apiUrl}/resources/${resourceId}/availability-rules/${ruleId}`);
  }

  // No enum fields, so no mapper needed - StartUtc/EndUtc/Reason all round-trip as plain strings.
  getBlackoutPeriods(resourceId: string): Observable<BlackoutPeriod[]> {
    return this.http.get<BlackoutPeriod[]>(`${environment.apiUrl}/resources/${resourceId}/blackout-periods`);
  }

  // The response includes conflictingBookingIds - every currently-active booking that now overlaps
  // this blackout. Creating it never blocks on that list and never cancels those bookings; the caller
  // decides what to show for it (see resource-availability-manage.ts).
  createBlackoutPeriod(resourceId: string, request: CreateBlackoutPeriodRequest): Observable<BlackoutPeriodWithConflicts> {
    return this.http.post<BlackoutPeriodWithConflicts>(`${environment.apiUrl}/resources/${resourceId}/blackout-periods`, request);
  }

  updateBlackoutPeriod(resourceId: string, blackoutId: string, request: UpdateBlackoutPeriodRequest): Observable<BlackoutPeriodWithConflicts> {
    return this.http.put<BlackoutPeriodWithConflicts>(`${environment.apiUrl}/resources/${resourceId}/blackout-periods/${blackoutId}`, request);
  }

  deleteBlackoutPeriod(resourceId: string, blackoutId: string): Observable<void> {
    return this.http.delete<void>(`${environment.apiUrl}/resources/${resourceId}/blackout-periods/${blackoutId}`);
  }

  // Bare assignments only (Id/ResourceId/UserId) - see getAssignedApprovers below for the display-ready
  // version this admin page actually renders.
  getResourceApprovers(resourceId: string): Observable<ResourceApproverAssignment[]> {
    return this.http.get<ResourceApproverAssignment[]>(`${environment.apiUrl}/resources/${resourceId}/approvers`);
  }

  // Resolves each assignment's bare userId into display data via GET /users?ids= - a second, tenant-
  // scoped request bounded by exactly this resource's own assignment count, never the whole tenant
  // roster. A userId that doesn't come back (deleted user) or comes back without an approval-capable
  // role still gets its own row - see mapAssignedApprover - never silently dropped.
  getAssignedApprovers(resourceId: string): Observable<AssignedApprover[]> {
    return this.getResourceApprovers(resourceId).pipe(
      switchMap((assignments) => {
        if (assignments.length === 0) {
          return of<AssignedApprover[]>([]);
        }

        const ids = assignments.map((assignment) => assignment.userId);
        return this.userService.getUsers(1, ids.length, { ids }).pipe(
          map((result) => {
            const userById = new Map(result.items.map((user) => [user.id, user]));
            return assignments.map((assignment) => mapAssignedApprover(assignment, userById.get(assignment.userId)));
          }),
        );
      }),
    );
  }

  assignResourceApprover(resourceId: string, request: AssignResourceApproverRequest): Observable<ResourceApproverAssignment> {
    return this.http.post<ResourceApproverAssignment>(`${environment.apiUrl}/resources/${resourceId}/approvers`, request);
  }

  removeResourceApprover(resourceId: string, userId: string): Observable<void> {
    return this.http.delete<void>(`${environment.apiUrl}/resources/${resourceId}/approvers/${userId}`);
  }

  // Cached indefinitely via shareReplay(1) - a failed request is deliberately NOT cached (catchError
  // below clears it before rethrowing), so a transient network failure can be retried by simply calling
  // this again, matching timezone-select.ts's own retry button.
  getSupportedTimeZones(): Observable<string[]> {
    if (!this.supportedTimeZonesRequest$) {
      this.supportedTimeZonesRequest$ = this.http.get<string[]>(`${environment.apiUrl}/resources/supported-timezones`).pipe(
        catchError((error: unknown) => {
          this.supportedTimeZonesRequest$ = null;
          return throwError(() => error);
        }),
        shareReplay(1),
      );
    }

    return this.supportedTimeZonesRequest$;
  }

  getResourceTypes(): Observable<ResourceType[]> {
    if (!this.resourceTypesRequest$) {
      this.resourceTypesRequest$ = this.http.get<ResourceType[]>(`${environment.apiUrl}/resource-types`).pipe(
        tap((types) => this.resourceTypes.set(types)),
        shareReplay(1),
      );
    }

    return this.resourceTypesRequest$;
  }

  resourceTypeName(resourceTypeId: string): string {
    return this.resourceTypes().find((type) => type.id === resourceTypeId)?.name ?? 'Unknown type';
  }

  // Unlike getResourceTypes(), always issues a fresh request - the resource-type admin page must show
  // the server's current truth on load/retry, never whatever getResourceTypes()'s shareReplay(1)
  // happened to cache from an earlier, unrelated page visit.
  reloadResourceTypes(): Observable<ResourceType[]> {
    return this.http
      .get<ResourceType[]>(`${environment.apiUrl}/resource-types`)
      .pipe(tap((types) => this.resourceTypes.set(types)));
  }

  // Keeps the shared `resourceTypes` signal (read by resource-list/resource-form/resource-detail) in
  // sync immediately on success, sorted the same way GetResourceTypesQueryHandler already orders its
  // response - no separate reload needed for every other page to see the change.
  createResourceType(request: CreateResourceTypeRequest): Observable<ResourceType> {
    return this.http.post<ResourceType>(`${environment.apiUrl}/resource-types`, request).pipe(
      tap((created) => this.resourceTypes.update((types) => sortedByName([...types, created]))),
    );
  }

  updateResourceType(id: string, request: UpdateResourceTypeRequest): Observable<ResourceType> {
    return this.http.put<ResourceType>(`${environment.apiUrl}/resource-types/${id}`, request).pipe(
      tap((updated) =>
        this.resourceTypes.update((types) => sortedByName(types.map((type) => (type.id === id ? updated : type)))),
      ),
    );
  }

  deleteResourceType(id: string): Observable<void> {
    return this.http
      .delete<void>(`${environment.apiUrl}/resource-types/${id}`)
      .pipe(tap(() => this.resourceTypes.update((types) => types.filter((type) => type.id !== id))));
  }
}

function sortedByName(types: ResourceType[]): ResourceType[] {
  return [...types].sort((a, b) => a.name.localeCompare(b.name));
}
