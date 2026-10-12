import { HttpClient, HttpContext } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import { environment } from '../../../environments/environment';
import { SKIP_GLOBAL_ERROR_TOAST } from '../../core/http/api-error';
import { ProposeBookingRequest, ProposeBookingResult } from './ai-booking.models';
import { ProposeBookingResultWire, mapProposeBookingResult } from './ai-booking.mappers';

@Injectable({ providedIn: 'root' })
export class AiBookingService {
  private readonly http = inject(HttpClient);

  // Skips the global error toast - AiAssistantService renders every outcome (including a provider
  // failure or a 429) inline as part of the assistant's own conversation, the same convention
  // BookingService.createBooking already uses for its own inline error handling.
  propose(request: ProposeBookingRequest): Observable<ProposeBookingResult> {
    const context = new HttpContext().set(SKIP_GLOBAL_ERROR_TOAST, true);
    return this.http
      .post<ProposeBookingResultWire>(`${environment.apiUrl}/ai-bookings/propose`, request, { context })
      .pipe(map(mapProposeBookingResult));
  }
}
