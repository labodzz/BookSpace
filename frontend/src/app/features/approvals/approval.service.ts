import { HttpClient, HttpContext } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { SKIP_GLOBAL_ERROR_TOAST } from '../../core/http/api-error';
import {
  ApproveBookingRequest,
  ApproveBookingResponse,
  BookingApprovalDecisionResponse,
  DecideBookingApprovalRequest,
  PendingApproval,
} from './approval.models';

@Injectable({ providedIn: 'root' })
export class ApprovalService {
  private readonly http = inject(HttpClient);

  getPendingApprovals(): Observable<PendingApproval[]> {
    return this.http.get<PendingApproval[]>(`${environment.apiUrl}/bookings/pending-approval`);
  }

  // Both decisions skip the global error toast - the approval queue renders a per-row failure message
  // inline instead (the row that failed is the one the approver needs the feedback next to).
  // approveRemainingSeries also approves every other still-Pending occurrence in the same series in this
  // one call - the API re-checks each sibling individually and never force-approves one that no longer
  // passes, reporting those back in the response instead.
  approve(bookingId: string, decisionNote?: string, approveRemainingSeries = false): Observable<ApproveBookingResponse> {
    const request: ApproveBookingRequest = { decisionNote: decisionNote || null, approveRemainingSeries };
    const context = new HttpContext().set(SKIP_GLOBAL_ERROR_TOAST, true);
    return this.http.post<ApproveBookingResponse>(`${environment.apiUrl}/bookings/${bookingId}/approve`, request, { context });
  }

  reject(bookingId: string, decisionNote?: string): Observable<BookingApprovalDecisionResponse> {
    const request: DecideBookingApprovalRequest = { decisionNote: decisionNote || null };
    const context = new HttpContext().set(SKIP_GLOBAL_ERROR_TOAST, true);
    return this.http.post<BookingApprovalDecisionResponse>(`${environment.apiUrl}/bookings/${bookingId}/reject`, request, { context });
  }
}
