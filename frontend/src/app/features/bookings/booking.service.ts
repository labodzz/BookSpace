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

// The most pages getBookingsForSeries will ever follow. A series can never have more than 750
// occurrences (CreateRecurringSeriesCommandRequestValidator's cap, mirrored by MAX_OCCURRENCES in
// RecurringBookingFormComponent) - 8 pages of MAX_PAGE_SIZE covers 800, comfortably above that ceiling,
// so in practice this bound is never actually why a series' occurrence list comes back incomplete.
const MAX_SERIES_PAGES = 8;

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

  // Every occurrence belonging to one recurring series, in a single call site - used by My Bookings to
  // expand a collapsed series group to its full occurrence list regardless of which page(s) of the plain
  // getOwnBookings listing its individual occurrences happen to fall on. Same follow-all-pages shape as
  // getOwnBookingsInRange, bounded by MAX_SERIES_PAGES instead of MAX_RANGE_PAGES.
  getBookingsForSeries(seriesId: string): Observable<OwnBooking[]> {
    return this.fetchSeriesPage(seriesId, 1).pipe(
      expand((state) => (state.page < MAX_SERIES_PAGES && state.page * MAX_PAGE_SIZE < state.totalCount
        ? this.fetchSeriesPage(seriesId, state.page + 1)
        : EMPTY)),
      reduce<{ items: OwnBooking[]; page: number; totalCount: number }, OwnBooking[]>(
        (acc, state) => [...acc, ...state.items],
        [],
      ),
    );
  }

  private fetchSeriesPage(
    seriesId: string, page: number,
  ): Observable<{ items: OwnBooking[]; page: number; totalCount: number }> {
    const params = new HttpParams().set('page', page).set('pageSize', MAX_PAGE_SIZE).set('seriesId', seriesId);

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
