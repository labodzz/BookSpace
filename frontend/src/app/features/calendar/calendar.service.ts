import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { DateTime } from 'luxon';
import { Subject, catchError, finalize, of, switchMap, tap } from 'rxjs';
import { ApiError, toApiError } from '../../core/http/api-error';
import { BookingService } from '../bookings/booking.service';
import { OwnBooking } from '../bookings/booking.models';

interface CalendarRange {
  fromUtc: DateTime;
  toUtc: DateTime;
}

// The performance-critical piece of the calendar (see WP-7's "hard problem"): GET /bookings has no
// tenant-wide date-range endpoint, only the caller's own bookings - so this fetches ONLY the visible
// range via BookingService.getOwnBookingsInRange, caches each range by key so re-visiting a month is
// instant, and uses switchMap so rapid navigation (repeated "next month" clicks) always cancels the
// stale in-flight request instead of piling up parallel calls or racing to render an outdated range.
@Injectable({ providedIn: 'root' })
export class CalendarService {
  private readonly bookingService = inject(BookingService);
  private readonly cache = new Map<string, OwnBooking[]>();
  private readonly rangeRequests = new Subject<CalendarRange>();

  readonly bookings = signal<OwnBooking[]>([]);
  readonly loading = signal(false);
  readonly error = signal<ApiError | null>(null);

  constructor() {
    this.rangeRequests
      .pipe(
        switchMap((range) => {
          const key = CalendarService.rangeKey(range);
          const cached = this.cache.get(key);
          if (cached) {
            this.error.set(null);
            return of(cached);
          }

          this.loading.set(true);
          this.error.set(null);
          return this.bookingService.getOwnBookingsInRange(range.fromUtc.toUTC().toISO()!, range.toUtc.toUTC().toISO()!).pipe(
            tap((items) => this.cache.set(key, items)),
            catchError((err: unknown) => {
              this.error.set(err instanceof HttpErrorResponse ? toApiError(err) : { status: 0, title: 'Something went wrong.' });
              return of<OwnBooking[]>([]);
            }),
            finalize(() => this.loading.set(false)),
          );
        }),
      )
      .subscribe((items) => this.bookings.set(items));
  }

  loadRange(fromUtc: DateTime, toUtc: DateTime): void {
    this.rangeRequests.next({ fromUtc, toUtc });
  }

  // Called after a booking is created or cancelled so a subsequent loadRange for the affected range
  // re-fetches instead of silently continuing to serve stale cached data.
  invalidate(): void {
    this.cache.clear();
  }

  private static rangeKey(range: CalendarRange): string {
    return `${range.fromUtc.toUTC().toISO()}_${range.toUtc.toUTC().toISO()}`;
  }
}
