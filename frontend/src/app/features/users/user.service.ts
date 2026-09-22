import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import { environment } from '../../../environments/environment';
import { PagedResult } from '../../core/http/paged-result';
import { AssignRoleResponse, UpdateUserRequest, UserDetail, UserStatus, UserSummary } from './user.models';
import { UserDetailWire, UserSummaryWire, mapUserDetail, mapUserSummary } from './user.mappers';

export interface GetUsersOptions {
  search?: string;
  role?: string;
  status?: UserStatus;
  ids?: string[];
}

@Injectable({ providedIn: 'root' })
export class UserService {
  private readonly http = inject(HttpClient);

  getUsers(page: number, pageSize: number, options: GetUsersOptions = {}): Observable<PagedResult<UserSummary>> {
    let params = new HttpParams().set('page', page).set('pageSize', pageSize);
    if (options.search) {
      params = params.set('search', options.search);
    }
    if (options.role) {
      params = params.set('role', options.role);
    }
    if (options.status) {
      params = params.set('status', options.status);
    }
    for (const id of options.ids ?? []) {
      params = params.append('ids', id);
    }

    return this.http
      .get<PagedResult<UserSummaryWire>>(`${environment.apiUrl}/users`, { params })
      .pipe(map((result) => ({ ...result, items: result.items.map(mapUserSummary) })));
  }

  getUser(id: string): Observable<UserDetail> {
    return this.http.get<UserDetailWire>(`${environment.apiUrl}/users/${id}`).pipe(map(mapUserDetail));
  }

  updateUser(id: string, request: UpdateUserRequest): Observable<UserDetail> {
    return this.http.put<UserDetailWire>(`${environment.apiUrl}/users/${id}`, request).pipe(map(mapUserDetail));
  }

  // A soft "delete" (Status -> Inactive), same convention as archiveResource - returns the updated user,
  // not 204, so the caller sees the new status without a follow-up GET. Idempotent on the backend: an
  // already-Inactive target just returns its current state.
  deactivateUser(id: string): Observable<UserDetail> {
    return this.http.delete<UserDetailWire>(`${environment.apiUrl}/users/${id}`).pipe(map(mapUserDetail));
  }

  reactivateUser(id: string): Observable<UserDetail> {
    return this.http.post<UserDetailWire>(`${environment.apiUrl}/users/${id}/reactivate`, null).pipe(map(mapUserDetail));
  }

  assignRole(id: string, role: string): Observable<AssignRoleResponse> {
    return this.http.post<AssignRoleResponse>(`${environment.apiUrl}/users/${id}/roles`, { role });
  }

  // The role name is a path segment (DELETE /users/{id}/roles/{role}) - encoded defensively even though
  // every role name this UI ever sends comes from DELEGABLE_ROLES's own fixed, safe set.
  removeRole(id: string, role: string): Observable<void> {
    return this.http.delete<void>(`${environment.apiUrl}/users/${id}/roles/${encodeURIComponent(role)}`);
  }
}
