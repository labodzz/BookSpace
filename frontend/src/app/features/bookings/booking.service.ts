import { HttpClient, HttpContext, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { EMPTY, Observable, expand, map, reduce } from 'rxjs';
import { environment } from '../../../environments/environment';
import { SKIP_GLOBAL_ERROR_TOAST } from '../../core/http/api-error';
import { PagedResult } from '../../core/http/paged-result';
import {
  CancelBookingResponse,
  CreateBookingRequest,
  CreateBookingResponse,
  CreateRecurringSeriesRequest,
  CreateRecurringSeriesResponse,
  OwnBooking,
  RangeBookingsResult,
} from './booking.models';
import {
  CancelBookingResponseWire,
  CreateBookingResponseWire,
  CreateRecurringSeriesResponseWire,
  OwnBookingWire,
  mapCancelBookingResponse,
  mapCreateBookingResponse,
  mapCreateRecurringSeriesResponse,
  mapOwnBooking,
  toRecurrenceFrequencyCode,
} from './booking.mappers';

// The largest page GetOwnBookingsQueryRequestValidator allows - used to keep both the calendar's
// range-bounded fetch and any other "get everything relevant" read to as few round trips as possible.
const MAX_PAGE_SIZE = 100;

// A hard ceiling on how many pages getOwnBookingsInRange will ever follow, so a pathological account
// can never turn one calendar navigation into an unbounded request chain - see CalendarService for why
// this matters. Set high enough (5,000 bookings) that hitting it in practice means something is
// genuinely pathological, not just a busy week - and when it IS hit, `truncated` tells the caller so
// the UI can say so, rather than the range silently rendering as if it were complete.
const MAX_RANGE_PAGES = 50;

@Injectable({ providedIn: 'root' })
export class BookingService {
  private readonly http = inject(HttpClient);

  getOwnBookings(page: number, pageSize: number, seriesId?: string): Observable<PagedResult<OwnBooking>> {
    let params = new HttpParams().set('page', page).set('pageSize', pageSize);
    if (seriesId) {
      params = params.set('seriesId', seriesId);
    }

    return this.http
      .get<PagedResult<OwnBookingWire>>(`${environment.apiUrl}/bookings`, { params })
      .pipe(map((result) => ({ ...result, items: result.items.map(mapOwnBooking) })));
  }

  // Fetches every one of the caller's own bookings overlapping [fromUtc, toUtc) - both already-materialized
  // recurring occurrences and one-off bookings come back as flat rows, so no client-side recurrence
  // expansion is needed. expand() follows subsequent pages only while more remain, bounded by
  // MAX_RANGE_PAGES so a request never turns into an unbounded chain for a pathological account.
  // `truncated` is true only when the cap itself was the reason pagination stopped (the server was
  // still reporting more results) - callers must surface that rather than presenting `items` as the
  // complete range.
  getOwnBookingsInRange(fromUtc: string, toUtc: string): Observable<RangeBookingsResult> {
    return this.fetchRangePage(fromUtc, toUtc, 1).pipe(
      expand((state) => (state.page < MAX_RANGE_PAGES && state.page * MAX_PAGE_SIZE < state.totalCount
        ? this.fetchRangePage(fromUtc, toUtc, state.page + 1)
        : EMPTY)),
      reduce<{ items: OwnBooking[]; page: number; totalCount: number }, RangeBookingsResult>(
        (acc, state) => ({
          items: [...acc.items, ...state.items],
          truncated: acc.truncated || (state.page === MAX_RANGE_PAGES && state.page * MAX_PAGE_SIZE < state.totalCount),
        }),
        { items: [], truncated: false },
      ),
    );
  }

  private fetchRangePage(
    fromUtc: string, toUtc: string, page: number,
  ): Observable<{ items: OwnBooking[]; page: number; totalCount: number }> {
    const params = new HttpParams().set('page', page).set('pageSize', MAX_PAGE_SIZE).set('fromUtc', fromUtc).set('toUtc', toUtc);

    return this.http
      .get<PagedResult<OwnBookingWire>>(`${environment.apiUrl}/bookings`, { params })
      .pipe(map((result) => ({ items: result.items.map(mapOwnBooking), page, totalCount: result.totalCount })));
  }

  // Both create calls skip the global error toast - the booking and recurring-booking forms render
  // field errors and conflicts inline (same convention AuthService.login uses for its own inline banner),
  // so a global toast on top would just duplicate the message.
  createBooking(request: CreateBookingRequest): Observable<CreateBookingResponse> {
    const context = new HttpContext().set(SKIP_GLOBAL_ERROR_TOAST, true);
    return this.http
      .post<CreateBookingResponseWire>(`${environment.apiUrl}/bookings`, request, { context })
      .pipe(map(mapCreateBookingResponse));
  }

  createRecurringSeries(request: CreateRecurringSeriesRequest): Observable<CreateRecurringSeriesResponse> {
    const context = new HttpContext().set(SKIP_GLOBAL_ERROR_TOAST, true);
    // The API deserializes enums as raw numbers (no JsonStringEnumConverter), so Frequency must be sent
    // as its numeric code, not the string label the rest of the app works with.
    const body = { ...request, frequency: toRecurrenceFrequencyCode(request.frequency) };
    return this.http
      .post<CreateRecurringSeriesResponseWire>(`${environment.apiUrl}/bookings/series`, body, { context })
      .pipe(map(mapCreateRecurringSeriesResponse));
  }

  cancelBooking(id: string, cancelRemainingSeries: boolean, reason?: string): Observable<CancelBookingResponse> {
    let params = new HttpParams().set('cancelRemainingSeries', cancelRemainingSeries);
    if (reason) {
      params = params.set('reason', reason);
    }

    return this.http
      .delete<CancelBookingResponseWire>(`${environment.apiUrl}/bookings/${id}`, { params })
      .pipe(map(mapCancelBookingResponse));
  }
}
