import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { DateTime } from 'luxon';
import { environment } from '../../../environments/environment';
import { OwnBookingWire } from '../bookings/booking.mappers';
import { CalendarService } from './calendar.service';

// The API serializes BookingStatus as its raw numeric code (no JsonStringEnumConverter) - 1 is
// Confirmed. This represents the wire shape flush() sends, not the mapped OwnBooking the service exposes.
function booking(id: string, startUtc: string): OwnBookingWire {
  return {
    id,
    resourceId: 'resource-1',
    startUtc,
    endUtc: startUtc,
    quantity: 1,
    status: 1,
    cancelledAtUtc: null,
    cancelledByAdmin: false,
    cancellationReason: null,
    seriesId: null,
    timeZoneId: 'UTC',
  };
}

const BOOKINGS_URL = `${environment.apiUrl}/bookings`;

// The calendar's core performance requirement: only the visible range is ever requested, a re-visited
// range is served from cache, and rapid navigation cancels the stale in-flight request instead of
// letting it race the newer one to render.
describe('CalendarService', () => {
  let service: CalendarService;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(CalendarService);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpTesting.verify());

  it('fetches bookings for the requested range and exposes them', () => {
    const from = DateTime.fromISO('2026-09-01T00:00:00Z', { zone: 'utc' });
    const to = DateTime.fromISO('2026-09-08T00:00:00Z', { zone: 'utc' });

    service.loadRange(from, to);

    httpTesting.expectOne((r) => r.url === BOOKINGS_URL).flush({
      items: [booking('booking-1', '2026-09-02T10:00:00Z')],
      page: 1,
      pageSize: 100,
      totalCount: 1,
    });

    expect(service.bookings().map((b) => b.id)).toEqual(['booking-1']);
    expect(service.loading()).toBe(false);
    expect(service.error()).toBeNull();
  });

  it('serves an already-fetched range from cache without issuing a second HTTP request', () => {
    const from = DateTime.fromISO('2026-09-01T00:00:00Z', { zone: 'utc' });
    const to = DateTime.fromISO('2026-09-08T00:00:00Z', { zone: 'utc' });

    service.loadRange(from, to);
    httpTesting.expectOne((r) => r.url === BOOKINGS_URL).flush({ items: [], page: 1, pageSize: 100, totalCount: 0 });

    service.loadRange(from, to);

    httpTesting.expectNone((r) => r.url === BOOKINGS_URL);
  });

  it('re-fetches for a genuinely different range', () => {
    const septFrom = DateTime.fromISO('2026-09-01T00:00:00Z', { zone: 'utc' });
    const septTo = DateTime.fromISO('2026-09-08T00:00:00Z', { zone: 'utc' });
    const octFrom = DateTime.fromISO('2026-10-01T00:00:00Z', { zone: 'utc' });
    const octTo = DateTime.fromISO('2026-10-08T00:00:00Z', { zone: 'utc' });

    service.loadRange(septFrom, septTo);
    httpTesting.expectOne((r) => r.url === BOOKINGS_URL).flush({ items: [], page: 1, pageSize: 100, totalCount: 0 });

    service.loadRange(octFrom, octTo);
    httpTesting.expectOne((r) => r.url === BOOKINGS_URL).flush({ items: [booking('booking-2', '2026-10-02T10:00:00Z')], page: 1, pageSize: 100, totalCount: 1 });

    expect(service.bookings().map((b) => b.id)).toEqual(['booking-2']);
  });

  it('cancels a stale in-flight request when the visible range changes before it resolves', () => {
    const septFrom = DateTime.fromISO('2026-09-01T00:00:00Z', { zone: 'utc' });
    const septTo = DateTime.fromISO('2026-09-08T00:00:00Z', { zone: 'utc' });
    const octFrom = DateTime.fromISO('2026-10-01T00:00:00Z', { zone: 'utc' });
    const octTo = DateTime.fromISO('2026-10-08T00:00:00Z', { zone: 'utc' });

    service.loadRange(septFrom, septTo);
    const staleRequest = httpTesting.expectOne((r) => r.url === BOOKINGS_URL);

    // Navigating away before the first request resolves must cancel it (switchMap), not let both
    // requests race to decide what the calendar ends up showing.
    service.loadRange(octFrom, octTo);
    expect(staleRequest.cancelled).toBe(true);

    httpTesting.expectOne((r) => r.url === BOOKINGS_URL).flush({ items: [booking('booking-3', '2026-10-02T10:00:00Z')], page: 1, pageSize: 100, totalCount: 1 });

    expect(service.bookings().map((b) => b.id)).toEqual(['booking-3']);
  });

  it('exposes truncated results rather than silently presenting them as the complete range', () => {
    const from = DateTime.fromISO('2026-09-01T00:00:00Z', { zone: 'utc' });
    const to = DateTime.fromISO('2026-09-08T00:00:00Z', { zone: 'utc' });

    service.loadRange(from, to);
    for (let page = 1; page <= 50; page++) {
      httpTesting
        .expectOne((r) => r.url === BOOKINGS_URL && r.params.get('page') === String(page))
        .flush({
          items: Array.from({ length: 100 }, (_, i) => booking(`booking-${(page - 1) * 100 + i}`, '2026-09-02T10:00:00Z')),
          page,
          pageSize: 100,
          totalCount: 10_000,
        });
    }

    expect(service.bookings()).toHaveLength(5000);
    expect(service.truncated()).toBe(true);
  });

  it('clears its cache on invalidate() so the next loadRange for the same range re-fetches', () => {
    const from = DateTime.fromISO('2026-09-01T00:00:00Z', { zone: 'utc' });
    const to = DateTime.fromISO('2026-09-08T00:00:00Z', { zone: 'utc' });

    service.loadRange(from, to);
    httpTesting.expectOne((r) => r.url === BOOKINGS_URL).flush({ items: [booking('booking-1', '2026-09-02T10:00:00Z')], page: 1, pageSize: 100, totalCount: 1 });
    expect(service.bookings().map((b) => b.id)).toEqual(['booking-1']);

    // Called after a booking is created/cancelled/approved for this range - a subsequent loadRange must
    // not keep serving the now-stale cached result.
    service.invalidate();
    service.loadRange(from, to);

    httpTesting.expectOne((r) => r.url === BOOKINGS_URL).flush({
      items: [booking('booking-1', '2026-09-02T10:00:00Z'), booking('booking-2', '2026-09-03T10:00:00Z')],
      page: 1,
      pageSize: 100,
      totalCount: 2,
    });
    expect(service.bookings().map((b) => b.id)).toEqual(['booking-1', 'booking-2']);
  });

  it('surfaces a failed request as an error and clears it once a subsequent range succeeds', () => {
    const from = DateTime.fromISO('2026-09-01T00:00:00Z', { zone: 'utc' });
    const to = DateTime.fromISO('2026-09-08T00:00:00Z', { zone: 'utc' });
    const octFrom = DateTime.fromISO('2026-10-01T00:00:00Z', { zone: 'utc' });
    const octTo = DateTime.fromISO('2026-10-08T00:00:00Z', { zone: 'utc' });

    service.loadRange(from, to);
    httpTesting.expectOne((r) => r.url === BOOKINGS_URL).flush('server error', { status: 500, statusText: 'Internal Server Error' });

    expect(service.error()).not.toBeNull();
    expect(service.bookings()).toEqual([]);

    service.loadRange(octFrom, octTo);
    httpTesting.expectOne((r) => r.url === BOOKINGS_URL).flush({ items: [], page: 1, pageSize: 100, totalCount: 0 });

    expect(service.error()).toBeNull();
  });
});
