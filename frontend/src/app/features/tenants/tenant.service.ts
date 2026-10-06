import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import { environment } from '../../../environments/environment';
import { PagedResult } from '../../core/http/paged-result';
import {
  CreateTenantRequest,
  CreateTenantResult,
  DeleteTenantRequest,
  InviteReplacementTenantAdminRequest,
  InviteReplacementTenantAdminResult,
  ResendTenantAdminInvitationResult,
  TenantDetail,
  TenantStatus,
  TenantStatusResult,
  TenantSummary,
  UpdateTenantRequest,
} from './tenant.models';
import { TenantDetailWire, TenantStatusResultWire, TenantSummaryWire, mapTenantDetail, mapTenantStatusResult, mapTenantSummary } from './tenant.mappers';

export interface GetTenantsOptions {
  search?: string;
  status?: TenantStatus;
}

// SysAdmin only - see docs/tenant-onboarding.md.
@Injectable({ providedIn: 'root' })
export class TenantService {
  private readonly http = inject(HttpClient);

  createTenant(request: CreateTenantRequest): Observable<CreateTenantResult> {
    return this.http.post<CreateTenantResult>(`${environment.apiUrl}/tenants`, request);
  }

  getTenants(page: number, pageSize: number, options: GetTenantsOptions = {}): Observable<PagedResult<TenantSummary>> {
    let params = new HttpParams().set('page', page).set('pageSize', pageSize);
    if (options.search) {
      params = params.set('search', options.search);
    }
    if (options.status) {
      params = params.set('status', options.status);
    }

    return this.http
      .get<PagedResult<TenantSummaryWire>>(`${environment.apiUrl}/tenants`, { params })
      .pipe(map((result) => ({ ...result, items: result.items.map(mapTenantSummary) })));
  }

  getTenant(id: string): Observable<TenantDetail> {
    return this.http.get<TenantDetailWire>(`${environment.apiUrl}/tenants/${id}`).pipe(map(mapTenantDetail));
  }

  updateTenant(id: string, request: UpdateTenantRequest): Observable<TenantStatusResult> {
    return this.http.put<TenantStatusResultWire>(`${environment.apiUrl}/tenants/${id}`, request).pipe(map(mapTenantStatusResult));
  }

  suspendTenant(id: string): Observable<TenantStatusResult> {
    return this.http.post<TenantStatusResultWire>(`${environment.apiUrl}/tenants/${id}/suspend`, null).pipe(map(mapTenantStatusResult));
  }

  reactivateTenant(id: string): Observable<TenantStatusResult> {
    return this.http.post<TenantStatusResultWire>(`${environment.apiUrl}/tenants/${id}/reactivate`, null).pipe(map(mapTenantStatusResult));
  }

  // ConfirmName is re-verified server-side against the tenant's CURRENT name - see
  // DeleteTenantCommandHandler. This call alone is not a confirmation UI; the component calling it owns
  // the warning/typed-confirmation/double-submit-prevention flow.
  deleteTenant(id: string, request: DeleteTenantRequest): Observable<TenantStatusResult> {
    return this.http.post<TenantStatusResultWire>(`${environment.apiUrl}/tenants/${id}/delete`, request).pipe(map(mapTenantStatusResult));
  }

  resendAdminInvitation(tenantId: string, adminUserId: string): Observable<ResendTenantAdminInvitationResult> {
    return this.http.post<ResendTenantAdminInvitationResult>(
      `${environment.apiUrl}/tenants/${tenantId}/users/${adminUserId}/resend-invitation`,
      null,
    );
  }

  revokeAdminInvitation(tenantId: string, adminUserId: string): Observable<void> {
    return this.http.delete<void>(`${environment.apiUrl}/tenants/${tenantId}/users/${adminUserId}/invitation`);
  }

  inviteReplacementTenantAdmin(
    tenantId: string,
    request: InviteReplacementTenantAdminRequest,
  ): Observable<InviteReplacementTenantAdminResult> {
    return this.http.post<InviteReplacementTenantAdminResult>(`${environment.apiUrl}/tenants/${tenantId}/admin-invitations`, request);
  }
}
