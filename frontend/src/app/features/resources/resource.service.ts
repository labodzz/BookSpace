import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { Observable, map, of, shareReplay, tap } from 'rxjs';
import { environment } from '../../../environments/environment';
import { PagedResult } from '../../core/http/paged-result';
import { ResourceAvailability, ResourceStatus, ResourceSummary, ResourceType } from './resource.models';
import { ResourceSummaryWire, mapResourceSummary } from './resource.mappers';

@Injectable({ providedIn: 'root' })
export class ResourceService {
  private readonly http = inject(HttpClient);

  // Resource types rarely change and are needed on every screen that shows or filters resources, so
  // they're fetched once and cached for the app's lifetime rather than re-requested per screen.
  private resourceTypesRequest$: Observable<ResourceType[]> | null = null;
  readonly resourceTypes = signal<ResourceType[]>([]);

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
}
